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
            BuildServerPicker();
            await ConnectAsync();
            await CheckUpdateAsync();
            StartMini();
        };
        _allTimer.Tick += async (_, _) => await LoadAllAsync();
        Closed += (_, _) => { _retry.Stop(); _pickup.Stop(); _allTimer.Stop(); _updateHttp.Dispose(); _miniTimer.Stop(); _mini?.Dispose(); _tray?.Dispose(); };
    }

    // ---------- 0.9.25 Infobereich und Mini-Anzeige im Modus „Server“ ----------

    private TrayService? _tray;
    private MiniPresenter? _mini;
    private readonly DispatcherTimer _miniTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    private void StartMini()
    {
        if (_tray is not null) return;
        _tray = new TrayService();
        _tray.OpenRequested += () => { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); };
        _tray.ExitRequested += Close;
        _mini = new MiniPresenter(_config, _tray);
        _miniTimer.Tick += async (_, _) => await RefreshMiniAsync();
        _miniTimer.Start();
        _ = RefreshMiniAsync();
    }

    /// <summary>Status des aktiven Servers für Tray-Symbol und Mini-Anzeige (gleiche Summen wie im Browser).</summary>
    private async Task RefreshMiniAsync()
    {
        if (_mini is null || _tray is null) return;
        try
        {
            using var client = new ServerClient(_config.Server.Url, _config.Server.Token, _config.Server.CertificateFingerprint, TimeSpan.FromSeconds(8));
            _mini.Update(Core.Display.MiniStatus.FromStatusJson(await client.StatusAsync()));
        }
        catch (Exception ex) when (ex is ServerException or System.Net.Http.HttpRequestException or TaskCanceledException or UriFormatException)
        {
            _tray.Update(TrayState.Error, L.T("BitaxeTuner – Server nicht erreichbar"));
        }
    }

    // ---------- 0.9.24 Mehrere Server ----------

    private readonly DispatcherTimer _allTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _pickerBusy;

    /// <summary>Umschalter füllen: aktiver Server, weitere Server, „Alle Server“. Nur sichtbar mit mehr als einem Server.</summary>
    private void BuildServerPicker()
    {
        _pickerBusy = true;
        var all = MultiServer.All(_config);
        ServerPicker.ItemsSource = all.Select(MultiServer.NameOf).Append(L.T("Alle Server (Summen)")).ToList();
        ServerPicker.SelectedIndex = AllPanel.Visibility == Visibility.Visible ? all.Count : 0;
        ServerPicker.Visibility = all.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _pickerBusy = false;
    }

    private async void ServerPicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_pickerBusy || ServerPicker.SelectedIndex < 0) return;
        var count = _config.ExtraServers.Count + 1;
        if (ServerPicker.SelectedIndex == count) { await ShowAllAsync(); return; }
        await SwitchToAsync(ServerPicker.SelectedIndex);
    }

    /// <summary>Auf Server Nr. <paramref name="index"/> wechseln (0 = aktiver) und die Oberfläche neu laden.</summary>
    private async Task SwitchToAsync(int index)
    {
        _allTimer.Stop();
        AllPanel.Visibility = Visibility.Collapsed;
        if (index > 0)
        {
            MultiServer.Activate(_config, index);
            _config.Save();
            BackupText.Text = "";
            HttpsButton.Visibility = Visibility.Collapsed;
        }
        BuildServerPicker();
        await ConnectAsync();
    }

    private async Task ShowAllAsync()
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        AllPanel.Visibility = Visibility.Visible;
        await LoadAllAsync();
        _allTimer.Start();
    }

    private async Task LoadAllAsync()
    {
        if (AllPanel.Visibility != Visibility.Visible) { _allTimer.Stop(); return; }
        AllHint.Text = L.T("Lade …");
        var tiles = await MultiServer.FetchAllAsync(_config);
        var sum = MultiServer.Sum(tiles);
        AllTotals.Text = L.T("{0} · {1} W · {2}/{3} Miner online", Core.Display.StatusRenderer.FormatHash(sum.HashGh), sum.PowerW.ToString("0"), sum.Online, sum.Count)
            + (sum.CostPerDay is { } c ? L.T(" · {0} {1}/Tag Strom", c.ToString("0.00"), sum.Currency ?? "") : "");
        AllHint.Text = L.T("{0} von {1} Servern erreichbar · aktualisiert {2:HH:mm:ss} · Klick auf einen Server wechselt dorthin.", sum.Reachable, sum.Servers, DateTime.Now);
        AllTiles.Children.Clear();
        foreach (var t in tiles) AllTiles.Children.Add(TileOf(t));
    }

    private System.Windows.Controls.Button TileOf(ServerTile t)
    {
        var lines = new System.Windows.Controls.StackPanel { Width = 250 };
        lines.Children.Add(new System.Windows.Controls.TextBlock { Text = (t.Index == 0 ? "● " : "") + t.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var url = new System.Windows.Controls.TextBlock { Text = t.Url, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) };
        url.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "MutedTextBrush");
        lines.Children.Add(url);
        if (!t.Reachable)
        {
            var err = new System.Windows.Controls.TextBlock { Text = L.T("nicht erreichbar: {0}", t.Error ?? ""), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            err.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
            lines.Children.Add(err);
        }
        else
        {
            lines.Children.Add(new System.Windows.Controls.TextBlock { Text = Core.Display.StatusRenderer.FormatHash(t.HashGh), FontSize = 22, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas") });
            lines.Children.Add(new System.Windows.Controls.TextBlock { Text = L.T("{0} W · {1}/{2} Miner online", t.PowerW.ToString("0"), t.Online, t.Count), FontSize = 12 });
            if (t.CostPerDay is { } c) lines.Children.Add(new System.Windows.Controls.TextBlock { Text = L.T("{0} {1}/Tag Strom", c.ToString("0.00"), t.Currency), FontSize = 12 });
            if (t.Paused)
            {
                var p = new System.Windows.Controls.TextBlock { Text = L.T("Motor pausiert"), FontSize = 12 };
                p.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarnBrush");
                lines.Children.Add(p);
            }
        }
        var b = new System.Windows.Controls.Button { Content = lines, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(14), HorizontalContentAlignment = HorizontalAlignment.Left };
        b.SetResourceReference(BackgroundProperty, "CardBrush");
        System.Windows.Automation.AutomationProperties.SetName(b, L.T("Zu {0} wechseln", t.Name));
        b.Click += async (_, _) => await SwitchToAsync(t.Index);
        return b;
    }

    private void Servers_Click(object sender, RoutedEventArgs e)
    {
        var w = new ServerListWindow(_config) { Owner = this };
        w.ShowDialog();
        if (!w.Changed) return;
        BuildServerPicker();
        if (AllPanel.Visibility == Visibility.Visible) _ = LoadAllAsync();
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
        catch (Exception ex)   // läuft per Timer im Hintergrund – nie als Fehlerfenster, nur im Statustext (nächster Versuch nach 30 min)
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
            await ShowHttpsHintAsync(client);
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

    /// <summary>Unverschlüsselte Verbindung: Knopf zum Umstellen zeigen, wenn der Server das erlaubt (Audit S4).</summary>
    private async Task ShowHttpsHintAsync(ServerClient client)
    {
        HttpsButton.Visibility = Visibility.Collapsed;
        if (client.BaseUri.Scheme != "http") return;
        try
        {
            var (enabled, configurable, _) = await client.HttpsStatusAsync();
            if (!enabled && configurable) HttpsButton.Visibility = Visibility.Visible;
        }
        catch (ServerException) { /* älterer Server ohne diese Funktion */ }
    }

    private async void Https_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                L.T("Server auf HTTPS umstellen?\n\nDanach sind Passwort, Token und Sitzung im Heimnetz verschlüsselt. Der Server startet dabei neu (etwa 10–20 Sekunden); die Miner laufen weiter.\n\nDiese App übernimmt die neue Adresse und den Fingerabdruck des Zertifikats selbst. Im Browser erscheint beim ersten Aufruf eine Warnung wegen des selbst signierten Zertifikats – das ist in Ordnung."),
                L.T("Auf HTTPS umstellen"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            using var client = new ServerClient(_config.Server.Url, _config.Server.Token, _config.Server.CertificateFingerprint);
            var fingerprint = await client.EnableHttpsAsync();
            // Audit N-Sec3: Der Fingerabdruck kam über die bisherige, unverschlüsselte Verbindung – erst nach Abgleich festhalten.
            // Ohne Bestätigung fragt die App beim ersten HTTPS-Verbinden mit dem tatsächlichen Zertifikat nach.
            if (fingerprint is not null && MessageBox.Show(this,
                    L.T("Fingerabdruck des neuen Zertifikats:\n\n{0}\n\nStimmt er mit dem im Browser (Einstellungen → Verbindung) bzw. im Server-Log überein? Nur dann „Ja“ – bei „Nein“ fragt die App beim nächsten Verbinden noch einmal.", fingerprint),
                    L.T("Auf HTTPS umstellen"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                fingerprint = null;
            _config.Server.Url = ServerClient.ToHttps(client.BaseUri).ToString();
            _config.Server.CertificateFingerprint = fingerprint;
            _config.Save();
            HttpsButton.Visibility = Visibility.Collapsed;
            ServerText.Text = L.T("Server startet mit HTTPS neu …");
            _retry.Stop();
            await Task.Delay(TimeSpan.FromSeconds(15));
            await ConnectAsync();
        }
        catch (ServerException ex)
        {
            MessageBox.Show(this, ex.Message, L.T("Auf HTTPS umstellen"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowError(string text)
    {
        if (_config.Server.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            text += L.T("\n\nWurde der Server auf HTTPS umgestellt? Dann unter „Betriebsart …“ einmal „Verbindung testen“ – die App erkennt das und fragt nach dem Zertifikat.");
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

    private void SshMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = SshButton.ContextMenu!;
        menu.PlacementTarget = SshButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------- 0.9.16: Notfall-Update und Systemdateien per SSH ----------

    private bool _sshBusy;

    /// <summary>Gemeinsamer Ablauf: Fortschritt im Update-Knopf, Fehler als Meldung (mit Hinweis auf „Betriebsart …“).</summary>
    private async Task RunSshAsync(string title, Func<SshUpdateService, Task> work)
    {
        if (_sshBusy) return;
        var host = SshKeyService.HostFor(_config.Server);
        if (host is null)
        {
            MessageBox.Show(this, L.T("Kein Rechner für SSH bekannt – unter „Betriebsart …“ eintragen."), title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _sshBusy = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Visibility = Visibility.Visible;
        try
        {
            await SshKeyService.EnsureKeyAsync();
            // 0.9.22: geänderter Host-Schlüssel (z. B. neuer Pi unter derselben Adresse) – bestätigen statt „Keine SSH-Verbindung“
            if (!await SshKeyService.EnsureHostKeyAsync(this, _config.Server.SshUser, host, title)) return;
            var ssh = new SshUpdateService(_config.Server.SshUser, host, _updateHttp, text => Dispatcher.Invoke(() => UpdateButton.Content = text));
            await work(ssh);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _sshBusy = false;
            ShowUpdateButton();
        }
    }

    private async void SshUpdate_Click(object sender, RoutedEventArgs e) => await RunSshAsync(L.T("Notfall-Update per SSH"), async ssh =>
    {
        var (arch, installed) = await ssh.ProbeAsync();
        var (info, file) = await ssh.DownloadAsync(arch);
        var target = info.Version.ToString(3);
        var text = L.T("Server per SSH aktualisieren: {0} → {1}\n\n1. Dienst stoppen und den Datenordner von Hand sichern (/var/backups/bitaxetuner)\n2. Geprüftes Paket installieren (Programm, Dienste, USB-Regel)\n3. Dienst startet neu\n\nNur nötig, wenn das Update über die Oberfläche nicht klappt.",
            installed ?? L.T("unbekannt"), target);
        if (installed == target) text += "\n\n" + L.T("Diese Version ist schon installiert – sie wird neu installiert.");
        UpdateButton.Content = L.T("Bitte bestätigen …");
        if (MessageBox.Show(this, text, L.T("Notfall-Update per SSH"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var saved = await ssh.UpdateAsync(info, file);

        // Warten, bis der Server mit der neuen Version antwortet
        UpdateButton.Content = L.T("Warte auf den Server …");
        string? running = null;
        var deadline = DateTime.Now.AddMinutes(5);
        while (DateTime.Now < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try
            {
                using var client = new ServerClient(_config.Server.Url, _config.Server.Token, _config.Server.CertificateFingerprint);
                running = (await client.InfoAsync()).Version;
                if (Plain(running) == target) break;
            }
            catch (ServerException) { /* startet gerade */ }
        }
        MessageBox.Show(this, Plain(running) == target
                ? L.T("Server läuft mit Version {0}.\nHandsicherung: {1}", target, saved)
                : L.T("Installiert, aber der Server meldet sich (noch) nicht mit Version {0}. Im SSH-Terminal prüfen: journalctl -u bitaxetuner -n 50\nHandsicherung: {1}", target, saved),
            L.T("Notfall-Update per SSH"), MessageBoxButton.OK, Plain(running) == target ? MessageBoxImage.Information : MessageBoxImage.Warning);
        _serverUpdate = null;
        await ConnectAsync();
    });

    private async void SshSystem_Click(object sender, RoutedEventArgs e) => await RunSshAsync(L.T("Systemdateien per SSH erneuern"), async ssh =>
    {
        var (arch, installed) = await ssh.ProbeAsync();
        var (info, file) = await ssh.DownloadAsync(arch);
        var latest = info.Version.ToString(3);
        if (installed != latest)
        {
            MessageBox.Show(this, L.T("Auf dem Pi läuft {0}, das neueste Paket ist {1}. Bitte zuerst aktualisieren (Update-Knopf oder „Notfall-Update per SSH …“) – dabei werden die Systemdateien mit eingerichtet.",
                installed ?? L.T("unbekannt"), latest), L.T("Systemdateien per SSH erneuern"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        UpdateButton.Content = L.T("Bitte bestätigen …");
        if (MessageBox.Show(this, L.T("Dienst, Neustart-Funktion und USB-Sicherung aus dem geprüften Paket {0} neu einrichten? Der Dienst startet dabei kurz neu, Daten bleiben unverändert.", info.Tag),
                L.T("Systemdateien per SSH erneuern"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await ssh.SystemFilesAsync(info, file);
        MessageBox.Show(this, L.T("Systemdateien eingerichtet (Dienst, Neustart-Funktion, USB-Sicherung)."), L.T("Systemdateien per SSH erneuern"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    });

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
            _serverRunning = s.Running;
            _serverUpdate = s.Latest is not null && s.CanInstall && Plain(s.Latest) != Plain(s.Current) ? s : null;
        }
        catch (ServerException) { _serverUpdate = null; }   // ältere/abweichende Server: nur App-Update anbieten
        ShowUpdateButton();
    }

    /// <summary>0.9.15: Läuft auf dem Server schon ein Update (egal von wo), bleibt der Knopf gesperrt.</summary>
    private string? _serverRunning;

    private void ShowUpdateButton()
    {
        if (_serverRunning is { } busy)
        {
            UpdateButton.Content = L.T("Server-Update läuft …");
            UpdateButton.ToolTip = busy;
            UpdateButton.IsEnabled = false;
            UpdateButton.Visibility = Visibility.Visible;
            return;
        }
        UpdateButton.IsEnabled = true;
        UpdateButton.ToolTip = null;
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
        string? lockId = null;
        using var lockClient = new ServerClient(_config.Server.Url, _config.Server.Token, _config.Server.CertificateFingerprint);
        try
        {
            using var client = new ServerClient(_config.Server.Url, _config.Server.Token, _config.Server.CertificateFingerprint);

            // 0. Update-Sperre auf dem Server: ab jetzt sehen Browser und andere Geräte „Update läuft“
            if (server is not null) lockId = await client.BeginServerUpdateAsync();

            // 1. Sicherung auf den PC (unabhängig von der täglichen Abholung)
            UpdateButton.Content = L.T("Sicherung auf den PC …");
            string name;
            try { name = await Core.Backup.BackupPickup.RunAsync(client, folder, Math.Clamp(_config.Server.BackupKeep, 1, 365), DateTime.Now); }
            catch (Exception ex)
            {
                if (lockId is not null) await lockClient.EndServerUpdateAsync(lockId, ex.Message);
                throw;
            }
            BackupText.Text = L.T("· Sicherung {0:HH:mm} ✓", DateTime.Now);
            BackupText.ToolTip = Path.Combine(folder, name);

            if (server is not null)
            {
                // 2. Server-Update anstoßen, 3. auf die neue Version warten (Neustart dauert ca. 1 Minute)
                UpdateButton.Content = L.T("Server wird aktualisiert …");
                await client.InstallServerUpdateAsync(lockId);
                // 0.9.16: Sicherung und Installation laufen auf dem Server weiter, die Anfrage kehrt sofort zurück
                var deadline = DateTime.Now.AddMinutes(20);
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
