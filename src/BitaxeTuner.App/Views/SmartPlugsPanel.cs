using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Plugs;

namespace BitaxeTuner.App.Views;

/// <summary>
/// Smart Plugs (Shelly) einrichten wie in der Server-Oberfläche: Adresse, Rolle, Miner, Test – eingebettet in die
/// Einstellungen, gespeichert mit „Speichern“. Es wird nur gemessen, nie geschaltet. Passwörter liegen in secrets.json.
/// </summary>
public sealed class SmartPlugsPanel : StackPanel
{
    private sealed class Row
    {
        public required SmartPlugConfig Plug { get; init; }
        public required TextBox Name, Host, Channel, User;
        public required PasswordBox Password;
        public required ComboBox Role;
        public required List<(CheckBox Box, string Host)> Miners;
    }

    private readonly AppHost _host;
    private readonly StackPanel _list = new();
    private readonly List<Row> _rows = [];
    private readonly HashSet<string> _removed = [];
    private readonly CheckBox _useForCosts = new() { Content = L.T("Kosten, Tagesbericht und Gesamteffizienz mit den Werten an der Steckdose rechnen"), Margin = new Thickness(0, 8, 0, 4) };
    private readonly TextBox _interval = new() { Width = 60, Margin = new Thickness(8, 0, 0, 0) };

    private static readonly (string Value, string Label)[] Roles =
    [
        ("miners", L.T("speist Miner")),
        ("other", L.T("Nebenverbraucher (Zusatzlüfter, Pi …)")),
        ("total", L.T("Gesamtmessung (alles dahinter)")),
    ];

