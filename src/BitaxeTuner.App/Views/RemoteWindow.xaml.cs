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
        Loaded += async (_, _) =>
        {
            await ConnectAsync();
            await CheckUpdateAsync();
        };
        Closed += (_, _) => { _retry.Stop(); _updateHttp.Dispose(); };
    }

    private async Task ConnectAsync()
    {
        _retry.Stop();
        try
        {
            using var client = new ServerClient(_config.Server.Url, _config.Server.Token, _config.Server.CertificateFingerprint);
            var info = await client.InfoAsync();
            if (info.ApiVersion != ServerClient.SupportedApiVersion)
                throw new ServerException($"Server {info.Version} und App {MainViewModel.CurrentVersion.ToString(3)} passen nicht zusammen – bitte beide aktualisieren.");
            if (info.SetupRequired)
                throw new ServerException("Der Server ist noch nicht eingerichtet – im Browser öffnen und Admin-Passwort festlegen.");
            var (session, _, expires) = await client.CreateSessionAsync();
            ServerText.Text = $"{client.BaseUri} · Server v{info.Version}" + (info.Paused ? " · PAUSIERT" : "");

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
            ShowError("Anzeige konnte nicht gestartet werden: " + ex.Message);
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
            if (!e.IsSuccess) ShowError($"Seite konnte nicht geladen werden ({e.WebErrorStatus}).");
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
        UpdateButton.Content = $"Update {u.Tag} installieren";
        UpdateButton.Visibility = Visibility.Visible;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } u) return;
        if (MessageBox.Show(this, $"BitaxeTuner {u.Tag} installieren?\n\nDie App wird beendet, aktualisiert und neu gestartet. " +
                                  "Der Server läuft währenddessen weiter (er wird in seiner Oberfläche separat aktualisiert).",
                "Update", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        UpdateButton.IsEnabled = false;
        try
        {
            var file = await new UpdateService(_updateHttp, UpdateChecker.Repository)
                .DownloadAsync(u, Path.Combine(Path.GetTempPath(), "BitaxeTuner-Update"),
                    new Progress<double>(p => UpdateButton.Content = $"Lade … {p:P0}"));
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = $"Update {u.Tag} installieren";
            MessageBox.Show(this, ex.Message, "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
