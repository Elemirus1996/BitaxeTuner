using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Reports;

namespace BitaxeTuner.App.Tax.Views;

/// <summary>Stromkosten je Monat neben den Zuflüssen (aus den Monatsberichten), wie im Browser unter „Steuer“.</summary>
public sealed class TaxEnergyWindow : Window
{
    public sealed record Row(string Month, string Kwh, string Cost, string Income, string Difference);

    private readonly Func<int, List<PeriodReport>> _months;
    private readonly ComboBox _year = new() { Width = 90, Margin = new Thickness(8, 0, 0, 0) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new Thickness(0, 10, 0, 0), MaxHeight = 460 };
    private readonly TextBlock _empty = new() { Margin = new Thickness(0, 10, 0, 0) };

    public TaxEnergyWindow(Func<int, List<PeriodReport>> months, IEnumerable<int> years)
    {
        _months = months;
        Title = L.T("Stromkosten je Monat");
        Width = 680;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        foreach (var (header, path) in new[] { (L.T("Monat"), nameof(Row.Month)), ("kWh", nameof(Row.Kwh)), (L.T("Stromkosten"), nameof(Row.Cost)),
                     (L.T("Zuflüsse (EUR)"), nameof(Row.Income)), (L.T("Differenz"), nameof(Row.Difference)) })
            _grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        foreach (var y in years.OrderDescending()) _year.Items.Add(y);
        _year.SelectionChanged += (_, _) => Load();

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = L.T("Jahr"), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(_year);

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
            Text = L.T("Stromkosten aus den Messwerten (mit Smart Plugs und Stundenpreisen, wenn eingerichtet). Ob und wie Stromkosten steuerlich berücksichtigt werden, klärt deine Steuerberatung – keine Steuerberatung."),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var close = new Button { Content = L.T("Schließen"), Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(head);
        root.Children.Add(_grid);
        root.Children.Add(_empty);
        root.Children.Add(hint);
        root.Children.Add(close);
        Content = root;
        if (_year.Items.Count > 0) _year.SelectedIndex = 0;
    }

    private void Load()
    {
        if (_year.SelectedItem is not int year) return;
        List<PeriodReport> months;
        try { months = _months(year); }
        catch (InvalidOperationException ex) { _empty.Text = ex.Message; return; }
        var c = L.Culture;
        var rows = months.Where(m => m.Miners.Any(x => x.TotalMinutes > 0) || m.IncomeEur > 0).ToList();
        var list = rows.Select(m =>
        {
            var has = m.Miners.Any(x => x.TotalMinutes > 0);
            return new Row(m.From.ToString("MMMM", c) + (m.Partial ? L.T(" (läuft)") : ""),
                has ? m.Energy.Kwh.ToString("0.0", c) : "–", has ? m.Energy.Cost.ToString("0.00", c) : "–",
                m.IncomeEur.ToString("0.00", c), ((double)m.IncomeEur - m.Energy.Cost).ToString("0.00", c));
        }).ToList();
        if (rows.Count > 0)
        {
            var kwh = rows.Sum(m => m.Energy.Kwh);
            var cost = rows.Sum(m => m.Energy.Cost);
            var inc = rows.Sum(m => m.IncomeEur);
            list.Add(new Row(L.T("Summe"), kwh.ToString("0.0", c), cost.ToString("0.00", c), inc.ToString("0.00", c), ((double)inc - cost).ToString("0.00", c)));
        }
        _grid.ItemsSource = list;
        _empty.Text = rows.Count == 0 ? L.T("Für dieses Jahr liegen keine Messwerte oder Zuflüsse vor.") : "";
    }
}