    public SmartPlugsPanel(AppHost host)
    {
        _host = host;

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = L.T("Shelly-Steckdosen mit Leistungsmessung (Gen1, Plus/Pro/Gen3) messen den echten Verbrauch inklusive Netzteil und Zusatzlüftern. BitaxeTuner liest nur – geschaltet wird nie."),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var settings = _host.Hub.Config.Plugs;
        _useForCosts.IsChecked = settings.UseForCosts;
        _interval.Text = settings.IntervalSeconds.ToString();
        foreach (var p in settings.Items) AddRow(Copy(p));

        var add = new Button { Content = L.T("Plug hinzufügen"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        add.Click += (_, _) => AddRow(new SmartPlugConfig { Name = L.T("Smart Plug") });

        var intervalRow = new StackPanel { Orientation = Orientation.Horizontal };
        intervalRow.Children.Add(new TextBlock { Text = L.T("abfragen alle (s)"), VerticalAlignment = VerticalAlignment.Center });
        intervalRow.Children.Add(_interval);

        Children.Add(hint);
        Children.Add(_list);
        Children.Add(add);
        Children.Add(_useForCosts);
        Children.Add(intervalRow);
        ShowEmptyHint();
    }

    private static SmartPlugConfig Copy(SmartPlugConfig p) => new()
    {
        Id = p.Id, Name = p.Name, Host = p.Host, Channel = p.Channel, User = p.User, Role = p.Role, Miners = [.. p.Miners],
    };

    private void ShowEmptyHint()
    {
        if (_rows.Count == 0 && _list.Children.Count == 0)
            _list.Children.Add(new TextBlock { Text = L.T("Noch kein Smart Plug eingerichtet."), Margin = new Thickness(0, 4, 0, 4) });
    }

    private void AddRow(SmartPlugConfig plug)
    {
        if (_rows.Count == 0) _list.Children.Clear();
        var status = _host.Hub.PlugStatuses().FirstOrDefault(s => s.Id == plug.Id);
        var hasPassword = _host.Hub.Secrets.Has(SmartPlugConfig.SecretKey(plug.Id));

        var row = new Row
        {
            Plug = plug,
            Name = new TextBox { Text = plug.Name },
            Host = new TextBox { Text = plug.Host },
            Channel = new TextBox { Text = plug.Channel.ToString() },
            User = new TextBox { Text = plug.User },
            Password = new PasswordBox { ToolTip = hasPassword ? L.T("gespeichert – leer lassen = unverändert") : L.T("Passwort (falls nötig)") },
            Role = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Value" },
            Miners = [],
        };
        row.Role.ItemsSource = Roles.Select(r => new { r.Value, r.Label }).ToList();
        row.Role.SelectedValue = plug.Role;

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        for (var c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        void Cell(int r, int c, string label, FrameworkElement input)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 6) };
            var tb = new TextBlock { Text = label, FontSize = 11 };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            sp.Children.Add(tb);
            sp.Children.Add(input);
            Grid.SetRow(sp, r);
            Grid.SetColumn(sp, c);
            grid.Children.Add(sp);
        }
        Cell(0, 0, L.T("Name"), row.Name);
        Cell(0, 1, L.T("Adresse (IP oder Name, „sim“ = Simulation)"), row.Host);
        Cell(0, 2, L.T("Rolle"), row.Role);
        Cell(1, 0, L.T("Benutzer (bei Gen2+ immer admin)"), row.User);
        Cell(1, 1, L.T("Passwort"), row.Password);
        Cell(1, 2, L.T("Kanal"), row.Channel);

        var miners = new WrapPanel();
        foreach (var d in _host.Hub.Devices)
        {
            var box = new CheckBox { Content = d.Title, IsChecked = plug.Miners.Contains(d.Host, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 2, 14, 2) };
            row.Miners.Add((box, d.Host));
            miners.Children.Add(box);
        }
        void UpdateMiners() => miners.Visibility = (row.Role.SelectedValue as string) == "miners" ? Visibility.Visible : Visibility.Collapsed;
        row.Role.SelectionChanged += (_, _) => UpdateMiners();
        UpdateMiners();

        var statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 11 };
        statusText.Text = status is null ? "" : status.Online ? L.T("{0} W", status.PowerW?.ToString("0.0", L.Culture)) + (status.Model is { } m ? " · " + m : "") : status.Error ?? "offline";
        var test = new Button { Content = L.T("Testen"), Padding = new Thickness(10, 2, 10, 2) };
        test.Click += async (_, _) => await TestAsync(row, statusText);
        var remove = new Button { Content = L.T("Entfernen"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        remove.SetResourceReference(StyleProperty, "DangerButton");

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(statusText);
        actions.Children.Add(test);
        actions.Children.Add(remove);
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(new TextBlock { Text = plug.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var card = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        var content = new StackPanel();
        content.Children.Add(head);
        content.Children.Add(grid);
        content.Children.Add(miners);
        card.Child = content;

        remove.Click += (_, _) =>
        {
            _rows.Remove(row);
            _list.Children.Remove(card);
            _removed.Add(plug.Id);
            ShowEmptyHint();
        };
        _rows.Add(row);
        _list.Children.Add(card);
    }

    private SmartPlugConfig Read(Row r) => new()
    {
        Id = r.Plug.Id,
        Name = string.IsNullOrWhiteSpace(r.Name.Text) ? L.T("Smart Plug") : r.Name.Text.Trim(),
        Host = r.Host.Text.Trim(),
        Channel = int.TryParse(r.Channel.Text, out var ch) ? Math.Clamp(ch, 0, 3) : 0,
        User = string.IsNullOrWhiteSpace(r.User.Text) ? "admin" : r.User.Text.Trim(),
        Role = r.Role.SelectedValue as string ?? "miners",
        Miners = (r.Role.SelectedValue as string) == "miners" ? r.Miners.Where(m => m.Box.IsChecked == true).Select(m => m.Host).ToList() : [],
    };

    private async Task TestAsync(Row row, TextBlock statusText)
    {
        var p = Read(row);
        try
        {
            IsEnabled = false;
            if (!SimulatedPlugClient.IsSimAddress(p.Host)) ShellyClient.BaseUri(p.Host);
            var password = row.Password.Password.Length > 0 ? row.Password.Password : _host.Hub.Secrets.Get(SmartPlugConfig.SecretKey(p.Id));
            using IPlugClient client = SimulatedPlugClient.IsSimAddress(p.Host) ? new SimulatedPlugClient(() => 20) : new ShellyClient(p.Host, p.User, password);
            var id = await client.IdentifyAsync();
            var r = await client.ReadAsync(p.Channel);
            statusText.Text = L.T("{0} (Gen {1}): {2} W", id.Model, id.Generation, r.PowerW.ToString("0.0", L.Culture)) +
                              (id.InsecureAuth ? "\n" + PlugIdentity.InsecureAuthHint : "");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            statusText.Text = ex is TaskCanceledException ? L.T("keine Antwort") : ex.Message;
        }
        finally { IsEnabled = true; }
    }

    /// <summary>Fehlertext, wenn die Eingaben nicht gespeichert werden können; sonst null.</summary>
    public string? Validate()
    {
        var items = _rows.Select(Read).ToList();
        try
        {
            foreach (var p in items.Where(p => !SimulatedPlugClient.IsSimAddress(p.Host))) ShellyClient.BaseUri(p.Host);
            if (items.Count(p => p.Role == "total") > 1) throw new LocalizedException("Höchstens ein Plug als Gesamtmessung.");
            return null;
        }
        catch (LocalizedException ex)
        {
            return ex.In(Loc.Current);
        }
    }

    /// <summary>In die Konfiguration übernehmen und Passwörter in secrets.json schreiben (Speichern der Einstellungen).</summary>
    public void Apply(AppConfig config)
    {
        config.Plugs = new SmartPlugSettings
        {
            Items = _rows.Select(Read).ToList(),
            UseForCosts = _useForCosts.IsChecked == true,
            IntervalSeconds = int.TryParse(_interval.Text, out var s) ? Math.Clamp(s, 5, 300) : 10,
        };
        var secrets = _host.Hub.Secrets;
        foreach (var id in _removed) secrets.Set(SmartPlugConfig.SecretKey(id), null);
        foreach (var r in _rows.Where(r => r.Password.Password.Length > 0))
            secrets.Set(SmartPlugConfig.SecretKey(r.Plug.Id), r.Password.Password);
    }
}
