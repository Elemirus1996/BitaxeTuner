using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using Microsoft.Win32;

namespace BitaxeTuner.App.Views;

/// <summary>
/// Dauerhaftes Protokoll wie im Browser (history.db, mindestens 30 Tage): Frequenz/Spannung, Benchmark, Dauertest,
/// Automatik, Lüfter, Verbindung, Einstellungen, Server – mit Zeitraum, Miner, Kategorie, Suche und CSV-Export.
/// </summary>
public sealed class JournalWindow : Window
{
    private sealed record Row(string Time, string Miner, string Category, string Message);

    private readonly AppHost _host;
    private readonly ComboBox _range = new() { Width = 110, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _miner = new() { Width = 220, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _search = new() { Width = 220, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly List<(string Key, CheckBox Box)> _cats = [];
    private readonly DataGrid _grid = new() { IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private List<Row> _rows = [];

    public JournalWindow(AppHost host)
    {
        _host = host;
        Title = L.T("Protokoll");
        Width = 1100;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        _range.ItemsSource = new[] { ("24h", L.T("24 h")), ("7d", L.T("7 Tage")), ("30d", L.T("30 Tage")) }.Select(x => new { Value = x.Item1, Label = x.Item2 }).ToList();
        _range.DisplayMemberPath = "Label";
        _range.SelectedValuePath = "Value";
        _range.SelectedIndex = 0;
        var miners = new List<object> { new { Value = (string?)null, Label = L.T("alle Miner und Server") }, new { Value = (string?)"", Label = L.T("nur Server") } };
        miners.AddRange(host.Hub.Devices.Select(d => new { Value = (string?)d.Host, Label = d.Title }));
        _miner.ItemsSource = miners;
        _miner.DisplayMemberPath = "Label";
        _miner.SelectedValuePath = "Value";
        _miner.SelectedIndex = 0;
        _search.ToolTip = L.T("Suchen …");
        _range.SelectionChanged += (_, _) => Load();
        _miner.SelectionChanged += (_, _) => Load();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Load(); };
        _search.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(_range);
        bar.Children.Add(_miner);
        bar.Children.Add(_search);
        var csv = new Button { Content = L.T("CSV speichern …"), Padding = new Thickness(10, 3, 10, 3) };
        csv.Click += (_, _) => SaveCsv();
        bar.Children.Add(csv);
        bar.Children.Add(_count);

        var cats = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var key in EventCategories.All)
        {
            var cb = new CheckBox { Content = EventCategories.Label(key), Margin = new Thickness(0, 2, 14, 2) };
            cb.Click += (_, _) => Load();
            _cats.Add((key, cb));
            cats.Children.Add(cb);
        }

        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Zeit"), Binding = new System.Windows.Data.Binding(nameof(Row.Time)), Width = 140 });
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Miner"), Binding = new System.Windows.Data.Binding(nameof(Row.Miner)), Width = 150 });
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Kategorie"), Binding = new System.Windows.Data.Binding(nameof(Row.Category)), Width = 150 });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = L.T("Meldung"), Binding = new System.Windows.Data.Binding(nameof(Row.Message)), Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } },
        });

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 6),
            Text = L.T("Keine Kategorie gewählt = alle. Dauerhaft gespeichert in history.db (mindestens 30 Tage) – läuft nach Neustarts und Updates weiter und ist in jeder Sicherung enthalten."),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(cats, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(cats);
        root.Children.Add(hint);
        root.Children.Add(_grid);
        Content = root;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        if (_host.Hub.History is not { } history)
        {
            _count.Text = L.T("Verlaufsdatenbank nicht verfügbar.");
            return;
        }
        var span = (_range.SelectedValue as string) switch { "7d" => TimeSpan.FromDays(7), "30d" => TimeSpan.FromDays(30), _ => TimeSpan.FromHours(24) };
        var now = DateTime.Now;
        var cats = _cats.Where(c => c.Box.IsChecked == true).Select(c => c.Key).ToList();
        var entries = history.QueryEvents(now - span, now, _miner.SelectedValue as string, cats, _search.Text);
        var names = _host.Hub.Devices.ToDictionary(d => d.Host, d => d.Title, StringComparer.OrdinalIgnoreCase);
        _rows = entries.Select(e => new Row(e.Time.ToString("g", L.Culture),
            e.Host is null ? L.T("Server") : names.GetValueOrDefault(e.Host, e.Host), EventCategories.Label(e.Category), e.Message)).ToList();
        _grid.ItemsSource = _rows;
        _count.Text = entries.Count >= 5000 ? L.T("neueste {0} Einträge", entries.Count) : L.T("{0} Einträge", entries.Count);
    }

    private void SaveCsv()
    {
        var dlg = new SaveFileDialog { FileName = $"BitaxeTuner-Protokoll-{DateTime.Now:yyyyMMdd-HHmm}.csv", Filter = "CSV (*.csv)|*.csv" };
        if (dlg.ShowDialog(this) != true) return;
        static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Zeit;Miner;Kategorie;Meldung\r\n");
        foreach (var r in _rows) sb.Append($"{Csv(r.Time)};{Csv(r.Miner)};{Csv(r.Category)};{Csv(r.Message)}\r\n");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }
}
