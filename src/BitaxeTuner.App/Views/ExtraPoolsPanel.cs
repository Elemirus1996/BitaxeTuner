using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Pools;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.20: weitere Pool-Konten wie in der Server-Oberfläche (public-pool, solo.ckpool, OCEAN, Braiins, ViaBTC) – nur lesend.
/// Gespeichert mit „Speichern“; Schlüssel nur in secrets.json (leer = unverändert).
/// </summary>
public sealed class ExtraPoolsPanel : StackPanel
{
    private sealed class Row
    {
        public required ExtraPoolAccount Account { get; init; }
        public required ComboBox Provider, Coin;
        public required TextBox Name, Address, BaseUrl;
        public required PasswordBox Key;
        public required CheckBox Enabled, Tax, Notify;
        public required TextBlock Status;
    }

    private static readonly (string Value, string Label)[] Providers =
    [
        ("public-pool", L.T("public-pool.io (Solo, je Adresse)")),
        ("ckpool", L.T("solo.ckpool.org (Solo, je Adresse)")),
        ("ocean", L.T("OCEAN (je Adresse)")),
        ("braiins", L.T("Braiins Pool (Zugangs-Token)")),
        ("viabtc", L.T("ViaBTC (API-Schlüssel)")),
    ];

    private readonly AppHost _host;
    private readonly StackPanel _list = new();
    private readonly List<Row> _rows = [];

