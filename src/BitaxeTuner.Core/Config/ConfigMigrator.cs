using BitaxeTuner.Core.Monitoring;
using Microsoft.Data.Sqlite;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// Einmalige Schritte beim ersten Start der zusammengeführten App.
/// Grundsatz: nur kopieren, nie verschieben, nie überschreiben.
/// </summary>
public static class ConfigMigrator
{
    /// <summary>
    /// Übernimmt Geräte und Daten des eigenständigen BitaxeTuner:
    /// Adressen → Devices (nur neue Hosts), results\*.json und profiles.json → &lt;data&gt;\tuning\.
    /// Liefert eine Liste der durchgeführten Schritte (leer = nichts zu tun).
    /// </summary>
    public static List<string> MigrateTunerData(AppConfig config, string legacySettingsDirectory, string tuningDirectory)
    {
        var log = new List<string>();
        if (config.TunerDataMigrated) return log;

        var legacy = TunerLegacySettings.TryLoad(legacySettingsDirectory);
        if (legacy is not null)
        {
            foreach (var address in legacy.DeviceAddresses.Select(a => a.Trim()).Where(a => a.Length > 0))
            {
                if (config.Devices.Any(d => string.Equals(d.Host.Trim(), address, StringComparison.OrdinalIgnoreCase)))
                    continue;
                config.Devices.Add(new DeviceConfig { Name = address, Host = address });
                log.Add($"Gerät aus BitaxeTuner übernommen: {address}");
            }
            config.WarningAccepted |= legacy.WarningAccepted;
            config.CheckForUpdates = legacy.CheckForUpdates;

            var oldData = legacy.EffectiveDataDirectory(legacySettingsDirectory);
            log.AddRange(CopyIfMissing(Path.Combine(oldData, "profiles.json"), Path.Combine(tuningDirectory, "profiles.json")));

            var oldResults = Path.Combine(oldData, "results");
            if (Directory.Exists(oldResults))
            {
                foreach (var file in Directory.EnumerateFiles(oldResults, "*.json"))
                    log.AddRange(CopyIfMissing(file, Path.Combine(tuningDirectory, "results", Path.GetFileName(file))));
            }
        }

        config.TunerDataMigrated = true;
        return log;
    }

    private static IEnumerable<string> CopyIfMissing(string source, string target)
    {
        if (!File.Exists(source)) yield break;
        if (File.Exists(target))
        {
            yield return $"Übersprungen (existiert bereits): {target}";
            yield break;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: false);
        yield return $"Kopiert: {source} → {target}";
    }

    /// <summary>
    /// Vollständige Sicherung des Datenordners vor der ersten Schemaänderung (tuning_events).
    /// history.db über die SQLite-Backup-API (WAL-sicher), alle übrigen Dateien per Kopie.
    /// Muss laufen, BEVOR history.db von der App geöffnet wird. Gibt den Sicherungsordner zurück.
    /// </summary>
    public static string BackupDataDirectory(string dataDirectory)
    {
        // Eindeutig, auch bei mehreren Sicherungen in derselben Sekunde (nie in eine vorhandene Sicherung schreiben)
        var stamp = "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(dataDirectory, stamp);
        for (var n = 2; Directory.Exists(target); n++) target = Path.Combine(dataDirectory, $"{stamp}-{n}");
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dataDirectory, file);
            if (relative.StartsWith("backup-", StringComparison.OrdinalIgnoreCase)) continue;
            // Tägliche Sicherungen nicht in jede Sicherung kopieren (würde sich vervielfachen)
            if (relative.StartsWith("auto-backups", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(file);
            // history.db samt WAL-Dateien wird unten konsistent über die Backup-API gesichert
            if (name.StartsWith("history.db", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(relative) is "" or null) continue;

            var dest = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: false);
        }

        var db = Path.Combine(dataDirectory, "history.db");
        if (File.Exists(db))
        {
            var destDb = Path.Combine(target, "history.db");
            using (var src = new SqliteConnection($"Data Source={db};Pooling=False"))
            using (var dst = new SqliteConnection($"Data Source={destDb};Pooling=False"))
            {
                src.Open();
                dst.Open();
                src.BackupDatabase(dst);
            }
            var (ok, _) = HistoryStore.Verify(destDb);
            if (!ok) throw new IOException("Sicherung von history.db ist nicht konsistent (integrity_check).");
        }

        return target;
    }
}
