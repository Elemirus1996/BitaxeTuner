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
