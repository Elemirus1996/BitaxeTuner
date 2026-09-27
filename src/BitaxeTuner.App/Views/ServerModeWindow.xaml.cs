using System.Diagnostics;
using System.Windows;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Transfer;

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
            ? $"Aktuell: Server ({config.Server.Url}) – dieser PC fragt keine Miner ab"
            : "Aktuell: Lokal – dieser PC fragt die Miner ab";
        ToServerCard.Visibility = serverMode ? Visibility.Collapsed : Visibility.Visible;
        ToLocalCard.Visibility = serverMode ? Visibility.Visible : Visibility.Collapsed;
        PiCard.Visibility = serverMode ? Visibility.Collapsed : Visibility.Visible;
        if (!serverMode) RefreshDrives();
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
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var client = CreateClient();
            try
            {
                var info = await client.InfoAsync();
                if (info.Name != "BitaxeTuner-Server") throw new ServerException("Unter dieser Adresse läuft kein BitaxeTuner-Server.");
                if (info.ApiVersion != ServerClient.SupportedApiVersion)
                    throw new ServerException($"Server-Version {info.Version} passt nicht zu dieser App ({ViewModels.MainViewModel.CurrentVersion.ToString(3)}). Bitte beide aktualisieren.");
                if (info.SetupRequired)
                    throw new ServerException("Der Server ist noch nicht eingerichtet. Öffne ihn im Browser und lege das Admin-Passwort fest (Einrichtungs-Code im Protokoll des Dienstes).");
                if (string.IsNullOrWhiteSpace(TokenBox.Password))
                    throw new ServerException("Bitte ein API-Token eintragen (Server-Oberfläche → Einstellungen → Desktop-App verbinden).");
                await client.StatusAsync(); // prüft das Token
                ConnectionText.Text = $"Verbunden: {info.Name} {info.Version} · {info.Devices} Miner · {info.Os}" +
                                      (info.Paused ? " · Motor PAUSIERT" : " · Motor läuft") +
                                      (client.PresentedFingerprint is { } fp ? $"\nZertifikat: {fp}" : "");
                _config.Server.Url = client.BaseUri.ToString();
                _config.Server.Token = TokenBox.Password.Trim();
                if (client.PresentedFingerprint is not null) _config.Server.CertificateFingerprint = client.PresentedFingerprint;
                _config.Save();
                return (client, info);
            }
            catch (ServerException ex) when (attempt == 0 && client.PresentedFingerprint is { } fp && ex.Message.StartsWith("Zertifikat"))
            {
                client.Dispose();
                var ok = MessageBox.Show(this,
                    $"Der Server verwendet ein selbst signiertes Zertifikat.\n\nFingerabdruck (SHA-256):\n{fp}\n\n" +
                    "Vergleiche ihn mit dem Protokoll des Servers (Zeile „HTTPS-Zertifikat“). Nur bestätigen, wenn er übereinstimmt.",
                    "Zertifikat bestätigen", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                if (!ok) { ConnectionText.Text = "Zertifikat nicht bestätigt."; return null; }
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

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            ConnectionText.Text = "Verbinde …";
            using var c = (await ConnectAsync())?.Client;
            if (c is not null) Log("Verbindung in Ordnung, Zugangsdaten gespeichert.");
        }
        finally { SetBusy(false); }
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            Log("Suche BitaxeTuner-Server im lokalen Netz (Port 8484) …");
            var found = await ServerClient.DiscoverAsync();
            UrlBox.Items.Clear();
            foreach (var (uri, info) in found) UrlBox.Items.Add(uri.ToString());
            if (found.Count > 0) UrlBox.Text = found[0].Uri.ToString();
            Log(found.Count == 0
                ? "Kein Server gefunden. Läuft der Dienst? Sonst Adresse manuell eintragen."
                : string.Join("\n", found.Select(f => $"Gefunden: {f.Uri} (v{f.Info.Version}, {f.Info.Devices} Miner{(f.Info.SetupRequired ? ", noch nicht eingerichtet" : "")})")));
        }
        finally { SetBusy(false); }
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ServerClient.Normalize(UrlBox.Text).ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Betriebsart"); }
    }

    // ---------- Lokal → Server ----------

    private async void UploadAndSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null) return;
        if (_host.Hub.Benchmarks.AnyRunning)
        {
            MessageBox.Show(this, "Bitte zuerst alle Benchmarks stoppen.", "Betriebsart", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    $"Daten dieses PCs auf den Server {client.BaseUri} übertragen und auf „Server“ umschalten?\n\n" +
                    $"Übertragen werden: {_host.Config.Devices.Count} Gerät(e), Verlauf (history.db), Einstellungen, Steuerdaten, Benchmark-Ergebnisse, Sicherungen.\n" +
                    "Die App fragt ab jetzt keine Miner mehr ab und startet danach neu. Deine lokalen Daten bleiben erhalten.",
                    "Daten übertragen", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            // Ab hier fragt nur noch eine Seite ab: lokal anhalten, dann übertragen
            _host.Hub.SetPaused(true);
            paused = true;
            var progress = new Progress<string>(Log);
            var message = await ServerTransfer.UploadAsync(_host, client,
                text => Dispatcher.Invoke(() => MessageBox.Show(this,
                    text + "\n\nServerdaten durch die Daten dieses PCs ersetzen?", "Server enthält Daten",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes),
                progress);
            Log(message);
            await client.SetPausedAsync(false);
            Log("Server-Motor läuft.");
            _config.Server.Enabled = true;
            _config.Save();
            MessageBox.Show(this, message + "\n\nDie App startet jetzt im Modus „Server“ neu.", "Übertragen", MessageBoxButton.OK, MessageBoxImage.Information);
            ServerTransfer.Restart();
        }
        catch (Exception ex) when (ex is ServerException or OperationCanceledException or System.IO.IOException or System.IO.InvalidDataException)
        {
            Log("Abgebrochen: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Übertragung", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (paused) { _host.Hub.SetPaused(false); Log("Lokale Abfrage läuft wieder."); }
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
                    $"Ohne Datenübertragung auf „Server“ umschalten?\n\nDer Server hat {c.Info.Devices} Miner eingetragen. " +
                    "Was dieser PC seit der letzten Übertragung gesammelt hat, bleibt nur hier lokal.",
                    "Umschalten", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            if (c.Info.Paused) await client.SetPausedAsync(false);
            _config.Server.Enabled = true;
            _config.Save();
            ServerTransfer.Restart();
        }
        catch (ServerException ex) { MessageBox.Show(this, ex.Message, "Umschalten", MessageBoxButton.OK, MessageBoxImage.Warning); }
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
                var label = $"{root}  {d.VolumeLabel}  ({d.TotalSize / 1024.0 / 1024.0:0} MB)" +
                            (ours ? " – BitaxeTuner-Image" : piBoot ? " – Raspberry Pi OS" : "");
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
            MessageBox.Show(this, "Keine SD-Karte gefunden. Karte einstecken und „Aktualisieren“ klicken.", "Raspberry Pi vorbereiten");
            return;
        }
        if (!System.IO.File.Exists(System.IO.Path.Combine(drive.Root, "cmdline.txt")))
        {
            MessageBox.Show(this, $"{drive.Root} ist keine Boot-Partition eines Raspberry-Pi-Images (cmdline.txt fehlt).", "Raspberry Pi vorbereiten",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (PiPassword1.Password.Length < 10 || PiPassword1.Password != PiPassword2.Password)
        {
            MessageBox.Show(this, "Das Admin-Passwort braucht mindestens 10 Zeichen und beide Eingaben müssen übereinstimmen.", "Raspberry Pi vorbereiten",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PiOsOptions? os = null;
        if (PiSetupOs.IsChecked == true)
        {
            os = new PiOsOptions(PiUser.Text.Trim(), PiUserPassword.Password,
                PiWifiSsid.Text.Trim() is { Length: > 0 } ssid ? ssid : null, PiWifiPassword.Password);
            try
            {
                PiOsSetup.Validate(os);
                if (!PiOsSetup.Supports(drive.Root))
                    throw new InvalidOperationException("Auf dieser Karte fehlt cloud-init (meta-data). Bitte das BitaxeTuner-Image ab Version 0.3.0 verwenden.");
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "Raspberry Pi vorbereiten", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (os.WifiSsid is null && MessageBox.Show(this,
                    "Kein WLAN eingetragen – der Pi ist dann nur mit LAN-Kabel erreichbar. Trotzdem fortfahren?",
                    "Raspberry Pi vorbereiten", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        var withData = PiIncludeData.IsChecked == true;
        if (MessageBox.Show(this,
                $"Einrichtungspaket nach {drive.Root}{Provisioning.FolderName} schreiben?\n\n" +
                (os is not null ? $"Zugang: Benutzer „{os.User}“, {(os.WifiSsid is { } w ? $"WLAN „{w}“" : "nur LAN")}, SSH an, Hostname bitaxetuner.\n" : "") +
                (withData ? $"Mitgegeben werden {_host.Config.Devices.Count} Gerät(e), Verlauf, Einstellungen, Steuerdaten und Ergebnisse (Kopie – lokal bleibt alles erhalten).\n" : "Ohne Daten – der Pi startet leer.\n") +
                "Der Pi startet pausiert. Diese App fragt die Miner weiter ab, bis du auf „Server“ umschaltest.",
                "Raspberry Pi vorbereiten", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        SetBusy(true);
        try
        {
            Log($"Schreibe Einrichtungspaket nach {drive.Root} …");
            _host.Config.Save();
            var password = PiPassword1.Password;
            var version = ViewModels.MainViewModel.CurrentVersion.ToString(3);
            if (os is not null)
            {
                await Task.Run(() => PiOsSetup.Write(drive.Root, os));
                Log($"Zugang geschrieben: Benutzer „{os.User}“, {(os.WifiSsid is { } s ? $"WLAN „{s}“" : "nur LAN")}, SSH an.");
            }
            var token = await Task.Run(() => Provisioning.Write(drive.Root, password, withData ? _host.DataDirectory : null, withData ? _host.History : null, version));
            PiPassword1.Clear();
            PiPassword2.Clear();
            PiUserPassword.Clear();
            PiWifiPassword.Clear();
            _config.Server.Url = "http://bitaxetuner.local:8484/";
            _config.Server.Token = token;
            _config.Server.CertificateFingerprint = null;
            _config.Save();
            UrlBox.Text = _config.Server.Url;
            TokenBox.Password = token;
            Log("Einrichtungspaket geschrieben, Adresse und Token für die App gespeichert.");
            MessageBox.Show(this,
                "Fertig. SD-Karte sicher auswerfen, in den Pi stecken und starten (der erste Start dauert einige Minuten).\n\n" +
                "Danach hier „Verbindung testen“ und „Nur umschalten“ – erst dann fragt der Pi die Miner ab.\n" +
                "Wird bitaxetuner.local nicht gefunden, die IP-Adresse des Pi eintragen (Router-Übersicht).",
                "Raspberry Pi vorbereiten", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or System.IO.InvalidDataException)
        {
            Log("Abgebrochen: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Raspberry Pi vorbereiten", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
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
                    $"Daten vom Server holen und auf „Lokal“ umschalten?\n\n" +
                    "• Der Server wird pausiert (laufende Benchmarks dort werden gestoppt und die Einstellungen wiederhergestellt).\n" +
                    "• Der komplette Stand des Servers ersetzt die lokalen Daten; der bisherige lokale Stand wird vorher gesichert.\n" +
                    "• Die App startet danach neu und fragt die Miner selbst ab – nur solange dieser PC läuft.",
                    "Daten holen", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            await client.SetPausedAsync(true);
            pausedServer = true;
            Log("Server pausiert.");
            await ServerTransfer.DownloadAsync(client, DataPaths.Current, new Progress<string>(Log));
            _config.Server.Enabled = false;
            _config.Save();
            MessageBox.Show(this, "Daten geprüft und bereitgestellt. Die App startet jetzt neu und übernimmt sie (mit Sicherung des bisherigen Stands).",
                "Daten holen", MessageBoxButton.OK, MessageBoxImage.Information);
            ServerTransfer.Restart();
        }
        catch (Exception ex) when (ex is ServerException or System.IO.IOException or System.IO.InvalidDataException)
        {
            Log("Abgebrochen: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Daten holen", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (pausedServer && client is not null)
            {
                try { await client.SetPausedAsync(false); Log("Server läuft wieder."); }
                catch (ServerException) { Log("Server konnte nicht fortgesetzt werden – bitte in seiner Oberfläche fortsetzen."); }
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
                ? "Der Server ist gerade nicht erreichbar. Trotzdem auf „Lokal“ umschalten?\n\nWICHTIG: Stoppe oder pausiere den Server, sobald er wieder läuft – sonst fragen beide die Miner ab."
                : "Auf „Lokal“ umschalten, ohne Daten zu holen?\n\nDer Server wird pausiert. Was er seit der letzten Übertragung gesammelt hat, bleibt nur auf dem Server.";
            if (MessageBox.Show(this, text, "Umschalten", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            if (client is not null)
            {
                try { await client.SetPausedAsync(true); }
                catch (ServerException ex)
                {
                    if (MessageBox.Show(this, $"Server konnte nicht pausiert werden: {ex.Message}\n\nTrotzdem umschalten?", "Umschalten",
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
