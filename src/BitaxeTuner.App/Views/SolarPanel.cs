using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.17: Quelle der PV-Leistung wie in der Server-Oberfläche (Karte „PV-Anlage“): Messsteckdose oder Home Assistant
/// (darüber auch Anker Solix). Gespeichert mit „Speichern“; das Token liegt nur in secrets.json. Es wird nur gelesen.
/// </summary>
public sealed class SolarPanel : StackPanel
{
    private readonly AppHost _host;
    private readonly ComboBox _source = new() { DisplayMemberPath = "Label", SelectedValuePath = "Value", Margin = new Thickness(0, 0, 0, 6) };
    private readonly ComboBox _plug = new() { DisplayMemberPath = "Label", SelectedValuePath = "Value", Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _url = new() { Margin = new Thickness(0, 0, 0, 6), ToolTip = "http://homeassistant.local:8123" };
    private readonly TextBox _entity = new() { Margin = new Thickness(0, 0, 0, 6), ToolTip = "sensor.pv_power" };
    private readonly PasswordBox _token = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _plugBox = new(), _haBox = new();
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public SolarPanel(AppHost host)
    {
        _host = host;
        var s = host.Hub.Config.Solar;
        Children.Add(Muted(L.T("Für die Automatik-Art „PV-Leistung“: Bei viel Sonne volle Leistung, bei wenig Sonne Stromsparen. BitaxeTuner liest nur, schaltet nichts.")));

        _source.ItemsSource = new[]
        {
            new { Value = "none", Label = L.T("keine") },
            new { Value = "plug", Label = L.T("Messsteckdose (Shelly, Tasmota, FRITZ!DECT)") },
            new { Value = "homeassistant", Label = L.T("Home Assistant (auch Anker Solix)") },
        };
        _source.SelectedValue = SolarSettings.Sources.Contains(s.Source) ? s.Source : "none";
        _source.SelectionChanged += (_, _) => UpdateVisibility();
        Children.Add(Label(L.T("Quelle")));
        Children.Add(_source);

        _plug.ItemsSource = host.Hub.Config.Plugs.Items.Select(p => new { Value = p.Id, Label = p.Name + (p.Role == "pv" ? " ☀" : "") }).ToList();
        _plug.SelectedValue = s.PlugId;
        _plugBox.Children.Add(Label(L.T("Messsteckdose")));
        _plugBox.Children.Add(_plug);
        Children.Add(_plugBox);

        _url.Text = s.HaUrl;
        _entity.Text = s.HaEntity;
        _token.ToolTip = host.Hub.Secrets.Has(MinerHub.SolarTokenKey) ? L.T("gespeichert – leer lassen = unverändert") : L.T("langlebiges Zugriffstoken");
        _haBox.Children.Add(Label(L.T("Adresse")));
        _haBox.Children.Add(_url);
        _haBox.Children.Add(Label(L.T("Sensor (Leistung in W oder kW)")));
        _haBox.Children.Add(_entity);
        _haBox.Children.Add(Label(L.T("Zugriffstoken")));
        _haBox.Children.Add(_token);
        _haBox.Children.Add(Muted(L.T("Token in Home Assistant unter Profil → Sicherheit → Langlebige Zugriffstoken erstellen. Anker Solix: über die Anker-Solix-Integration in Home Assistant, dann deren Sensor für die PV-Leistung eintragen.")));
        Children.Add(_haBox);

        var test = new Button { Content = L.T("Testen"), Padding = new Thickness(12, 4, 12, 4) };
        test.Click += async (_, _) => await TestAsync();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(test);
        row.Children.Add(_result);
        Children.Add(row);
        UpdateVisibility();
    }

    private static TextBlock Label(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 11 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return tb;
    }

    private static TextBlock Muted(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return tb;
    }

    private void UpdateVisibility()
    {
        var src = _source.SelectedValue as string;
        _plugBox.Visibility = src == "plug" ? Visibility.Visible : Visibility.Collapsed;
        _haBox.Visibility = src == "homeassistant" ? Visibility.Visible : Visibility.Collapsed;
    }

    private SolarSettings Read() => new()
    {
        Source = _source.SelectedValue as string ?? "none",
        PlugId = _plug.SelectedValue as string ?? "",
        HaUrl = _url.Text,
        HaEntity = _entity.Text,
    };

    private async Task TestAsync()
    {
        try
        {
            var s = MinerHub.NormalizeSolar(Read(), _host.Hub.Config.Plugs.Items);
            double w = s.Source switch
            {
                "plug" => _host.Hub.PlugSolarW(s.PlugId, DateTime.Now) ?? throw new LocalizedException("Von dieser Messsteckdose liegt gerade kein frischer Messwert vor."),
                "homeassistant" => await _host.Hub.ReadHomeAssistantAsync(s, _token.Password),
                _ => throw new LocalizedException("Bitte zuerst eine Quelle auswählen."),
            };
            _result.Text = L.T("PV-Leistung gelesen: {0} W", w.ToString("0"));
        }
        catch (LocalizedException ex)
        {
            _result.Text = ex.In(Loc.Current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _result.Text = L.T("Fehler: {0}", ex.Message);
        }
    }

    /// <summary>Fehlertext oder null (vor dem Speichern).</summary>
    public string? Validate()
    {
        try
        {
            MinerHub.NormalizeSolar(Read(), _host.Hub.Config.Plugs.Items);
            return null;
        }
        catch (LocalizedException ex)
        {
            return ex.In(Loc.Current);
        }
    }

    /// <summary>In die Konfiguration übernehmen; Token nur, wenn neu eingegeben.</summary>
    public void Apply(AppConfig config)
    {
        var s = MinerHub.NormalizeSolar(Read(), config.Plugs.Items);
        _host.Hub.ApplySolarSettings(s, _token.Password);
        config.Solar = s;
    }
}