    public ExtraPoolsPanel(AppHost host)
    {
        _host = host;
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = L.T("Neben Mining-Dutch: Solo-Pools (public-pool, solo.ckpool) zeigen Hashrate, Worker und beste Shares je Wallet-Adresse; OCEAN, Braiins und ViaBTC zusätzlich Guthaben, Erträge und Auszahlungen – auf Wunsch als Zuflüsse in die Steuer (nur ergänzend). Nur lesend; Schlüssel bleiben auf dem Server. Bei ViaBTC die IP-Freigabe für den Server eintragen."),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        Children.Add(hint);
        Children.Add(_list);
        foreach (var v in host.Hub.ExtraPools) AddRow(v.Account.Clone(), v.KeySet, StatusText(v.Status, v.Error));
        var add = new Button { Content = L.T("Pool-Konto hinzufügen"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        add.Click += (_, _) => AddRow(new ExtraPoolAccount(), false, L.T("noch nicht abgefragt"));
        Children.Add(add);
    }

    private static string Hash(double? gh) => gh is not { } g ? "–" : g >= 1000 ? $"{(g / 1000).ToString("0.00", CultureInfo.CurrentCulture)} TH/s" : $"{g.ToString("0.0", CultureInfo.CurrentCulture)} GH/s";

    private static string StatusText(ExtraPoolStatus? s, string? error)
    {
        if (error is not null) return error;
        if (s is null) return L.T("noch nicht abgefragt");
        var parts = new List<string> { L.T("Hashrate {0}", Hash(s.HashrateGh)), L.T("{0} Worker aktiv", s.ActiveWorkers) };
        if (s.BestDifficulty is { } bd) parts.Add(L.T("Best Diff {0}", bd.ToString("N0", CultureInfo.CurrentCulture)));
        if (s.Balance is { } b) parts.Add(L.T("Guthaben {0} {1}", b, s.Coin));
        if (s.Payouts.OrderByDescending(p => p.TimeUtc).FirstOrDefault() is { } p)
            parts.Add(L.T("Letzte Auszahlung: {0} {1} ({2})", p.Amount, s.Coin, p.TimeUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        return string.Join(" · ", parts);
    }

    private void AddRow(ExtraPoolAccount a, bool keySet, string status)
    {
        var row = new Row
        {
            Account = a,
            Provider = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Value", ItemsSource = Providers.Select(p => new { p.Value, p.Label }).ToList(), SelectedValue = a.Provider },
            Coin = new ComboBox { ItemsSource = new[] { "BTC", "BCH" }, SelectedItem = a.Coin == "BCH" ? "BCH" : "BTC" },
            Name = new TextBox { Text = a.Name, MaxLength = 40 },
            Address = new TextBox { Text = a.Address },
            BaseUrl = new TextBox { Text = a.BaseUrl, ToolTip = L.T("leer = Standard des Pools") },
            Key = new PasswordBox { ToolTip = keySet ? L.T("gespeichert – leer lassen = unverändert") : L.T("Zugangs-Token / API-Schlüssel (nur lesen)") },
            Enabled = new CheckBox { Content = L.T("eingeschaltet"), IsChecked = a.Enabled, Margin = new Thickness(0, 0, 12, 0) },
            Tax = new CheckBox { Content = L.T("Erträge in die Steuer übernehmen"), IsChecked = a.TaxImport, Margin = new Thickness(0, 0, 12, 0) },
            Notify = new CheckBox { Content = L.T("Push bei Auszahlung"), IsChecked = a.NotifyPayouts },
            Status = new TextBlock { Text = status, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) },
        };
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        for (var c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < 2; r++) grid.RowDefinitions.Add(new RowDefinition());
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
        Cell(0, 0, L.T("Pool"), row.Provider);
        Cell(0, 1, L.T("Name"), row.Name);
        Cell(0, 2, L.T("Wallet-Adresse"), row.Address);
        Cell(1, 0, L.T("Zugang"), row.Key);
        Cell(1, 1, L.T("Coin"), row.Coin);
        Cell(1, 2, L.T("Adresse der Pool-API (optional, z. B. eigene public-pool-Instanz)"), row.BaseUrl);
        void Update()
        {
            var p = row.Provider.SelectedValue as string ?? "public-pool";
            ((FrameworkElement)row.Address.Parent).Visibility = p is "public-pool" or "ckpool" or "ocean" ? Visibility.Visible : Visibility.Collapsed;
            ((FrameworkElement)row.Key.Parent).Visibility = p is "braiins" or "viabtc" ? Visibility.Visible : Visibility.Collapsed;
            ((FrameworkElement)row.Coin.Parent).Visibility = p == "viabtc" ? Visibility.Visible : Visibility.Collapsed;
            row.Tax.Visibility = p is "ocean" or "braiins" or "viabtc" ? Visibility.Visible : Visibility.Collapsed;
        }
        row.Provider.SelectionChanged += (_, _) => Update();
        Update();

        var checks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        checks.Children.Add(row.Enabled);
        checks.Children.Add(row.Tax);
        checks.Children.Add(row.Notify);
        var test = new Button { Content = L.T("Testen"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 4, 8, 0) };
        var remove = new Button { Content = L.T("Entfernen"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 4, 0, 0) };
        var box = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        test.Click += async (_, _) =>
        {
            try
            {
                var s = await _host.Hub.FetchExtraPoolAsync(Read(row), row.Key.Password);
                row.Status.Text = StatusText(s, null);
            }
            catch (Exception ex) when (ex is LocalizedException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                row.Status.Text = ex is LocalizedException le ? le.In(Loc.Current) : ex.Message;
            }
        };
        remove.Click += (_, _) => { _rows.Remove(row); _list.Children.Remove(box); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(test);
        buttons.Children.Add(remove);
        box.Children.Add(grid);
        box.Children.Add(checks);
        box.Children.Add(row.Status);
        box.Children.Add(buttons);
        box.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 0) });
        _rows.Add(row);
        _list.Children.Add(box);
    }

    private static ExtraPoolAccount Read(Row r)
    {
        var a = r.Account.Clone();
        a.Provider = r.Provider.SelectedValue as string ?? "public-pool";
        a.Name = r.Name.Text;
        a.Address = r.Address.Text;
        a.BaseUrl = r.BaseUrl.Text;
        a.Coin = r.Coin.SelectedItem as string ?? "BTC";
        a.Enabled = r.Enabled.IsChecked == true;
        a.TaxImport = r.Tax.IsChecked == true;
        a.NotifyPayouts = r.Notify.IsChecked == true;
        return a;
    }

    /// <summary>Fehlertext oder null (vor dem Speichern).</summary>
    public string? Validate()
    {
        foreach (var a in _rows.Select(Read))
            if (a.NeedsAddress && !ExtraPoolClient.IsAddress(a.Address.Trim()))
                return L.T("{0}: bitte eine gültige Wallet-Adresse eintragen.", a.DisplayName);
        return null;
    }

    /// <summary>Konten und neu eingegebene Schlüssel übernehmen (speichert die Konfiguration mit).</summary>
    public void Apply()
    {
        var accounts = _rows.Select(Read).ToList();
        var secrets = _rows.Where(r => r.Key.Password.Length > 0).ToDictionary(r => r.Account.Id, r => r.Key.Password);
        _host.Hub.SaveExtraPools(accounts, secrets, L.T("Desktop"));
    }
}
