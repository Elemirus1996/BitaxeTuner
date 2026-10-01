using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Reports;

namespace BitaxeTuner.App.Views;

/// <summary>
/// Vergleichsbericht wie im Browser: 2–6 Miner, Zeitraum, Werte und Diagramme wählen → druckbare Seite im Standardbrowser.
/// Vorauswahl „Kühlung“ für die Frage, warum ein Miner heißer läuft als ein gleicher.
/// </summary>
public sealed class CompareReportWindow : Window
{
    private readonly AppHost _host;
    private readonly List<(HubDevice Device, CheckBox Box)> _miners = [];
    private readonly List<(string Key, CheckBox Box)> _values = [];
    private readonly List<(string Key, CheckBox Box)> _charts = [];
    private readonly ComboBox _range = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };

    public CompareReportWindow(AppHost host)
    {
        _host = host;
        Title = L.T("Vergleichsbericht");
        Width = 860;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 820;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        TextBlock Head(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) };
        CheckBox Box(string text, bool on) => new() { Content = text, IsChecked = on, Margin = new Thickness(0, 2, 0, 2) };

        var minerCol = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        minerCol.Children.Add(Head(L.T("Miner (bis 6)")));
        foreach (var d in host.Hub.Devices)
        {
            var b = Box(d.Title, _miners.Count < 2);
            _miners.Add((d, b));
            minerCol.Children.Add(b);
        }
        minerCol.Children.Add(Head(L.T("Zeitraum")));
        _range.ItemsSource = CompareReports.Ranges.Select(r => new { Value = r, Label = CompareReports.RangeLabel(r) }).ToList();
        _range.DisplayMemberPath = "Label";
        _range.SelectedValuePath = "Value";
        _range.SelectedIndex = 0;
        minerCol.Children.Add(_range);
        minerCol.Children.Add(Head(L.T("Diagramme")));
        foreach (var (key, label) in CompareReports.Charts())
        {
            var b = Box(label, CompareReports.DefaultCharts.Contains(key));
            _charts.Add((key, b));
            minerCol.Children.Add(b);
        }

        var columns = new WrapPanel();
        columns.Children.Add(minerCol);
        foreach (var (group, title) in new[] { ("now", L.T("Aktuell")), ("range", L.T("Über den Zeitraum")), ("bench", L.T("Letzter Benchmark")) })
        {
            var col = new StackPanel { Margin = new Thickness(0, 0, 16, 0), MinWidth = 180 };
            col.Children.Add(Head(title));
            foreach (var (key, label, g) in CompareReports.Values())
            {
                if (g != group) continue;
                var b = Box(label, CompareReports.DefaultValues.Contains(key));
                _values.Add((key, b));
                col.Children.Add(b);
            }
            columns.Children.Add(col);
        }

        Button Btn(string text, Action click, bool primary = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            if (primary) b.SetResourceReference(StyleProperty, "PrimaryButton");
            b.Click += (_, _) => click();
            return b;
        }
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        presets.Children.Add(new TextBlock { Text = L.T("Vorauswahl:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        presets.Children.Add(Btn(L.T("Standard"), () => Apply(CompareReports.DefaultValues, CompareReports.DefaultCharts)));
        presets.Children.Add(Btn(L.T("Kühlung (z. B. für eine Frage in der Community)"), () => Apply(CompareReports.CoolingValues, CompareReports.CoolingCharts)));

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
            Text = L.T("Öffnet eine druckbare Seite – als PDF über „Drucken“. Ohne IP- und Wallet-Adressen."),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Btn(L.T("Bericht öffnen"), Open, primary: true));
        buttons.Children.Add(new Button { Content = L.T("Schließen"), Padding = new Thickness(12, 4, 12, 4), IsCancel = true });

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(presets);
        root.Children.Add(new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 600 });
        root.Children.Add(hint);
        root.Children.Add(buttons);
        Content = root;
    }

    private void Apply(IEnumerable<string> values, IEnumerable<string> charts)
    {
        var v = values.ToHashSet();
        var c = charts.ToHashSet();
        foreach (var (key, box) in _values) box.IsChecked = v.Contains(key);
        foreach (var (key, box) in _charts) box.IsChecked = c.Contains(key);
    }

    private void Open()
    {
        var devices = _miners.Where(m => m.Box.IsChecked == true).Select(m => m.Device).ToList();
        if (devices.Count is < 1 or > CompareReports.MaxMiners)
        {
            MessageBox.Show(this, L.T("Bitte 1 bis {0} Miner auswählen.", CompareReports.MaxMiners), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var report = CompareReports.Build(_host.Hub, devices, _range.SelectedValue as string ?? "24h",
            _values.Where(x => x.Box.IsChecked == true).Select(x => x.Key), _charts.Where(x => x.Box.IsChecked == true).Select(x => x.Key), DateTime.Now);
        var file = Path.Combine(Path.GetTempPath(), $"BitaxeTuner-Vergleich-{DateTime.Now:yyyyMMdd-HHmm}.html");
        File.WriteAllText(file, CompareReports.Html(report));
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }
}
