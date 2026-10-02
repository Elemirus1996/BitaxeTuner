using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BitaxeTuner.App.Services;
using BitaxeTuner.App.ViewModels;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Transfer;
using BitaxeTuner.Core.Update;
using Microsoft.Web.WebView2.Core;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// Betriebsart „Server“: Die App fragt keine Miner ab und startet keinen eigenen Motor. Sie zeigt die Oberfläche
/// des BitaxeTuner-Servers eingebettet an (angemeldet über das API-Token) und kümmert sich um App-Updates
/// und den Wechsel der Betriebsart.
/// </summary>
public partial class RemoteWindow : Window
{
    private readonly AppConfig _config;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(30) };
    /// <summary>Tägliche Sicherung vom Server holen: 2 min nach dem Start, danach halbstündlich prüfen.</summary>
    private readonly DispatcherTimer _pickup = new() { Interval = TimeSpan.FromMinutes(2) };
    private bool _pickupBusy;
    private readonly System.Net.Http.HttpClient _updateHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private UpdateInfo? _update;
    private bool _webReady;

    public RemoteWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        ServerText.Text = config.Server.Url;
        VersionText.Text = "App v" + MainViewModel.CurrentVersion.ToString(3);
        _retry.Tick += async (_, _) => await ConnectAsync();
        _pickup.Tick += async (_, _) =>
        {
            _pickup.Interval = TimeSpan.FromMinutes(30);
            await PickupBackupAsync();
        };
        _pickup.Start();
        Loaded += async (_, _) =>
        {
            await ConnectAsync();
            await CheckUpdateAsync();
        };
        Closed += (_, _) => { _retry.Stop(); _pickup.Stop(); _updateHttp.Dispose(); };
    }

    /// <summary>Einmal täglich eine geprüfte Sicherung des Servers auf diesen PC holen (Einstellung unter Betriebsart).</summary>
    private async Task PickupBackupAsync(bool force = false)
    {
        var s = _config.Server;
        if (_pickupBusy || !(force || Core.Backup.BackupPickup.IsDue(s, DateTime.Now))) return;
        _pickupBusy = true;
        try
        {
            using var client = new ServerClient(s.Url, s.Token, s.CertificateFingerprint);
            var name = await Core.Backup.BackupPickup.RunAsync(client, Core.Backup.BackupPickup.FolderOf(s), Math.Clamp(s.BackupKeep, 1, 365), DateTime.Now);
            s.BackupLastPickup = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            _config.Save();
            BackupText.Text = L.T("· Sicherung {0:HH:mm} ✓", DateTime.Now);
            BackupText.ToolTip = Path.Combine(Core.Backup.BackupPickup.FolderOf(s), name);
        }
        catch (Exception ex) when (ex is ServerException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            BackupText.Text = L.T("· Sicherung fehlgeschlagen");
            BackupText.ToolTip = ex.Message;
        }
        finally
        {
            _pickupBusy = false;
        }
    }

    /// <summary>
    /// Nach dem ersten erfolgreichen Verbinden einmal fragen, ob täglich eine Sicherung auf diesen PC geholt werden soll –
    /// sonst gibt es außerhalb des Servers keine Kopie (z. B. wenn die SD-Karte ausfällt).
    /// </summary>
    private async void AskPickupOnce()
    {
        var s = _config.Server;
        if (s.BackupPickup || s.BackupPickupAsked) return;
        s.BackupPickupAsked = true;
        _config.Save();
        var folder = Core.Backup.BackupPickup.FolderOf(s);
        if (MessageBox.Show(this,
                L.T("Soll diese App täglich eine geprüfte Sicherung des Servers auf diesen PC holen?\n\nOrdner: {0}\n\nDann gibt es eine Kopie außerhalb des Servers – z. B. falls dessen SD-Karte ausfällt. Ändern jederzeit unter „Sicherungen“ bzw. „Betriebsart …“.", folder),
                L.T("Sicherung auf diesen PC"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        s.BackupPickup = true;
        _config.Save();
        await PickupBackupAsync(force: true);
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        var menu = BackupButton.ContextMenu!;
        menu.PlacementTarget = BackupButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        PickupNowItem.IsEnabled = !_pickupBusy;
        menu.IsOpen = true;
    }

    private async void PickupNow_Click(object sender, RoutedEventArgs e)
    {
        BackupText.Text = L.T("· Sicherung wird geholt …");
        await PickupBackupAsync(force: true);
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e) =>
        Services.BackupActions.OpenFolder(this, Core.Backup.BackupPickup.FolderOf(_config.Server));

    private async void RestoreToServer_Click(object sender, RoutedEventArgs e)
    {
        if (await Services.BackupActions.RestoreToServerAsync(this, _config.Server)) await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        _retry.Stop();
        try
        {
            using var client = new ServerClient(_config.Server.Url, _config.Server.Token, _config.Server.CertificateFingerprint);
            var info = await client.InfoAsync();
            if (info.ApiVersion != ServerClient.SupportedApiVersion)
                throw new ServerException(L.T("Server {0} und App {1} passen nicht zusammen – bitte beide aktualisieren.", info.Version, MainViewModel.CurrentVersion.ToString(3)));
            if (info.SetupRequired)
                throw new ServerException(L.T("Der Server ist noch nicht eingerichtet – im Browser öffnen und Admin-Passwort festlegen."));
            var (session, _, expires) = await client.CreateSessionAsync();
            ServerText.Text = L.T("{0} · Server v{1}", client.BaseUri, info.Version) + (info.Paused ? L.T(" · PAUSIERT") : "");

            await EnsureWebAsync();
            var core = Web.CoreWebView2;
            var cookie = core.CookieManager.CreateCookie("bt_session", session, client.BaseUri.Host, "/");
            cookie.IsHttpOnly = true;
            cookie.IsSecure = client.BaseUri.Scheme == "https";
            cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
            cookie.Expires = expires.ToLocalTime();
            core.CookieManager.AddOrUpdateCookie(cookie);
            core.Navigate(client.BaseUri.ToString());
            ErrorPanel.Visibility = Visibility.Collapsed;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(AskPickupOnce));
        }
        catch (Exception ex) when (ex is ServerException or WebView2RuntimeNotFoundException)
        {
            ShowError(ex is WebView2RuntimeNotFoundException
                ? "Die WebView2-Laufzeit fehlt (bei Windows 10/11 normalerweise vorhanden). Installation: https://go.microsoft.com/fwlink/p/?LinkId=2124703"
                : ex.Message);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ShowError(L.T("Anzeige konnte nicht gestartet werden: ") + ex.Message);
        }
    }

    private async Task EnsureWebAsync()
    {
        if (_webReady) return;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitaxeTuner", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, folder);
        await Web.EnsureCoreWebView2Async(env);
        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Selbst signiertes Zertifikat: nur mit dem beim Verbinden bestätigten Fingerabdruck
        core.ServerCertificateErrorDetected += (_, e) =>
        {
            using var cert = e.ServerCertificate.ToX509Certificate2();
            e.Action = _config.Server.CertificateFingerprint is { } pinned &&
                       string.Equals(ServerClient.Fingerprint(cert), pinned, StringComparison.OrdinalIgnoreCase)
                ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                : CoreWebView2ServerCertificateErrorAction.Cancel;
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) ShowError(L.T("Seite konnte nicht geladen werden ({0}).", e.WebErrorStatus));
        };
        // Sicherungen aus der Server-Oberfläche (z. B. „vor dem Update herunterladen“) direkt in den Sicherungsordner dieses PCs
        core.DownloadStarting += (_, e) =>
        {
            var name = Path.GetFileName(e.ResultFilePath);
            if (!Core.Backup.BackupNames.IsBackup(name)) return;
            var folder = Core.Backup.BackupPickup.FolderOf(_config.Server);
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // sonst normaler Download
            e.ResultFilePath = Path.Combine(folder, name);
            e.Handled = true;
            BackupText.Text = L.T("· Sicherung {0:HH:mm} → PC", DateTime.Now);
            BackupText.ToolTip = e.ResultFilePath;
        };
        // Links nach außen (z. B. GitHub) im Standardbrowser öffnen
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
        };
        _webReady = true;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorPanel.Visibility = Visibility.Visible;
        _retry.Start();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ServerClient.Normalize(_config.Server.Url).ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "BitaxeTuner"); }
    }

    private void Mode_Click(object sender, RoutedEventArgs e) =>
        new ServerModeWindow(_config, null) { Owner = this }.ShowDialog();

    /// <summary>Konsole auf dem Server öffnen (Benutzer/Rechner unter „Betriebsart … → SSH-Terminal zum Server“).</summary>
    private async void Ssh_Click(object sender, RoutedEventArgs e) =>
        await Services.SshKeyService.OpenForServerAsync(this, _config.Server, () => Mode_Click(sender, e));

    // ---------- Updates: Server und App mit einem Klick ----------

    private ServerUpdateStatus? _serverUpdate;

    private static string Plain(string? version) => (version ?? "").Trim().TrimStart('v', 'V');

    /// <summary>
    /// App-Update (GitHub) und Server-Update (über die Server-API) prüfen. Ein Knopf für beides: „Server + App“,
    /// nur Server oder nur App – je nachdem, was neu ist und ob sich der Server selbst aktualisieren kann.
    /// </summary>
    private async Task CheckUpdateAsync()
    {
        if (!_config.CheckForUpdates) return;
        try
        {
            var result = await new UpdateService(_updateHttp, UpdateChecker.Repository).CheckAsync(MainViewModel.CurrentVersion);
            _update = result.Status == UpdateCheckStatus.UpdateAvailable ? result.Update : null;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException) { _update = null; }
        try
        {
            using var client = new ServerClient(_config.Server.Url, _config.Server.Token, _config.Server.CertificateFingerprint);
            var s = await client.CheckServerUpdateAsync();
            _serverUpdate = s.Latest is not null && s.CanInstall && Plain(s.Latest) != Plain(s.Current) ? s : null;
        }
        catch (ServerException) { _serverUpdate = null; }   // ältere/abweichende Server: nur App-Update anbieten
        ShowUpdateButton();
    }

    private void ShowUpdateButton()
    {
        UpdateButton.Content = (_update, _serverUpdate) switch
        {
            ({ } u, { } s) => Plain(u.Tag) == Plain(s.Latest) ? L.T("Update {0}: Server + App", u.Tag) : L.T("Updates: Server {0} + App {1}", s.Latest!, u.Tag),
            ({ } u, null) => L.T("Update {0} installieren", u.Tag),
            (null, { } s) => L.T("Server-Update {0}", s.Latest!),
            _ => null,
        };
        UpdateButton.Visibility = UpdateButton.Content is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var app = _update;
        var server = _serverUpdate;
        if (app is null && server is null) return;
        var folder = Core.Backup.BackupPickup.FolderOf(_config.Server);
        var text = new System.Text.StringBuilder();
        if (server is not null) text.AppendLine(L.T("Server: {0} → {1}", server.Current, server.Latest!));
        if (app is not null) text.AppendLine(L.T("App: {0} → {1}", MainViewModel.CurrentVersion.ToString(3), app.Tag));
        text.AppendLine();
        text.AppendLine(L.T("Ablauf:"));
        text.AppendLine(L.T("1. Geprüfte Sicherung des Servers auf diesen PC ({0})", folder));
        if (server is not null)
        {
            text.AppendLine(L.T("2. Server-Update (der Server sichert vorher zusätzlich auf USB/NAS, laufende Benchmarks werden gestoppt)"));
            text.AppendLine(L.T("3. Warten, bis der Server mit der neuen Version wieder läuft"));
        }
        if (app is not null) text.AppendLine(server is null ? L.T("2. App-Update – die App startet neu") : L.T("4. App-Update – die App startet neu"));
        text.AppendLine();
        text.Append(L.T("Schlägt ein Schritt fehl, wird nicht weitergemacht."));
        if (MessageBox.Show(this, text.ToString(), L.T("Update"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        UpdateButton.IsEnabled = false;
        try
        {
            using var client = new ServerClient(_config.Server.Url, _config.Server.Token, _config.Server.CertificateFingerprint);

            // 1. Sicherung auf den PC (unabhängig von der täglichen Abholung)
            UpdateButton.Content = L.T("Sicherung auf den PC …");
            var name = await Core.Backup.BackupPickup.RunAsync(client, folder, Math.Clamp(_config.Server.BackupKeep, 1, 365), DateTime.Now);
            BackupText.Text = L.T("· Sicherung {0:HH:mm} ✓", DateTime.Now);
            BackupText.ToolTip = Path.Combine(folder, name);

            if (server is not null)
            {
                // 2. Server-Update anstoßen, 3. auf die neue Version warten (Neustart dauert ca. 1 Minute)
                UpdateButton.Content = L.T("Server wird aktualisiert …");
                await client.InstallServerUpdateAsync();
                var deadline = DateTime.Now.AddMinutes(8);
                string? running = null;
                while (DateTime.Now < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    try
                    {
                        running = (await client.InfoAsync()).Version;
                        if (Plain(running) == Plain(server.Latest)) break;
                    }
                    catch (ServerException) { /* startet gerade neu */ }
                }
                if (Plain(running) != Plain(server.Latest))
                    throw new InvalidOperationException(L.T("Der Server meldet sich nach dem Update nicht mit Version {0} (zuletzt: {1}). Die App wurde nicht aktualisiert – bitte die Server-Oberfläche prüfen.",
                        server.Latest!, running ?? L.T("nicht erreichbar")));
                _serverUpdate = null;
                await ConnectAsync();
            }

            if (app is not null)
            {
                // 4. App-Update (wie bisher): Installer starten, App beendet sich und startet neu
                var file = await new UpdateService(_updateHttp, UpdateChecker.Repository)
                    .DownloadAsync(app, Path.Combine(Path.GetTempPath(), L.T("BitaxeTuner-Update")),
                        new Progress<double>(p => UpdateButton.Content = L.T("Lade … {0:P0}", p)));
                Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
                Application.Current.Shutdown();
                return;
            }
            MessageBox.Show(this, L.T("Server läuft jetzt mit Version {0}.", server!.Latest!), L.T("Update"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ServerException or IOException or InvalidDataException or InvalidOperationException
                                       or System.Net.Http.HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, L.T("Update"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
            ShowUpdateButton();
        }
    }
}
