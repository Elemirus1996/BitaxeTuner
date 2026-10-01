using System.Diagnostics;
using System.IO;
using System.Windows;
using BitaxeTuner.Core.Backup;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Transfer;
using Microsoft.Win32;

namespace BitaxeTuner.App.Services;

/// <summary>
/// Sicherungen in der Desktop-App: Ordner öffnen und eine Sicherung einspielen – lokal (beim nächsten Start, wie die
/// Übernahme vom Server) oder auf den Server. Eingespielt wird immer erst nach vollständiger Prüfung des Archivs;
/// der bisherige Stand bleibt als Ordner „backup-…“ im Datenordner erhalten.
/// </summary>
public static class BackupActions
{
    /// <summary>Hinweistext vor dem Einspielen (Desktop und Server gleich).</summary>
    public static string Warning => L.T("Ersetzt Einstellungen, Verlauf (history.db), Steuerdaten, Benchmark-Ergebnisse und Miner-Sicherungen durch den Stand der Sicherung. Der jetzige Stand wird vorher im Datenordner aufbewahrt (Ordner „backup-…“). Sicherungsziele, MQTT und Passwörter bleiben, wie sie sind. Die Miner selbst werden nicht verändert.");

    /// <summary>Ordner im Explorer öffnen (wird bei Bedarf angelegt).</summary>
    public static void OpenFolder(Window owner, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(owner, ex.Message, L.T("Ordner öffnen"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Sicherungsdatei wählen; null bei Abbruch.</summary>
    private static string? PickFile(Window owner, string initialFolder)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Sicherung einspielen"),
            Filter = L.T("BitaxeTuner-Sicherung (*.zip)|*.zip"),
            InitialDirectory = Directory.Exists(initialFolder) ? initialFolder : "",
        };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    private static bool Confirm(Window owner, string file) =>
        MessageBox.Show(owner, L.T("Sicherung {0} einspielen?", Path.GetFileName(file)) + "\n\n" + Warning,
            L.T("Sicherung einspielen"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>
    /// Lokaler Betrieb: Sicherung prüfen und für den nächsten Start bereitlegen, dann neu starten
    /// (history.db ist geöffnet und kann erst beim Start ersetzt werden).
    /// </summary>
    public static async Task RestoreLocalAsync(Window owner, string dataDirectory, string initialFolder)
    {
        if (PickFile(owner, initialFolder) is not { } file || !Confirm(owner, file)) return;
        var pending = ServerTransfer.PendingDirectory(dataDirectory);
        try
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            var manifest = await Task.Run(() =>
            {
                using var fs = File.OpenRead(file);
                return DataArchive.ExtractAndVerify(fs, pending);
            });
            ServerTransfer.MarkReady(pending, Path.GetFileName(file));
            MessageBox.Show(owner,
                L.T("Geprüft: {0} Gerät(e), {1} Dateien, {2:N0} Verlaufswerte (Stand {3:g}).", manifest.Devices, manifest.Files.Count,
                    manifest.HistoryRows.GetValueOrDefault("samples"), manifest.CreatedUtc.ToLocalTime()) +
                "\n\n" + L.T("Die App startet jetzt neu und spielt die Sicherung dabei ein."),
                L.T("Sicherung einspielen"), MessageBoxButton.OK, MessageBoxImage.Information);
            ServerTransfer.Restart();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            try { if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true); } catch { /* egal */ }
            MessageBox.Show(owner, L.T("Sicherung abgelehnt – es wurde nichts verändert.") + "\n\n" + ex.Message,
                L.T("Sicherung einspielen"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Modus „Server“: Sicherung vom PC auf den Server hochladen und dort einspielen. true bei Erfolg.</summary>
    public static async Task<bool> RestoreToServerAsync(Window owner, ServerConnectionSettings s)
    {
        if (PickFile(owner, BackupPickup.FolderOf(s)) is not { } file || !Confirm(owner, file)) return false;
        try
        {
            using var client = new ServerClient(s.Url, s.Token, s.CertificateFingerprint);
            await using var fs = File.OpenRead(file);
            var message = await client.ImportAsync(fs, replace: true);
            MessageBox.Show(owner, message, L.T("Sicherung einspielen"), MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex) when (ex is ServerException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, L.T("Sicherung nicht eingespielt – auf dem Server wurde nichts verändert.") + "\n\n" + ex.Message,
                L.T("Sicherung einspielen"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
