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
    private async Task PickupBackupAsync()
    {
        var s = _config.Server;
        if (_pickupBusy || !Core.Backup.BackupPickup.IsDue(s, DateTime.Now)) return;
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

    // ---------- App-Updates (wie im Modus „Lokal“) ----------

    private async Task CheckUpdateAsync()
    {
        if (!_config.CheckForUpdates) return;
        var result = await new UpdateService(_updateHttp, UpdateChecker.Repository).CheckAsync(MainViewModel.CurrentVersion);
        if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Update is not { } u) return;
        _update = u;
        UpdateButton.Content = L.T("Update {0} installieren", u.Tag);
        UpdateButton.Visibility = Visibility.Visible;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } u) return;
        if (MessageBox.Show(this, L.T("BitaxeTuner {0} installieren?\n\nDie App wird beendet, aktualisiert und neu gestartet. ", u.Tag) +
                                  L.T("Der Server läuft währenddessen weiter (er wird in seiner Oberfläche separat aktualisiert)."),
                L.T("Update"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        UpdateButton.IsEnabled = false;
        try
        {
            var file = await new UpdateService(_updateHttp, UpdateChecker.Repository)
                .DownloadAsync(u, Path.Combine(Path.GetTempPath(), L.T("BitaxeTuner-Update")),
                    new Progress<double>(p => UpdateButton.Content = L.T("Lade … {0:P0}", p)));
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = L.T("Update {0} installieren", u.Tag);
            MessageBox.Show(this, ex.Message, L.T("Update"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
