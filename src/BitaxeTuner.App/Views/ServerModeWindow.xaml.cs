using System.Diagnostics;
using System.Windows;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Transfer;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// Betriebsart wählen: „Lokal“ (App fragt selbst ab) oder „Server“ (BitaxeTuner-Server übernimmt 24/7).
/// Umschalten nur mit geprüfter Verbindung; die jeweils andere Seite wird pausiert, damit nie beide abfragen.
/// </summary>
public partial class ServerModeWindow : Window
{
    private readonly AppConfig _config;
    private readonly AppHost? _host;   // null: App läuft gerade im Server-Modus
    private bool _busy;

    public ServerModeWindow(AppConfig config, AppHost? host)
    {
        InitializeComponent();
        _config = config;
        _host = host;
        UrlBox.Text = config.Server.Url;
        TokenBox.Password = config.Server.Token;
        var serverMode = host is null;
        ModeTitle.Text = serverMode
            ? L.T("Aktuell: Server ({0}) – dieser PC fragt keine Miner ab", config.Server.Url)
            : L.T("Aktuell: Lokal – dieser PC fragt die Miner ab");
        ToServerCard.Visibility = serverMode ? Visibility.Collapsed : Visibility.Visible;
        ToLocalCard.Visibility = serverMode ? Visibility.Visible : Visibility.Collapsed;
        PiCard.Visibility = serverMode ? Visibility.Collapsed : Visibility.Visible;
        PickupCheck.IsChecked = config.Server.BackupPickup;
        PickupFolder.Text = Core.Backup.BackupPickup.FolderOf(config.Server);
        if (!serverMode) RefreshDrives();

        var region = PiRegion.FromSystem(System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage);
        PiCountry.Text = region.Country;
        PiTimezone.Text = region.Timezone;
        PiKeyboard.Text = region.Keyboard;
        SshUserBox.Text = config.Server.SshUser;
        SshHostBox.Text = config.Server.SshHost;
    }

