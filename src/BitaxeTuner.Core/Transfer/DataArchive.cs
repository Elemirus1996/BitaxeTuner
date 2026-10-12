using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using Microsoft.Data.Sqlite;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Transfer;

public sealed class ArchiveFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class ArchiveManifest
{
    public int Format { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>"desktop" oder "server".</summary>
    public string Source { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public List<ArchiveFile> Files { get; set; } = [];
    /// <summary>Zeilen je Tabelle in history.db (Gegenprobe nach dem Entpacken).</summary>
    public Dictionary<string, long> HistoryRows { get; set; } = [];
    public int Devices { get; set; }
}

/// <summary>
/// Datenübertragung Desktop ↔ Server: config.json, history.db (SQLite-Backup-API, WAL-sicher), tax\, tuning\, snapshots\
/// als ZIP mit Manifest (SHA-256 je Datei, Zeilenzahlen). Beim Entpacken wird alles geprüft; übernommen wird erst,
/// wenn Prüfsummen und integrity_check stimmen – und nur nach einer Sicherung des bisherigen Datenordners.
/// Zugangsdaten des Servers, Zertifikat, Sicherungen und datadir.json werden nie übertragen.
/// </summary>
public static class DataArchive
{
    public const string ManifestName = "manifest.json";
    private const string DataPrefix = "data/";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Bereiche, die eine Übernahme ersetzt (alles andere im Zielordner bleibt unberührt).</summary>
    public static readonly string[] ReplacedFiles = ["config.json", "history.db", "history.db-wal", "history.db-shm"];
    public static readonly string[] ReplacedDirectories = ["tax", "tuning", "snapshots", Host.MinerHub.PictureDirectory];

    public static bool IsTransferable(string relative)
    {
        var p = relative.Replace('\\', '/');
        var top = p.Split('/')[0];
        if (p.Contains('/')) return ReplacedDirectories.Contains(top, StringComparer.OrdinalIgnoreCase) && !p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        return p.Equals("config.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 0.9.16: Reste abgebrochener Exporte („.export-…“ als Datei oder Ordner, z. B. nach einem Neustart mitten im Herunterladen)
    /// löschen – sie belegen Platz und können eine config.json mit aufgelösten Zugangsdaten enthalten. Nur Einträge, die älter
    /// als <paramref name="minAge"/> sind (laufende Exporte bleiben). Liefert die Zahl der gelöschten Einträge und Bytes.
    /// </summary>
    public static (int Count, long Bytes) CleanupStaleExports(string dataDirectory, TimeSpan minAge)
    {
        if (!Directory.Exists(dataDirectory)) return (0, 0);
        var cutoff = DateTime.UtcNow - minAge;
        int count = 0;
        long bytes = 0;
        foreach (var entry in new DirectoryInfo(dataDirectory).EnumerateFileSystemInfos(".export-*"))
        {
            try
            {
                if (entry.LastWriteTimeUtc > cutoff) continue;
                if (entry is DirectoryInfo d)
                {
                    bytes += d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    d.Delete(recursive: true);
                }
                else
                {
                    bytes += ((FileInfo)entry).Length;
                    entry.Delete();
                }
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* gerade in Benutzung – beim nächsten Mal */ }
        }
        return (count, bytes);
    }

    /// <summary>Archiv erstellen. Ist history.db im Prozess geöffnet, <paramref name="openHistory"/> übergeben (Backup über dieselbe Verbindung).</summary>
    /// <param name="source">„backup“ = Sicherung: Tokens bleiben als Verweis (Werte nur in secrets.json, nicht im Archiv).
    /// Sonst Übertragung (Desktop ↔ Server, Pi-Einrichtung): config.json mit aufgelösten Tokens, damit sie mitkommen.</param>
    public static ArchiveManifest Create(string dataDirectory, HistoryStore? openHistory, Stream output, string source, string appVersion)
    {
        var manifest = new ArchiveManifest { Source = source, AppVersion = appVersion };
        // Audit N-Sec5: config.json mit aufgelösten Tokens nur im (geschützten) Datenordner zwischenlagern, nicht in %TEMP%
        // bzw. C:\Windows\Temp beim Windows-Dienst. Der Ordner selbst wird nicht mit ins Archiv gepackt (IsTransferable).
        var temp = Path.Combine(dataDirectory, ".export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).ToList())
            {
                var relative = Path.GetRelativePath(dataDirectory, file).Replace('\\', '/');
                if (!IsTransferable(relative)) continue;
                if (relative.Equals("config.json", StringComparison.OrdinalIgnoreCase) && source != "backup")
                {
                    var resolved = Path.Combine(temp, "config.json");
                    ConfigSecrets.ResolveFile(file, resolved);
                    Add(zip, manifest, relative, resolved);
                    continue;
                }
                Add(zip, manifest, relative, file);
            }

            var db = Path.Combine(dataDirectory, "history.db");
            var copy = Path.Combine(temp, "history.db");
            if (openHistory is not null) openHistory.BackupTo(copy);
            else if (File.Exists(db)) BackupFile(db, copy);
            if (File.Exists(copy))
            {
                // Sicherungen: frisch per VACUUM INTO neu aufgebaute Kopie – quick_check genügt (0.9.15, Tempo auf dem Pi)
                var (ok, rows) = HistoryStore.Verify(copy, quick: source == "backup");
                if (!ok) throw new IOException(L.T("history.db ist nicht konsistent (integrity_check) – Übertragung abgebrochen."));
                manifest.HistoryRows = rows;
                Add(zip, manifest, "history.db", copy);
            }

            var configFile = Path.Combine(dataDirectory, "config.json");
            if (File.Exists(configFile)) manifest.Devices = AppConfig.Load(configFile).Devices.Count;

            var entry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using (var s = entry.Open()) JsonSerializer.Serialize(s, manifest, Json);
            return manifest;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(temp, true); } catch { /* egal */ }
        }
    }

    private static void Add(ZipArchive zip, ArchiveManifest manifest, string relative, string file)
    {
        // Große Dateien (history.db) schnell packen: „Optimal“ kostet auf einem Pi ein Vielfaches an CPU-Zeit für wenige Prozent
        var level = new FileInfo(file).Length > 16L * 1024 * 1024 ? CompressionLevel.Fastest : CompressionLevel.Optimal;
        var entry = zip.CreateEntry(DataPrefix + relative, level);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var output = entry.Open())
        {
            var buffer = new byte[81920];
            int read;
            long size = 0;
            while ((read = input.Read(buffer)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
                size += read;
            }
            manifest.Files.Add(new ArchiveFile { Path = relative, Size = size, Sha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() });
        }
    }

    private static void BackupFile(string source, string target)
    {
        using var src = new SqliteConnection($"Data Source={source};Pooling=False");
        using var dst = new SqliteConnection($"Data Source={target};Pooling=False");
        src.Open();
        dst.Open();
        src.BackupDatabase(dst);
    }

    /// <summary>
    /// 0.9.15: Archiv prüfen, ohne es zu entpacken – jede Datei wird beim Lesen gegen Größe und SHA-256 aus dem Manifest
    /// geprüft, dazu Vollständigkeit und erlaubte Pfade. Für frisch erstellte Sicherungen: history.db wurde vor dem Packen
    /// geprüft, die Prüfsumme belegt, dass genau diese Bytes im Archiv stehen. Spart auf dem Pi das Schreiben und erneute
    /// Prüfen der ganzen Datenbank. Wirft <see cref="InvalidDataException"/> bei jeder Abweichung.
    /// </summary>
    public static ArchiveManifest VerifyArchive(Stream archive)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException(L.T("Kein BitaxeTuner-Datenarchiv (manifest.json fehlt)."));
        ArchiveManifest manifest;
        using (var s = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<ArchiveManifest>(s) ?? throw new InvalidDataException(L.T("manifest.json ist leer."));
        if (manifest.Format != 1) throw new InvalidDataException(L.T("Archivformat {0} wird nicht unterstützt – bitte beide Seiten aktualisieren.", manifest.Format));

        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == ManifestName || entry.FullName.EndsWith('/')) continue;
            if (!entry.FullName.StartsWith(DataPrefix, StringComparison.Ordinal))
                throw new InvalidDataException(L.T("Unerwarteter Eintrag im Archiv: {0}", entry.FullName));
            var relative = entry.FullName[DataPrefix.Length..];
            if (!expected.TryGetValue(relative, out var info)) throw new InvalidDataException(L.T("Datei nicht im Manifest: {0}", relative));
            if (relative != "history.db" && !IsTransferable(relative)) throw new InvalidDataException(L.T("Datei darf nicht übertragen werden: {0}", relative));
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes = 0;
            using (var input = entry.Open())
            {
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    bytes += read;
                }
            }
            if (bytes != info.Size || Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() != info.Sha256)
                throw new InvalidDataException(L.T("Prüfsumme stimmt nicht: {0}", relative));
            seen.Add(relative);
        }
        var missing = expected.Keys.Where(k => !seen.Contains(k)).ToList();
        if (missing.Count > 0) throw new InvalidDataException(L.T("Im Archiv fehlen: ") + string.Join(", ", missing));
        return manifest;
    }

