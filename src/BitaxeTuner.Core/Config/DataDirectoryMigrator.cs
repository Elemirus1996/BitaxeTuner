using System.Security.Cryptography;
using System.Text.Json;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// Zieht den Datenordner um: kopieren → prüfen → umschalten. Das Original bleibt vollständig erhalten,
/// im Ziel wird nie etwas überschrieben. Umgeschaltet wird erst, wenn alle Prüfungen bestanden sind.
/// </summary>
public static class DataDirectoryMigrator
{
    public sealed record Result(bool Success, string Message, IReadOnlyList<string> Log);

    /// <summary>Dateien und Ordner, die umziehen (Sicherungsordner bleiben im alten Ordner).</summary>
    private static IEnumerable<string> FilesToCopy(string source) =>
        Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(source, f);
                var name = Path.GetFileName(f);
                if (rel.StartsWith("backup-", StringComparison.OrdinalIgnoreCase)) return false;
                if (Path.GetDirectoryName(rel) is "" or null)
                {
                    if (name.StartsWith("history.db", StringComparison.OrdinalIgnoreCase)) return false; // eigene Behandlung
                    if (name.Equals(DataPaths.BootstrapFileName, StringComparison.OrdinalIgnoreCase)) return false;
                    if (name.Equals("UMGEZOGEN.txt", StringComparison.OrdinalIgnoreCase)) return false;
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            });

    /// <param name="history">Geöffnete Verlaufsdatenbank (wird über die Backup-API kopiert). Polling vorher anhalten.</param>
    public static Result Migrate(string sourceDirectory, string targetDirectory, HistoryStore? history, string bootstrapFile)
    {
        var log = new List<string>();
        if (Environment.GetEnvironmentVariable(DataPaths.EnvironmentVariable) is { Length: > 0 })
            return new Result(false, $"Umzug nicht möglich, solange {DataPaths.EnvironmentVariable} gesetzt ist.", log);

        var src = Path.GetFullPath(sourceDirectory).TrimEnd('\\');
        var dst = Path.GetFullPath(targetDirectory).TrimEnd('\\');
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
            return new Result(false, "Quelle und Ziel sind identisch.", log);
        if (dst.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase))
            return new Result(false, "Das Ziel darf nicht innerhalb des aktuellen Datenordners liegen.", log);

        var files = FilesToCopy(src).ToList();
        var targets = files.Select(f => Path.Combine(dst, Path.GetRelativePath(src, f))).ToList();
        var dbTarget = Path.Combine(dst, "history.db");

        // 1. Kollisionen prüfen – nie überschreiben
        var conflicts = targets.Where(File.Exists).ToList();
        if (File.Exists(dbTarget) || File.Exists(dbTarget + "-wal")) conflicts.Add(dbTarget);
        if (conflicts.Count > 0)
            return new Result(false, "Im Ziel liegen bereits gleichnamige Dateien – nichts wurde verändert:\n" +
                                     string.Join("\n", conflicts.Take(10)), log);

        try
        {
            // 2. Kopieren
            Directory.CreateDirectory(dst);
            for (var i = 0; i < files.Count; i++)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targets[i])!);
                File.Copy(files[i], targets[i], overwrite: false);
                log.Add($"Kopiert: {Path.GetRelativePath(src, files[i])}");
            }

            Dictionary<string, long>? expectedRows = null;
            if (history is not null)
            {
                expectedRows = history.CountRows();
                history.BackupTo(dbTarget);
                log.Add("history.db über die SQLite-Backup-API kopiert");
            }

            // 3. Prüfen
            for (var i = 0; i < files.Count; i++)
            {
                if (!HashEquals(files[i], targets[i]))
                    return new Result(false, $"Prüfsumme weicht ab: {targets[i]}. Es wurde nicht umgeschaltet.", log);
            }
            log.Add($"{files.Count} Datei(en) per SHA-256 geprüft");

            if (expectedRows is not null)
            {
                var (ok, rows) = HistoryStore.Verify(dbTarget);
                if (!ok) return new Result(false, "integrity_check der kopierten history.db fehlgeschlagen. Es wurde nicht umgeschaltet.", log);
                foreach (var (table, count) in expectedRows)
                {
                    if (!rows.TryGetValue(table, out var copied) || copied != count)
                        return new Result(false, $"Zeilenzahl in {table} weicht ab ({count} → {copied}). Es wurde nicht umgeschaltet.", log);
                }
                log.Add("history.db: integrity_check ok, Zeilenzahlen identisch");
            }

            // 4. Umschalten
            Directory.CreateDirectory(Path.GetDirectoryName(bootstrapFile)!);
            var tmp = bootstrapFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new { DataDirectory = dst }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, bootstrapFile, overwrite: true);
            log.Add($"Datenordner umgeschaltet: {bootstrapFile}");

            File.WriteAllText(Path.Combine(src, "UMGEZOGEN.txt"),
                $"Die Daten wurden am {DateTime.Now:dd.MM.yyyy HH:mm} nach {dst} kopiert und dort weiterverwendet.\r\n" +
                "Dieser Ordner wurde nicht verändert und kann als Sicherung behalten werden.\r\n");
            return new Result(true, $"Umzug nach {dst} abgeschlossen. Die App startet jetzt neu.", log);
        }
        catch (Exception ex)
        {
            return new Result(false, $"Umzug abgebrochen: {ex.Message}. Es wurde nicht umgeschaltet, der alte Ordner ist unverändert.", log);
        }
    }

    private static bool HashEquals(string a, string b)
    {
        using var sha = SHA256.Create();
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return sha.ComputeHash(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }
}