    private void Log(string text) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\n");
        LogBox.ScrollToEnd();
    });

    private void SetBusy(bool busy)
    {
        _busy = busy;
        IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private ServerClient CreateClient()
    {
        var uri = ServerClient.Normalize(UrlBox.Text);
        // Bestätigter Zertifikat-Fingerabdruck gilt nur für dieselbe Adresse
        var pinned = string.Equals(uri.ToString(), ServerClient.Normalize(_config.Server.Url is { Length: > 0 } u ? u : "x").ToString(), StringComparison.OrdinalIgnoreCase)
            ? _config.Server.CertificateFingerprint : _pinnedNow;
        return new ServerClient(UrlBox.Text, TokenBox.Password, pinned);
    }

    private string? _pinnedNow;

    /// <summary>Verbindung und Token prüfen. Bei selbst signiertem HTTPS-Zertifikat den Fingerabdruck bestätigen lassen.</summary>
    private async Task<(ServerClient Client, ServerInfo Info)?> ConnectAsync()
    {
        var triedHttps = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var client = CreateClient();
            try
            {
                var info = await client.InfoAsync();
                if (info.Name != "BitaxeTuner-Server") throw new ServerException(L.T("Unter dieser Adresse läuft kein BitaxeTuner-Server."));
                if (info.ApiVersion != ServerClient.SupportedApiVersion)
                    throw new ServerException(L.T("Server-Version {0} passt nicht zu dieser App ({1}). Bitte beide aktualisieren.", info.Version, ViewModels.MainViewModel.CurrentVersion.ToString(3)));
                if (info.SetupRequired)
                    throw new ServerException(L.T("Der Server ist noch nicht eingerichtet. Öffne ihn im Browser und lege das Admin-Passwort fest (Einrichtungs-Code im Protokoll des Dienstes)."));
                if (string.IsNullOrWhiteSpace(TokenBox.Password))
                    throw new ServerException(L.T("Bitte ein API-Token eintragen (Server-Oberfläche → Einstellungen → Desktop-App verbinden)."));
                await client.StatusAsync(); // prüft das Token
                ConnectionText.Text = L.T("Verbunden: {0} {1} · {2} Miner · {3}", info.Name, info.Version, info.Devices, info.Os) +
                                      (info.Paused ? L.T(" · Motor PAUSIERT") : L.T(" · Motor läuft")) +
                                      (client.PresentedFingerprint is { } fp ? L.T("\nZertifikat: {0}", fp) : "");
                _config.Server.Url = client.BaseUri.ToString();
                _config.Server.Token = TokenBox.Password.Trim();
                if (client.PresentedFingerprint is not null) _config.Server.CertificateFingerprint = client.PresentedFingerprint;
                _config.Save();
                return (client, info);
            }
            catch (ServerException ex) when (!triedHttps && client.BaseUri.Scheme == "http" && client.PresentedFingerprint is null)
            {
                // Server ist vielleicht auf HTTPS umgestellt (Audit S4): dieselbe Adresse mit https versuchen,
                // das Zertifikat bestätigt man im nächsten Durchlauf
                var uri = client.BaseUri;
                client.Dispose();
                triedHttps = true;
                if (!await RespondsWithHttpsAsync(uri))
                {
                    ConnectionText.Text = ex.Message;
                    Log(ex.Message);
                    return null;
                }
                UrlBox.Text = ServerClient.ToHttps(uri).ToString();
                Log(L.T("Server antwortet nur noch verschlüsselt – Adresse auf {0} umgestellt.", UrlBox.Text));
            }
            catch (ServerException ex) when (attempt < 2 && client.PresentedFingerprint is { } fp && ex.Message.StartsWith(L.T("Zertifikat")))
            {
                client.Dispose();
                var ok = MessageBox.Show(this,
                    L.T("Der Server verwendet ein selbst signiertes Zertifikat.\n\nFingerabdruck (SHA-256):\n{0}\n\n", fp) +
                    L.T("Vergleiche ihn mit dem Protokoll des Servers (Zeile „HTTPS-Zertifikat“). Nur bestätigen, wenn er übereinstimmt."),
                    L.T("Zertifikat bestätigen"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                if (!ok) { ConnectionText.Text = L.T("Zertifikat nicht bestätigt."); return null; }
                _pinnedNow = fp;
            }
            catch (ServerException ex)
            {
                client.Dispose();
                ConnectionText.Text = ex.Message;
                Log(ex.Message);
                return null;
            }
        }
        return null;
    }

    /// <summary>Antwortet unter derselben Adresse ein HTTPS-Server (gleich welches Zertifikat)?</summary>
    private static async Task<bool> RespondsWithHttpsAsync(Uri httpUri)
    {
        try
        {
            using var handler = new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(4),
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            };
            using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            using var r = await http.GetAsync(new Uri(ServerClient.ToHttps(httpUri), "api/v1/info"));
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            ConnectionText.Text = L.T("Verbinde …");
            using var c = (await ConnectAsync())?.Client;
            if (c is not null) Log(L.T("Verbindung in Ordnung, Zugangsdaten gespeichert."));
        }
        finally { SetBusy(false); }
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            Log(L.T("Suche BitaxeTuner-Server im lokalen Netz (Port 8484) …"));
            var found = await ServerClient.DiscoverAsync();
            UrlBox.Items.Clear();
            foreach (var (uri, info) in found) UrlBox.Items.Add(uri.ToString());
            if (found.Count > 0) UrlBox.Text = found[0].Uri.ToString();
            Log(found.Count == 0
                ? L.T("Kein Server gefunden. Läuft der Dienst? Sonst Adresse manuell eintragen.")
                : string.Join("\n", found.Select(f => L.T("Gefunden: {0} (v{1}, {2} Miner{3})", f.Uri, f.Info.Version, f.Info.Devices, (f.Info.SetupRequired ? L.T(", noch nicht eingerichtet") : "")))));
        }
        finally { SetBusy(false); }
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ServerClient.Normalize(UrlBox.Text).ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Betriebsart")); }
    }

    // ---------- Lokal → Server ----------

    private async void UploadAndSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null) return;
        if (_host.Hub.Benchmarks.AnyRunning)
        {
            MessageBox.Show(this, L.T("Bitte zuerst alle Benchmarks stoppen."), L.T("Betriebsart"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetBusy(true);
        var paused = false;
        try
        {
            var conn = await ConnectAsync();
            if (conn is not { } c) return;
            using var client = c.Client;
            if (MessageBox.Show(this,
                    L.T("Daten dieses PCs auf den Server {0} übertragen und auf „Server“ umschalten?\n\n", client.BaseUri) +
                    L.T("Übertragen werden: {0} Gerät(e), Verlauf (history.db), Einstellungen, Steuerdaten, Benchmark-Ergebnisse, Sicherungen.\n", _host.Config.Devices.Count) +
                    L.T("Die App fragt ab jetzt keine Miner mehr ab und startet danach neu. Deine lokalen Daten bleiben erhalten."),
                    L.T("Daten übertragen"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            // Ab hier fragt nur noch eine Seite ab: lokal anhalten, dann übertragen
            _host.Hub.SetPaused(true);
            paused = true;
            var progress = new Progress<string>(Log);
            var message = await ServerTransfer.UploadAsync(_host, client,
                text => Dispatcher.Invoke(() => MessageBox.Show(this,
                    text + L.T("\n\nServerdaten durch die Daten dieses PCs ersetzen?"), L.T("Server enthält Daten"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes),
                progress);
            Log(message);
            await client.SetPausedAsync(false);
            Log(L.T("Server-Motor läuft."));
            _config.Server.Enabled = true;
            _config.Save();
            MessageBox.Show(this, message + L.T("\n\nDie App startet jetzt im Modus „Server“ neu."), L.T("Übertragen"), MessageBoxButton.OK, MessageBoxImage.Information);
            ServerTransfer.Restart();
        }
        catch (Exception ex) when (ex is ServerException or OperationCanceledException or System.IO.IOException or System.IO.InvalidDataException)
        {
            Log(L.T("Abgebrochen: ") + ex.Message);
            MessageBox.Show(this, ex.Message, L.T("Übertragung"), MessageBoxButton.OK, MessageBoxImage.Warning);
            if (paused) { _host.Hub.SetPaused(false); Log(L.T("Lokale Abfrage läuft wieder.")); }
        }
        finally { SetBusy(false); }
    }

    private async void SwitchToServer_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var conn = await ConnectAsync();
            if (conn is not { } c) return;
            using var client = c.Client;
            if (MessageBox.Show(this,
                    L.T("Ohne Datenübertragung auf „Server“ umschalten?\n\nDer Server hat {0} Miner eingetragen. ", c.Info.Devices) +
                    L.T("Was dieser PC seit der letzten Übertragung gesammelt hat, bleibt nur hier lokal."),
                    L.T("Umschalten"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            if (c.Info.Paused) await client.SetPausedAsync(false);
            _config.Server.Enabled = true;
            _config.Save();
            ServerTransfer.Restart();
        }
        catch (ServerException ex) { MessageBox.Show(this, ex.Message, L.T("Umschalten"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false); }
    }

    // ---------- Sicherung vom Server holen ----------

    private void Pickup_Changed(object sender, RoutedEventArgs e)
    {
        _config.Server.BackupPickup = PickupCheck.IsChecked == true;
        var folder = PickupFolder.Text.Trim();
        _config.Server.BackupFolder = folder == Core.Backup.BackupPickup.DefaultFolder ? "" : folder;
        _config.Save();
    }

    private void PickupOpen_Click(object sender, RoutedEventArgs e)
    {
        Pickup_Changed(sender, e);
        Services.BackupActions.OpenFolder(this, Core.Backup.BackupPickup.FolderOf(_config.Server));
    }

    private void PickupBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Ordner für Sicherungen vom Server"), InitialDirectory = PickupFolder.Text };
        if (dlg.ShowDialog(this) != true) return;
        PickupFolder.Text = dlg.FolderName;
        Pickup_Changed(sender, e);
    }

    private async void PickupNow_Click(object sender, RoutedEventArgs e)
    {
        Pickup_Changed(sender, e);
        SetBusy(true);
        try
        {
            var conn = await ConnectAsync();
            if (conn is not { } c) return;
            using var client = c.Client;
            Log(L.T("Hole Sicherung vom Server …"));
            var name = await Core.Backup.BackupPickup.RunAsync(client, Core.Backup.BackupPickup.FolderOf(_config.Server), Math.Clamp(_config.Server.BackupKeep, 1, 365), DateTime.Now);
            Log(L.T("Sicherung geprüft und abgelegt: {0}", System.IO.Path.Combine(Core.Backup.BackupPickup.FolderOf(_config.Server), name)));
        }
        catch (Exception ex) when (ex is ServerException or System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            Log(L.T("Sicherung fehlgeschlagen: ") + ex.Message);
            MessageBox.Show(this, ex.Message, L.T("Sicherung"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    // ---------- Raspberry Pi vorbereiten ----------

    private sealed record BootDrive(string Root, string Label);

    /// <summary>Wechseldatenträger; Boot-Partitionen eines Pi-Images (config.txt/cmdline.txt) zuerst.</summary>
    private void RefreshDrives()
    {
        var drives = new List<(BootDrive Drive, bool IsPiBoot)>();
        foreach (var d in System.IO.DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != System.IO.DriveType.Removable) continue;
                var root = d.RootDirectory.FullName;
                var piBoot = System.IO.File.Exists(System.IO.Path.Combine(root, "config.txt")) && System.IO.File.Exists(System.IO.Path.Combine(root, "cmdline.txt"));
                var ours = System.IO.Directory.Exists(System.IO.Path.Combine(root, Provisioning.FolderName));
                var label = L.T("{0}  {1}  ({2:0} MB)", root, d.VolumeLabel, d.TotalSize / 1024.0 / 1024.0) +
                            (ours ? L.T(" – BitaxeTuner-Image") : piBoot ? L.T(" – Raspberry Pi OS") : "");
                drives.Add((new BootDrive(root, label), piBoot));
            }
            catch (System.IO.IOException) { /* Laufwerk nicht lesbar */ }
        }
        DriveBox.ItemsSource = drives.OrderByDescending(x => x.IsPiBoot).Select(x => x.Drive).ToList();
        DriveBox.SelectedIndex = drives.Count > 0 ? 0 : -1;
    }

    private void RefreshDrives_Click(object sender, RoutedEventArgs e) => RefreshDrives();

    private async void PreparePi_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null) return;
        if (DriveBox.SelectedItem is not BootDrive drive)
        {
            MessageBox.Show(this, L.T("Keine SD-Karte gefunden. Karte einstecken und „Aktualisieren“ klicken."), L.T("Raspberry Pi vorbereiten"));
            return;
        }
        if (!System.IO.File.Exists(System.IO.Path.Combine(drive.Root, "cmdline.txt")))
        {
            MessageBox.Show(this, L.T("{0} ist keine Boot-Partition eines Raspberry-Pi-Images (cmdline.txt fehlt).", drive.Root), L.T("Raspberry Pi vorbereiten"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (PiPassword1.Password.Length < 10 || PiPassword1.Password != PiPassword2.Password)
        {
            MessageBox.Show(this, L.T("Das Admin-Passwort braucht mindestens 10 Zeichen und beide Eingaben müssen übereinstimmen."), L.T("Raspberry Pi vorbereiten"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PiOsOptions? os = null;
        if (PiSetupOs.IsChecked == true)
        {
            try
            {
                // SSH-Schlüssel dieses PCs (bei Bedarf einmalig erzeugen) – Anmeldung am Pi ohne Passwort
                string[]? keys = PiSshKey.IsChecked == true ? [await SshKeyService.EnsureKeyAsync()] : null;
                os = new PiOsOptions(PiUser.Text.Trim(), PiUserPassword.Password,
                    PiWifiSsid.Text.Trim() is { Length: > 0 } ssid ? ssid : null, PiWifiPassword.Password,
                    Country: PiCountry.Text.Trim().ToUpperInvariant(), Timezone: PiTimezone.Text.Trim(),
                    Keyboard: PiKeyboard.Text.Trim().ToLowerInvariant(), SshKeys: keys);
                PiOsSetup.Validate(os);
                if (!PiOsSetup.Supports(drive.Root))
                    throw new InvalidOperationException(L.T("Auf dieser Karte fehlt cloud-init (meta-data). Bitte das BitaxeTuner-Image ab Version 0.3.0 verwenden."));
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, L.T("Raspberry Pi vorbereiten"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (os.WifiSsid is null && MessageBox.Show(this,
                    L.T("Kein WLAN eingetragen – der Pi ist dann nur mit LAN-Kabel erreichbar. Trotzdem fortfahren?"),
                    L.T("Raspberry Pi vorbereiten"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        var withData = PiIncludeData.IsChecked == true;
        if (MessageBox.Show(this,
                L.T("Einrichtungspaket nach {0}{1} schreiben?\n\n", drive.Root, Provisioning.FolderName) +
                (os is not null ? L.T("Zugang: Benutzer „{0}“, {1}, SSH an, Hostname bitaxetuner.\n", os.User, (os.WifiSsid is { } w ? L.T("WLAN „{0}“", w) : L.T("nur LAN"))) : "") +
                (withData ? L.T("Mitgegeben werden {0} Gerät(e), Verlauf, Einstellungen, Steuerdaten und Ergebnisse (Kopie – lokal bleibt alles erhalten).\n", _host.Config.Devices.Count) : L.T("Ohne Daten – der Pi startet leer.\n")) +
                L.T("Der Pi startet pausiert. Diese App fragt die Miner weiter ab, bis du auf „Server“ umschaltest."),
                L.T("Raspberry Pi vorbereiten"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        SetBusy(true);
        try
        {
            Log(L.T("Schreibe Einrichtungspaket nach {0} …", drive.Root));
            _host.Config.Save();
            var password = PiPassword1.Password;
            var version = ViewModels.MainViewModel.CurrentVersion.ToString(3);
            if (os is not null)
            {
                await Task.Run(() => PiOsSetup.Write(drive.Root, os));
                Log(L.T("Zugang geschrieben: Benutzer „{0}“, {1}, SSH an.", os.User, (os.WifiSsid is { } s ? L.T("WLAN „{0}“", s) : L.T("nur LAN"))));
                if (os.SshKeys is { Count: > 0 }) Log(L.T("SSH-Schlüssel dieses PCs eingetragen – Anmeldung ohne Passwort."));
                _config.Server.SshUser = os.User;
                _config.Server.SshHost = "";
                SshUserBox.Text = os.User;
                SshHostBox.Text = "";
            }
            var token = await Task.Run(() => Provisioning.Write(drive.Root, password, withData ? _host.DataDirectory : null, withData ? _host.History : null, version));
            PiPassword1.Clear();
            PiPassword2.Clear();
            PiUserPassword.Clear();
            PiWifiPassword.Clear();
            // Neue Installationen starten mit HTTPS (Audit S4); den Fingerabdruck bestätigt man beim ersten Verbinden
            _config.Server.Url = "https://bitaxetuner.local:8484/";
            _config.Server.Token = token;
            _config.Server.CertificateFingerprint = null;
            _config.Save();
            UrlBox.Text = _config.Server.Url;
            TokenBox.Password = token;
            Log(L.T("Einrichtungspaket geschrieben, Adresse und Token für die App gespeichert."));
            MessageBox.Show(this,
                L.T("Fertig. SD-Karte sicher auswerfen, in den Pi stecken und starten (der erste Start dauert einige Minuten).\n\n") +
                L.T("Danach hier „Verbindung testen“ und „Nur umschalten“ – erst dann fragt der Pi die Miner ab.\n") +
                L.T("Wird bitaxetuner.local nicht gefunden, die IP-Adresse des Pi eintragen (Router-Übersicht)."),
                L.T("Raspberry Pi vorbereiten"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or System.IO.InvalidDataException)
        {
            Log(L.T("Abgebrochen: ") + ex.Message);
            MessageBox.Show(this, ex.Message, L.T("Raspberry Pi vorbereiten"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    // ---------- SSH zum Pi ----------

    private (string User, string Host)? SshTarget()
    {
        var user = SshUserBox.Text.Trim();
        var host = SshHostBox.Text.Trim() is { Length: > 0 } h ? h : SshKeyService.HostOf(UrlBox.Text.Trim()) ?? "";
        try
        {
            SshKeyService.Validate(user, host);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, L.T("SSH-Terminal zum Server"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        _config.Server.SshUser = user;
        _config.Server.SshHost = SshHostBox.Text.Trim();
        _config.Save();
        return (user, host);
    }

    private async void SshOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SshTarget() is not { } t) return;
        try
        {
            await SshKeyService.EnsureKeyAsync();
            SshKeyService.OpenTerminal(t.User, t.Host);
            Log(L.T("SSH-Terminal zu {0}@{1} geöffnet.", t.User, t.Host));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            MessageBox.Show(this, ex.Message, L.T("SSH-Terminal zum Server"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SshInstall_Click(object sender, RoutedEventArgs e)
    {
        if (SshTarget() is not { } t) return;
        if (MessageBox.Show(this,
                L.T("Den SSH-Schlüssel dieses PCs bei {0}@{1} eintragen?\n\nEs öffnet sich ein Konsolenfenster; dort einmal das Passwort des Servers eingeben. Danach meldet sich „SSH-Terminal öffnen“ ohne Passwort an.", t.User, t.Host),
                L.T("SSH-Terminal zum Server"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            await SshKeyService.EnsureKeyAsync();
            SshKeyService.InstallOnPi(t.User, t.Host);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            MessageBox.Show(this, ex.Message, L.T("SSH-Terminal zum Server"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Server → Lokal ----------

    private async void DownloadAndSwitch_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        ServerClient? client = null;
        var pausedServer = false;
        try
        {
            var conn = await ConnectAsync();
            if (conn is not { } c) return;
            client = c.Client;
            if (MessageBox.Show(this,
                    L.T("Daten vom Server holen und auf „Lokal“ umschalten?\n\n") +
                    L.T("• Der Server wird pausiert (laufende Benchmarks dort werden gestoppt und die Einstellungen wiederhergestellt).\n") +
                    L.T("• Der komplette Stand des Servers ersetzt die lokalen Daten; der bisherige lokale Stand wird vorher gesichert.\n") +
                    L.T("• Die App startet danach neu und fragt die Miner selbst ab – nur solange dieser PC läuft."),
                    L.T("Daten holen"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            await client.SetPausedAsync(true);
            pausedServer = true;
            Log(L.T("Server pausiert."));
            await ServerTransfer.DownloadAsync(client, DataPaths.Current, new Progress<string>(Log));
            _config.Server.Enabled = false;
            _config.Save();
            MessageBox.Show(this, L.T("Daten geprüft und bereitgestellt. Die App startet jetzt neu und übernimmt sie (mit Sicherung des bisherigen Stands)."),
                L.T("Daten holen"), MessageBoxButton.OK, MessageBoxImage.Information);
            ServerTransfer.Restart();
        }
        catch (Exception ex) when (ex is ServerException or System.IO.IOException or System.IO.InvalidDataException)
        {
            Log(L.T("Abgebrochen: ") + ex.Message);
            MessageBox.Show(this, ex.Message, L.T("Daten holen"), MessageBoxButton.OK, MessageBoxImage.Warning);
            if (pausedServer && client is not null)
            {
                try { await client.SetPausedAsync(false); Log(L.T("Server läuft wieder.")); }
                catch (ServerException) { Log(L.T("Server konnte nicht fortgesetzt werden – bitte in seiner Oberfläche fortsetzen.")); }
            }
        }
        finally
        {
            client?.Dispose();
            SetBusy(false);
        }
    }

    private async void SwitchToLocal_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            ServerClient? client = null;
            try { client = new ServerClient(_config.Server.Url, _config.Server.Token, _config.Server.CertificateFingerprint); await client.InfoAsync(); }
            catch (ServerException) { client?.Dispose(); client = null; }

            var text = client is null
                ? L.T("Der Server ist gerade nicht erreichbar. Trotzdem auf „Lokal“ umschalten?\n\nWICHTIG: Stoppe oder pausiere den Server, sobald er wieder läuft – sonst fragen beide die Miner ab.")
                : L.T("Auf „Lokal“ umschalten, ohne Daten zu holen?\n\nDer Server wird pausiert. Was er seit der letzten Übertragung gesammelt hat, bleibt nur auf dem Server.");
            if (MessageBox.Show(this, text, L.T("Umschalten"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            if (client is not null)
            {
                try { await client.SetPausedAsync(true); }
                catch (ServerException ex)
                {
                    if (MessageBox.Show(this, L.T("Server konnte nicht pausiert werden: {0}\n\nTrotzdem umschalten?", ex.Message), L.T("Umschalten"),
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                }
                client.Dispose();
            }
            _config.Server.Enabled = false;
            _config.Save();
            ServerTransfer.Restart();
        }
        finally { SetBusy(false); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