    /// <summary>
    /// Archiv in einen leeren Bereitstellungsordner entpacken und vollständig prüfen (Pfade, Prüfsummen,
    /// Vollständigkeit, integrity_check, Zeilenzahlen). Wirft <see cref="InvalidDataException"/> bei jeder Abweichung.
    /// </summary>
    public static ArchiveManifest ExtractAndVerify(Stream archive, string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory) && Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
            throw new IOException(L.T("Bereitstellungsordner ist nicht leer."));
        Directory.CreateDirectory(stagingDirectory);
        var root = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;

        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException(L.T("Kein BitaxeTuner-Datenarchiv (manifest.json fehlt)."));
        ArchiveManifest manifest;
        using (var s = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<ArchiveManifest>(s) ?? throw new InvalidDataException(L.T("manifest.json ist leer."));
        if (manifest.Format != 1) throw new InvalidDataException(L.T("Archivformat {0} wird nicht unterstützt – bitte beide Seiten aktualisieren.", manifest.Format));

        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == ManifestName || entry.FullName.EndsWith('/')) continue;
            if (!entry.FullName.StartsWith(DataPrefix, StringComparison.Ordinal))
                throw new InvalidDataException(L.T("Unerwarteter Eintrag im Archiv: {0}", entry.FullName));
            var relative = entry.FullName[DataPrefix.Length..];
            if (!expected.TryGetValue(relative, out var info)) throw new InvalidDataException(L.T("Datei nicht im Manifest: {0}", relative));
            if (relative != "history.db" && !IsTransferable(relative)) throw new InvalidDataException(L.T("Datei darf nicht übertragen werden: {0}", relative));
            var target = Path.GetFullPath(Path.Combine(stagingDirectory, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.T("Ungültiger Pfad im Archiv: {0}", relative));

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open())
            using (var output = File.Create(target))
                input.CopyTo(output);
            var bytes = new FileInfo(target).Length;
            var hash = Hash(target);
            if (bytes != info.Size || hash != info.Sha256) throw new InvalidDataException(L.T("Prüfsumme stimmt nicht: {0}", relative));
            seen.Add(relative);
        }
        var missing = expected.Keys.Where(k => !seen.Contains(k)).ToList();
        if (missing.Count > 0) throw new InvalidDataException(L.T("Im Archiv fehlen: ") + string.Join(", ", missing));

        var db = Path.Combine(stagingDirectory, "history.db");
        if (File.Exists(db))
        {
            var (ok, rows) = HistoryStore.Verify(db);
            SqliteConnection.ClearAllPools();
            if (!ok) throw new InvalidDataException(L.T("history.db im Archiv ist beschädigt (integrity_check)."));
            foreach (var (table, count) in manifest.HistoryRows)
                if (rows.GetValueOrDefault(table) != count) throw new InvalidDataException(L.T("history.db: Zeilenzahl in {0} weicht ab.", table));
        }
        return manifest;
    }

    public static string Hash(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>
    /// Geprüfte Daten übernehmen: zuerst Sicherung des Zielordners (backup-…), dann die übertragenen Bereiche ersetzen.
    /// history.db darf dabei in keinem Prozess geöffnet sein. <paramref name="merge"/> übernimmt ortsgebundene Einstellungen
    /// aus der bisherigen config.json (z. B. Design, Serververbindung der Desktop-App).
    /// </summary>
    public static string Apply(string stagingDirectory, string dataDirectory, Action<AppConfig, AppConfig>? merge = null)
    {
        Directory.CreateDirectory(dataDirectory);
        var oldConfigFile = Path.Combine(dataDirectory, "config.json");
        var oldConfig = File.Exists(oldConfigFile) ? AppConfig.Load(oldConfigFile) : null;
        var backup = ConfigMigrator.BackupDataDirectory(dataDirectory);

        foreach (var name in ReplacedFiles)
        {
            var f = Path.Combine(dataDirectory, name);
            if (File.Exists(f)) File.Delete(f);
        }
        foreach (var dir in ReplacedDirectories)
        {
            var d = Path.Combine(dataDirectory, dir);
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }
        foreach (var file in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(dataDirectory, Path.GetRelativePath(stagingDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: false);
        }

        if (oldConfig is not null && merge is not null && File.Exists(oldConfigFile))
        {
            var fresh = AppConfig.Load(oldConfigFile);
            merge(oldConfig, fresh);
            fresh.Save(oldConfigFile);
        }
        return backup;
    }
}
