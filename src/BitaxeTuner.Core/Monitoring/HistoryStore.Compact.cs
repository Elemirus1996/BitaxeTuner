using BitaxeTuner.Core.I18n;
using Microsoft.Data.Sqlite;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>Ergebnis der einmaligen Umstellung beim Öffnen (null = nichts zu tun).</summary>
/// <param name="Skipped">Grund, warum nicht umgestellt wurde (wird beim nächsten Start erneut versucht).</param>
public sealed record HistoryCompaction(long BeforeBytes, long AfterBytes, string? Skipped = null);

/// <summary>
/// 0.9.16: history.db klein halten. SQLite gibt den Platz gelöschter Zeilen (z. B. Miner-Logs nach der Aufbewahrungszeit)
/// sonst nie an den Datenträger zurück – auf dem Pi lagen so über 300 MB leere Seiten in der Datei.
///
/// Einmalig beim Öffnen (vor dem Start des Hubs – Abfragen und Lüfterbefehle laufen noch nicht) wird die Datei per
/// „VACUUM INTO“ kompakt neu geschrieben und dabei auf auto_vacuum=INCREMENTAL umgestellt. Die neue Datei wird geprüft
/// (quick_check, gleiche Zeilenzahl je Tabelle), erst dann gegen die alte getauscht; bei jedem Fehler bleibt die alte
/// unverändert. Danach gibt <see cref="ReleaseFreeSpace"/> leere Seiten nach jedem Aufräumen in kleinen Schritten zurück.
/// </summary>
public sealed partial class HistoryStore
{
    /// <summary>Wird während der Umstellung etwa alle 20 s aufgerufen (Server: systemd mehr Startzeit melden).</summary>
    public static Action? KeepAlive { get; set; }

    /// <summary>Ergebnis der Umstellung beim Öffnen – der Hub schreibt es ins Protokoll.</summary>
    public HistoryCompaction? Compaction { get; private set; }

    private const long SpaceMargin = 64L * 1024 * 1024;

    private static long PragmaLong(SqliteConnection c, string pragma)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Freier Platz auf dem Datenträger des Ordners (null = unbekannt).</summary>
    private static long? AvailableBytes(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, cmp))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault()?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static HistoryCompaction? CompactOnOpen(string file)
    {
        if (!File.Exists(file)) return null;
        var target = file + ".compact";
        var before = new FileInfo(file).Length;
        try
        {
            if (File.Exists(target)) File.Delete(target);   // Rest eines abgebrochenen Versuchs
            Dictionary<string, long> counts;
            using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                c.Open();
                if (PragmaLong(c, "auto_vacuum") == 2) return null;
                var pageSize = PragmaLong(c, "page_size");
                var used = (PragmaLong(c, "page_count") - PragmaLong(c, "freelist_count")) * pageSize;
                if (AvailableBytes(Path.GetDirectoryName(file)!) is { } free && free < used + SpaceMargin)
                    return new HistoryCompaction(before, before,
                        L.T("zu wenig freier Speicher ({0} MB nötig)", ((used + SpaceMargin) / 1048576).ToString(L.Culture)));
                Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
                Exec(c, "PRAGMA auto_vacuum=INCREMENTAL;");
                static void Ping() { try { KeepAlive?.Invoke(); } catch { /* nur Hinweis */ } }
                Ping();
                using (new Timer(_ => Ping(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20)))
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "VACUUM INTO $target";
                    cmd.Parameters.AddWithValue("$target", target);
                    cmd.CommandTimeout = 0;
                    cmd.ExecuteNonQuery();
                }
                counts = CountRows(c);
            }

            // Neue Datei prüfen, bevor sie die alte ersetzt
            using (var t = new SqliteConnection($"Data Source={target};Pooling=False"))
            {
                t.Open();
                if (PragmaLong(t, "auto_vacuum") != 2) Exec(t, "PRAGMA auto_vacuum=INCREMENTAL; VACUUM;");
                using var check = t.CreateCommand();
                check.CommandText = "PRAGMA quick_check;";
                var ok = check.ExecuteScalar() as string == "ok" && PragmaLong(t, "auto_vacuum") == 2;
                var copied = CountRows(t);
                if (!ok || copied.Count != counts.Count || counts.Any(kv => !copied.TryGetValue(kv.Key, out var n) || n != kv.Value))
                    throw new IOException(L.T("Prüfung der verkleinerten Datei fehlgeschlagen"));
            }

            // Tauschen: alte Datei erst weg, wenn die neue an ihrem Platz liegt
            var wal = file + "-wal";
            if (File.Exists(wal) && new FileInfo(wal).Length > 0)
                throw new IOException(L.T("Datenbank ist noch in Benutzung"));
            foreach (var extra in new[] { wal, file + "-shm" }) if (File.Exists(extra)) File.Delete(extra);
            var old = file + ".pre-compact";
            File.Move(file, old, overwrite: true);
            try { File.Move(target, file); }
            catch
            {
                File.Move(old, file, overwrite: true);
                throw;
            }
            File.Delete(old);
            return new HistoryCompaction(before, new FileInfo(file).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            try { if (File.Exists(target)) File.Delete(target); } catch { /* beim nächsten Start */ }
            return new HistoryCompaction(before, before, ex.Message);
        }
    }

    /// <summary>Leere Seiten an den Datenträger zurückgeben (nach dem Aufräumen). Liefert die freigegebenen Bytes.</summary>
    public long ReleaseFreeSpace()
    {
        lock (_lock)
        {
            if (PragmaLong(_db, "auto_vacuum") != 2) return 0;
            var pageSize = PragmaLong(_db, "page_size");
            var before = PragmaLong(_db, "freelist_count");
            if (before == 0) return 0;
            Exec(_db, "PRAGMA incremental_vacuum;");
            Exec(_db, "PRAGMA wal_checkpoint(TRUNCATE);");
            return (before - PragmaLong(_db, "freelist_count")) * pageSize;
        }
    }

    /// <summary>Leerer Platz in der Datei und ob er laufend freigegeben wird.</summary>
    public (long FreeBytes, bool Incremental) SpaceInfo()
    {
        lock (_lock)
            return (PragmaLong(_db, "freelist_count") * PragmaLong(_db, "page_size"), PragmaLong(_db, "auto_vacuum") == 2);
    }
}
