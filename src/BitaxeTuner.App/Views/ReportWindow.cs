using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Reports;
using Microsoft.Win32;

namespace BitaxeTuner.App.Views;

/// <summary>Monats- und Jahresbericht wie im Browser: Zusammenfassung, druckbare Seite im Standardbrowser, CSV, Push.</summary>
public sealed class ReportWindow : Window
{
    private readonly AppHost _host;
    private readonly ComboBox _period = new() { MinWidth = 200, Margin = new Thickness(8, 0, 0, 0), DisplayMemberPath = "Label", SelectedValuePath = "Value" };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontFamily = new System.Windows.Media.FontFamily("Consolas, Segoe UI") };

    public ReportWindow(AppHost host)
    {
        _host = host;
        Title = L.T("Bericht");
        Width = 720;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var periods = _host.Hub.ReportPeriods(DateTime.Now).Select(p => new
        {
            Value = p,
            Label = p.Length == 4 ? L.T("Jahr {0}", p) : DateTime.ParseExact(p, "yyyy-MM", CultureInfo.InvariantCulture).ToString("MMMM yyyy", L.Culture),
        }).ToList();
        _period.ItemsSource = periods;
        _period.SelectionChanged += (_, _) => Load();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = L.T("Zeitraum"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_period);

        Button Btn(string text, Action click, bool primary = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            if (primary) b.SetResourceReference(StyleProperty, "PrimaryButton");
            b.Click += (_, _) => click();
            return b;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Btn(L.T("Ansehen / Drucken"), OpenHtml, primary: true));
        buttons.Children.Add(Btn(L.T("CSV speichern …"), SaveCsv));
        buttons.Children.Add(Btn(L.T("Per Push senden"), async () => await SendAsync()));
        var close = new Button { Content = L.T("Schließen"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(close);

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
            Text = L.T("Druckbare Seite: im Browser „Drucken“ → „Als PDF speichern“. Abgeschlossene Monate werden gespeichert und bleiben auch erhalten, wenn ältere Minutenwerte bereinigt werden."),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(row);
        root.Children.Add(new ScrollViewer { Content = _summary, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 480 });
        root.Children.Add(hint);
        root.Children.Add(buttons);
        Content = root;

        if (periods.Count == 0) _summary.Text = L.T("Noch keine Messwerte für einen Bericht.");
        else _period.SelectedIndex = Math.Min(1, periods.Count - 1); // Vormonat
    }

    private string? Period => _period.SelectedValue as string;

    private PeriodReport? Build()
    {
        if (Period is not { } p) return null;
        try { return _host.Hub.BuildReport(p, DateTime.Now); }
        catch (InvalidOperationException ex)
        {
            _summary.Text = ex.Message;
            return null;
        }
    }

    private void Load()
    {
        if (Build() is not { } r) return;
        var c = L.Culture;
        var lines = new List<string>
        {
            ReportRenderer.Title(r) + (r.Partial ? " " + L.T("(läuft noch)") : ""),
            L.T("Energie {0} kWh · Stromkosten {1} {2}", r.Energy.Kwh.ToString("0.00", c), r.Energy.Cost.ToString("0.00", c), r.Currency) +
                (r.Energy.AvgCt is { } ct ? L.T(" · Ø {0} ct/kWh", ct.ToString("0.0", c)) : ""),
        };
        if (r.Income.Count > 0) lines.Add(L.T("Zuflüsse: {0} € ({1})", r.IncomeEur.ToString("0.00", c), string.Join(", ", r.Income.Select(i => $"{i.Count}× {i.Coin}"))));
        if (r.DataFrom is { } df) lines.Add(L.T("Messwerte liegen erst ab {0} vor – der Zeitraum davor fehlt im Bericht.", df.ToString("g", c)));
        lines.Add("");
        foreach (var m in r.Miners)
            lines.Add(L.T("{0}: verfügbar {1} % · {2} J/TH", m.Name, ((m.Availability ?? 0) * 100).ToString("0.0", c), m.Jth?.ToString("0.0", c) ?? "–") +
                      $" · {m.Kwh.ToString("0.00", c)} kWh");
        foreach (var p in r.Plugs)
            lines.Add($"{p.Name}: {p.Kwh.ToString("0.00", c)} kWh");
        _summary.Text = string.Join("\n", lines);
    }

    private void OpenHtml()
    {
        if (Build() is not { } r) return;
        var file = Path.Combine(Path.GetTempPath(), $"BitaxeTuner-{r.Period}.html");
        File.WriteAllText(file, ReportRenderer.Html(r));
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    private void SaveCsv()
    {
        if (Build() is not { } r) return;
        var dlg = new SaveFileDialog { FileName = $"BitaxeTuner-{r.Period}.csv", Filter = "CSV (*.csv)|*.csv" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllBytes(dlg.FileName, ReportRenderer.Csv(r));
    }

    private async Task SendAsync()
    {
        if (Period is not { } p) return;
        var error = await _host.Hub.SendMonthlyReportAsync(p, DateTime.Now, markSent: false);
        MessageBox.Show(this, error ?? L.T("Bericht per Push gesendet."), Title, MessageBoxButton.OK,
            error is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
