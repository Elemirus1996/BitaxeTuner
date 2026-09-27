using System.Globalization;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Transfer;

namespace BitaxeTuner.Core.Backup;

/// <summary>
/// Desktop-App im Modus „Server“: einmal täglich den kompletten Datenstand des Servers als geprüfte Sicherung
/// auf diesen PC holen (gleiches Archiv wie „Daten vom Server holen“, hier nur abgelegt, nichts übernommen).
/// </summary>
public static class BackupPickup
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BitaxeTuner-Sicherungen");

    public static string FolderOf(ServerConnectionSettings s) => s.BackupFolder is { Length: > 0 } f ? f : DefaultFolder;

    public static bool IsDue(ServerConnectionSettings s, DateTime now) =>
        s.Enabled && s.BackupPickup && s.BackupLastPickup != now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Herunterladen, vollständig prüfen, ablegen, alte aufräumen. Liefert den Dateinamen.</summary>
    public static async Task<string> RunAsync(ServerClient client, string folder, int keep, DateTime now, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var name = BackupNames.For(now);
        var tmp = Path.Combine(folder, name + ".tmp");
        var staging = Path.Combine(Path.GetTempPath(), "bt-pickup-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fs = File.Create(tmp)) await client.DownloadExportAsync(fs, ct);
            await Task.Run(() =>
            {
                using var check = File.OpenRead(tmp);
                DataArchive.ExtractAndVerify(check, staging);
            }, ct);
            File.Move(tmp, Path.Combine(folder, name), overwrite: true);
            foreach (var old in BackupNames.Surplus(Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>(), keep))
                File.Delete(Path.Combine(folder, old));
            return name;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* egal */ }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* egal */ }
        }
    }
}
