using System.IO.Compression;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Transfer;

/// <summary>Freien Speicher prüfen, bevor große Dateien geschrieben werden (Audit E4, z. B. SD-Karte des Pi).</summary>
public static class DiskSpace
{
    /// <summary>Immer frei bleiben (Verlauf, Protokoll, Sicherungen laufen weiter).</summary>
    public const long Reserve = 200L * 1024 * 1024;

    /// <summary>Freier Platz auf dem Laufwerk/Dateisystem des Ordners; null, wenn unbekannt.</summary>
    public static long? FreeBytes(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            // Linux: das Dateisystem mit dem längsten passenden Einhängepunkt (z. B. /var/lib auf eigener Partition)
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return drive?.AvailableFreeSpace;
        }
        catch { return null; }
    }

    /// <summary>Größe eines Ordners (alle Dateien), 0 bei Fehlern.</summary>
    public static long FolderSize(string directory)
    {
        try { return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    /// <summary>Entpackte Größe eines ZIP-Archivs.</summary>
    public static long UncompressedSize(string zipFile)
    {
        using var zip = ZipFile.OpenRead(zipFile);
        return zip.Entries.Sum(e => e.Length);
    }

    /// <summary>Wirft eine verständliche Meldung, wenn weniger als <paramref name="needed"/> + Reserve frei ist.</summary>
    public static void Require(string directory, long needed)
    {
        if (FreeBytes(directory) is not { } free) return;   // unbekannt: nicht blockieren
        if (free < needed + Reserve)
            throw new LocalizedException("Zu wenig Speicherplatz: frei {0} MB, nötig etwa {1} MB. Bitte Platz schaffen (z. B. alte Sicherungen löschen).",
                free / (1024 * 1024), (needed + Reserve) / (1024 * 1024)) { Status = 507 };
    }
}
