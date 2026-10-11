using System.Text.RegularExpressions;
using BitaxeTuner.Core.Backup;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>Vorheriger Stand des Datenordners (angelegt beim Einspielen einer Sicherung bzw. vor Schemaänderungen).</summary>
public sealed record PreviousState(string Name, DateTime Time, long Bytes);

/// <summary>Speicherplatz des Datenträgers mit dem Datenordner und was BitaxeTuner davon belegt.</summary>
/// <param name="TotalBytes">Größe des Datenträgers (null = nicht ermittelbar).</param>
/// <param name="FreeBytes">Freier Platz (für den Dienst nutzbar).</param>
public sealed record StorageInfo(string Drive, long? TotalBytes, long? FreeBytes, long DataBytes, long HistoryBytes,
    long BackupsBytes, int BackupCount, long PreviousBytes, IReadOnlyList<PreviousState> Previous);

/// <summary>
/// 0.9.15: Speicherübersicht und Aufräumen. Sicherungen im Datenordner und vorherige Stände („backup-…“-Ordner) lassen sich
/// einzeln löschen – nur nach Bestätigung in der Oberfläche, jeder Löschvorgang steht im Protokoll.
/// </summary>
public sealed partial class MinerHub
{
    private static readonly Regex PreviousStatePattern = new(@"^backup-\d{8}-\d{6}(-\d+)?$", RegexOptions.CultureInvariant);

    /// <summary>Unter dieser Grenze warnt die Übersicht (500 MB oder 5 % des Datenträgers, das Kleinere).</summary>
    public static bool StorageLow(StorageInfo s) =>
        s.FreeBytes is { } free && s.TotalBytes is { } total && free < Math.Min(500L * 1024 * 1024, total / 20);

    public IReadOnlyList<PreviousState> PreviousStates()
    {
        if (!Directory.Exists(DataDirectory)) return [];
        return new DirectoryInfo(DataDirectory).EnumerateDirectories("backup-*")
            .Where(d => PreviousStatePattern.IsMatch(d.Name))
            .Select(d => new PreviousState(d.Name, d.CreationTime, DirectorySize(d)))
            .OrderByDescending(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    public StorageInfo Storage()
    {
        var full = Path.GetFullPath(DataDirectory);
        DriveInfo? drive = null;
        try
        {
            // Datenträger mit dem längsten passenden Einhängepunkt (Linux: /, /mnt/ssd …; Windows: C:\ …)
            drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ohne Datenträgerangaben */ }

        var previous = PreviousStates();
        var backups = LocalBackups();
        var history = History?.FilePath is { } h ? FileSize(h) + FileSize(h + "-wal") + FileSize(h + "-shm") : 0;
        long? total = null, free = null;
        try { if (drive is not null) { total = drive.TotalSize; free = drive.AvailableFreeSpace; } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unbekannt */ }
        return new StorageInfo(drive?.Name ?? Path.GetPathRoot(full) ?? full, total, free,
            Directory.Exists(full) ? DirectorySize(new DirectoryInfo(full)) : 0, history,
            backups.Sum(f => f.Length), backups.Count, previous.Sum(p => p.Bytes), previous);
    }

    /// <summary>Sicherung im Datenordner löschen (nur Dateien mit dem Namensmuster der Sicherungen).</summary>
    public void DeleteLocalBackup(string name, string by)
    {
        if (!BackupNames.IsBackup(name)) throw new LocalizedException("Sicherung nicht gefunden.");
        var file = Path.Combine(BackupDirectory, name);
        if (!File.Exists(file)) throw new LocalizedException("Sicherung nicht gefunden.");
        var size = new FileInfo(file).Length;
        File.Delete(file);
        LogEvent(null, EventCategories.System, L.T("Sicherung gelöscht: {0} ({1} MB) – {2}", name, (size / 1048576.0).ToString("0.0", L.Culture), by));
    }

    /// <summary>Vorherigen Stand („backup-…“-Ordner) löschen.</summary>
    public void DeletePreviousState(string name, string by)
    {
        if (!PreviousStatePattern.IsMatch(name)) throw new LocalizedException("Vorheriger Stand nicht gefunden.");
        var dir = new DirectoryInfo(Path.Combine(DataDirectory, name));
        if (!dir.Exists) throw new LocalizedException("Vorheriger Stand nicht gefunden.");
        var size = DirectorySize(dir);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        dir.Delete(recursive: true);
        LogEvent(null, EventCategories.System, L.T("Vorheriger Stand gelöscht: {0} ({1} MB) – {2}", name, (size / 1048576.0).ToString("0.0", L.Culture), by));
    }

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    private static long DirectorySize(DirectoryInfo dir)
    {
        try { return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch (IOException) { return 0L; } }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
