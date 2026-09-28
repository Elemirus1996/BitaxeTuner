using System.Diagnostics;
using System.IO;
using System.Windows;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Transfer;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Services;

/// <summary>
/// Wechsel der Betriebsart „Lokal“ ↔ „Server“ mit einmaliger, geprüfter Datenübertragung.
/// Nie zwei aktive Datenbestände: vor dem Umschalten wird die jeweils andere Seite pausiert.
/// </summary>
public static class ServerTransfer
{
    public const string PendingFolder = "transfer-pending";
    private const string ReadyMarker = ".ready";

    public static string PendingDirectory(string dataDirectory) => Path.Combine(dataDirectory, PendingFolder);

    /// <summary>
    /// Beim Start, bevor history.db geöffnet wird: vom Server geholte und bereits geprüfte Daten übernehmen
    /// (mit Sicherung des bisherigen Stands). Liefert einen Hinweis für die Statuszeile oder null.
    /// </summary>
    public static string? ApplyPendingImport(string dataDirectory)
    {
        var pending = PendingDirectory(dataDirectory);
        if (!Directory.Exists(pending)) return null;
        var marker = Path.Combine(pending, ReadyMarker);
        if (!File.Exists(marker))
        {
            // Abgebrochener Download: nichts übernehmen
            Directory.Delete(pending, recursive: true);
            return null;
        }
        File.Delete(marker);
        var backup = DataArchive.Apply(pending, dataDirectory, KeepDesktopSettings);
        Directory.Delete(pending, recursive: true);
        return L.T("Daten vom Server übernommen. Bisheriger lokaler Stand gesichert in {0}.", Path.GetFileName(backup));
    }

    /// <summary>Einstellungen, die zu diesem PC gehören und nicht vom Server kommen.</summary>
    private static void KeepDesktopSettings(AppConfig local, AppConfig fromServer)
    {
        fromServer.Server = local.Server;
        fromServer.Theme = local.Theme;
        fromServer.MinimizeToTray = local.MinimizeToTray;
        fromServer.StartMinimized = local.StartMinimized;
        fromServer.WarningAccepted = local.WarningAccepted;
        fromServer.IntegrationBackupDone = true;
        fromServer.TunerDataMigrated = local.TunerDataMigrated;
        fromServer.WebView = local.WebView;
        fromServer.Backup = local.Backup; // Sicherungsziele gehören zum Gerät (Pfade, NAS)
        fromServer.Mqtt = local.Mqtt;
    }

    /// <summary>Lokale Daten (inkl. geöffneter history.db) als Archiv hochladen. Fragt nach, wenn der Server schon Daten hat.</summary>
    public static async Task<string> UploadAsync(AppHost host, ServerClient client, Func<string, bool> confirmReplace, IProgress<string> log)
    {
        var file = Path.Combine(Path.GetTempPath(), $"bitaxetuner-upload-{Guid.NewGuid():N}.zip");
        try
        {
            log.Report(L.T("Erstelle Datenarchiv (history.db über die SQLite-Backup-API) …"));
            host.Config.Save();
            var manifest = await Task.Run(() =>
            {
                using var fs = File.Create(file);
                return DataArchive.Create(host.DataDirectory, host.History, fs, "desktop", MainViewModelVersion());
            });
            log.Report(L.T("Archiv: {0} Dateien, {1:N0} Verlaufswerte, ", manifest.Files.Count, manifest.HistoryRows.GetValueOrDefault("samples")) +
                       L.T("{0:0.0} MB. Lade hoch …", new FileInfo(file).Length / 1024.0 / 1024.0));
            try
            {
                await using var fs = File.OpenRead(file);
                return await client.ImportAsync(fs, replace: false);
            }
            catch (ServerException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict)
            {
                if (!confirmReplace(ex.Message)) throw new OperationCanceledException(L.T("Abgebrochen – auf dem Server wurde nichts verändert."));
                log.Report(L.T("Ersetze Serverdaten (der Server sichert seinen Stand vorher) …"));
                await using var fs = File.OpenRead(file);
                return await client.ImportAsync(fs, replace: true);
            }
        }
        finally
        {
            try { File.Delete(file); } catch { /* egal */ }
        }
    }

    /// <summary>Serverdaten herunterladen, prüfen und für den nächsten Start bereitstellen.</summary>
    public static async Task<ArchiveManifest> DownloadAsync(ServerClient client, string dataDirectory, IProgress<string> log)
    {
        var pending = PendingDirectory(dataDirectory);
        if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
        var file = Path.Combine(Path.GetTempPath(), $"bitaxetuner-download-{Guid.NewGuid():N}.zip");
        try
        {
            log.Report(L.T("Lade Daten vom Server …"));
            await using (var fs = File.Create(file)) await client.DownloadExportAsync(fs);
            log.Report(L.T("{0:0.0} MB geladen. Prüfe Prüfsummen und history.db …", new FileInfo(file).Length / 1024.0 / 1024.0));
            var manifest = await Task.Run(() =>
            {
                using var fs = File.OpenRead(file);
                return DataArchive.ExtractAndVerify(fs, pending);
            });
            File.WriteAllText(Path.Combine(pending, ReadyMarker), DateTime.Now.ToString("O"));
            log.Report(L.T("Geprüft: {0} Dateien, {1:N0} Verlaufswerte.", manifest.Files.Count, manifest.HistoryRows.GetValueOrDefault("samples")));
            return manifest;
        }
        catch
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            throw;
        }
        finally
        {
            try { File.Delete(file); } catch { /* egal */ }
        }
    }

    /// <summary>App neu starten (die Betriebsart wird beim Start gewählt).</summary>
    public static void Restart()
    {
        // Die neue Instanz wartet, bis diese beendet ist (history.db muss frei sein, bevor Daten übernommen werden)
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--wait-pid {Environment.ProcessId}") { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    private static string MainViewModelVersion() => ViewModels.MainViewModel.CurrentVersion.ToString(3);
}
