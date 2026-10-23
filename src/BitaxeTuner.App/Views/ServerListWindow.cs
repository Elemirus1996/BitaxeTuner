using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Transfer;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.24 Mehrere Server: weitere Server hinzufügen (Name, Adresse, API-Token – geprüft, selbst signiertes Zertifikat wird
/// wie unter „Betriebsart …“ per Fingerabdruck bestätigt) und entfernen. Der aktive Server bleibt unter „Betriebsart …“.
/// </summary>
public sealed class ServerListWindow : Window
{
    private readonly AppConfig _config;
    private readonly ListBox _list = new() { Height = 160, Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBox _name = new() { Margin = new Thickness(0, 2, 0, 6) };
    private readonly TextBox _url = new() { Margin = new Thickness(0, 2, 0, 6) };
    private readonly PasswordBox _token = new() { Margin = new Thickness(0, 2, 0, 6) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _add = new() { Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _remove = new() { Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };

    /// <summary>Liste geändert (Umschalter neu aufbauen).</summary>
    public bool Changed { get; private set; }

    public ServerListWindow(AppConfig config)
    {
        _config = config;
        Title = L.T("Server verwalten");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        TextBlock Label(string text) => new() { Text = text, FontSize = 11, Opacity = 0.75 };
        _add.Content = L.T("Prüfen und hinzufügen");
        _remove.Content = L.T("Entfernen");
        _add.Click += async (_, _) => await AddAsync();
        _remove.Click += (_, _) => Remove();
        _list.SelectionChanged += (_, _) => _remove.IsEnabled = _list.SelectedIndex > 0;
        System.Windows.Automation.AutomationProperties.SetName(_name, L.T("Name"));
        System.Windows.Automation.AutomationProperties.SetName(_url, L.T("Adresse"));
        System.Windows.Automation.AutomationProperties.SetName(_token, L.T("API-Token"));
        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Children =
            {
                new TextBlock { Text = L.T("Server dieser App – der erste ist der aktive. Umschalten oben im Fenster, dort auch „Alle Server“ mit Summen."), TextWrapping = TextWrapping.Wrap },
                _list,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _remove } },
                new Separator { Margin = new Thickness(0, 12, 0, 8) },
                new TextBlock { Text = L.T("Weiteren Server hinzufügen"), FontWeight = FontWeights.SemiBold },
                Label(L.T("NAME (Z. B. STANDORT)")), _name,
                Label(L.T("ADRESSE (z. B. https://raspberrypi.local:8484)")), _url,
                Label(L.T("API-TOKEN (Server-Oberfläche → Einstellungen → Desktop-App verbinden)")), _token,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _add } },
                _status,
            },
        };
        Fill();
    }

    private void Fill()
    {
        var all = MultiServer.All(_config);
        _list.ItemsSource = all.Select((s, i) => (i == 0 ? "● " : "   ") + MultiServer.NameOf(s) + "  ·  " + s.Url).ToList();
        _remove.IsEnabled = false;
    }

    private void Remove()
    {
        var i = _list.SelectedIndex;
        if (i <= 0) return;
        var s = MultiServer.All(_config)[i];
        if (MessageBox.Show(this, L.T("„{0}“ aus der Liste entfernen? Am Server selbst ändert sich nichts.", MultiServer.NameOf(s)), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        MultiServer.Remove(_config, i);
        _config.Save();
        Changed = true;
        Fill();
    }

    private async Task AddAsync()
    {
        _add.IsEnabled = false;
        _status.Text = L.T("Prüfe Verbindung …");
        string? pinned = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var client = new ServerClient(_url.Text, _token.Password, pinned);
                try
                {
                    var info = await client.InfoAsync();
                    if (info.Name != "BitaxeTuner-Server") throw new ServerException(L.T("Unter dieser Adresse läuft kein BitaxeTuner-Server."));
                    if (info.ApiVersion != ServerClient.SupportedApiVersion)
                        throw new ServerException(L.T("Server-Version {0} passt nicht zu dieser App ({1}). Bitte beide aktualisieren.", info.Version, ViewModels.MainViewModel.CurrentVersion.ToString(3)));
                    if (string.IsNullOrWhiteSpace(_token.Password))
                        throw new ServerException(L.T("Bitte ein API-Token eintragen (Server-Oberfläche → Einstellungen → Desktop-App verbinden)."));
                    await client.StatusAsync();   // prüft das Token
                    MultiServer.Add(_config, new ServerConnectionSettings
                    {
                        Name = _name.Text.Trim(),
                        Url = client.BaseUri.ToString(),
                        Token = _token.Password.Trim(),
                        CertificateFingerprint = client.PresentedFingerprint,
                    });
                    _config.Save();
                    Changed = true;
                    _status.Text = L.T("Hinzugefügt: {0} {1} · {2} Miner.", info.Name, info.Version, info.Devices);
                    _name.Clear(); _url.Clear(); _token.Clear();
                    Fill();
                    return;
                }
                catch (ServerException) when (attempt == 0 && client.PresentedFingerprint is { } fp && pinned is null)
                {
                    if (MessageBox.Show(this,
                            L.T("Der Server verwendet ein selbst signiertes Zertifikat.\n\nFingerabdruck (SHA-256):\n{0}\n\n", fp) +
                            L.T("Vergleiche ihn mit dem Protokoll des Servers (Zeile „HTTPS-Zertifikat“). Nur bestätigen, wenn er übereinstimmt."),
                            L.T("Zertifikat bestätigen"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        _status.Text = L.T("Zertifikat nicht bestätigt.");
                        return;
                    }
                    pinned = fp;
                }
            }
        }
        catch (Exception ex) when (ex is ServerException or LocalizedException or UriFormatException or ArgumentException)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _add.IsEnabled = true;
        }
    }
}
