using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Transfer;

/// <summary>
/// 0.9.20: Umzug von einem alten auf einen neuen Server (z. B. Pi 3 → Pi 5). Der alte Server wird pausiert, sein
/// komplettes Datenarchiv geholt, lokal geprüft (SHA-256, integrity_check) und auf dem neuen eingespielt. Der alte bleibt
/// danach pausiert, damit nie zwei Server dieselben Miner abfragen; schlägt etwas fehl, läuft er wieder an.
/// </summary>
public static class ServerMigration
{
    /// <param name="confirmReplace">Neuer Server hat schon Daten: Meldung des Servers → ersetzen (er sichert seinen Stand vorher)?</param>
    /// <returns>Meldung des neuen Servers (Geräte, Verlaufswerte, Sicherung des bisherigen Stands).</returns>
    public static async Task<string> CopyAsync(ServerClient from, ServerClient to, Func<string, bool> confirmReplace,
        IProgress<string> log, CancellationToken ct = default)
    {
        var file = Path.Combine(Path.GetTempPath(), $"bitaxetuner-umzug-{Guid.NewGuid():N}.zip");
        var check = Path.Combine(Path.GetTempPath(), $"bitaxetuner-umzug-{Guid.NewGuid():N}");
        var paused = false;
        try
        {
            await from.SetPausedAsync(true, ct);
            paused = true;
            log.Report(L.T("Alter Server pausiert."));
            log.Report(L.T("Lade Daten vom alten Server …"));
            await using (var fs = File.Create(file)) await from.DownloadExportAsync(fs, ct);
            log.Report(L.T("{0:0.0} MB geladen. Prüfe Prüfsummen und history.db …", new FileInfo(file).Length / 1024.0 / 1024.0));
            var manifest = await Task.Run(() =>
            {
                using var fs = File.OpenRead(file);
                return DataArchive.ExtractAndVerify(fs, check);
            }, ct);
            log.Report(L.T("Geprüft: {0} Dateien, {1:N0} Verlaufswerte.", manifest.Files.Count, manifest.HistoryRows.GetValueOrDefault("samples")));
            log.Report(L.T("Spiele die Daten auf dem neuen Server ein …"));
            string message;
            try
            {
                await using var fs = File.OpenRead(file);
                message = await to.ImportAsync(fs, replace: false, ct);
            }
            catch (ServerException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict)
            {
                if (!confirmReplace(ex.Message)) throw new OperationCanceledException(L.T("Abgebrochen – auf dem neuen Server wurde nichts verändert."));
                log.Report(L.T("Ersetze Serverdaten (der Server sichert seinen Stand vorher) …"));
                await using var fs = File.OpenRead(file);
                message = await to.ImportAsync(fs, replace: true, ct);
            }
            paused = false;   // Erfolg: der alte Server bleibt bewusst pausiert
            log.Report(L.T("Umzug fertig. Der alte Server bleibt pausiert – du kannst ihn jetzt abschalten."));
            return message;
        }
        finally
        {
            if (paused)
            {
                try { await from.SetPausedAsync(false, CancellationToken.None); log.Report(L.T("Alter Server läuft wieder.")); }
                catch (ServerException) { log.Report(L.T("Alter Server konnte nicht fortgesetzt werden – bitte in seiner Oberfläche fortsetzen.")); }
            }
            try { File.Delete(file); } catch { /* egal */ }
            try { if (Directory.Exists(check)) Directory.Delete(check, true); } catch { /* egal */ }
        }
    }
}
