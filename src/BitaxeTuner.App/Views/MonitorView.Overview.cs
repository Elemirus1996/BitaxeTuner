using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.14 Gesamtansicht wie im Browser: Gewinn-Kachel (Ertrag gegen Stromkosten) und eine Karte je Miner unter den Kennzahlen.
/// Ein Klick auf eine Karte wählt den Miner in der Seitenleiste.
/// </summary>
public partial class MonitorView
{
    /// <summary>Miner-Karte angeklickt: Hauptfenster wählt diesen Miner.</summary>
    public event Action<string>? MinerRequested;

    private void RenderOverviewExtras(bool aggregate)
    {
        OverviewExtras.Visibility = aggregate ? Visibility.Visible : Visibility.Collapsed;
        if (!aggregate) return;
        RenderProfitTile();
        RenderMinerCards();
    }

    private void RenderProfitTile()
    {
        var profit = _host.Hub.Profit(DateTime.Now);
        var day = profit?.Window("24h");
        if (day is not { Income: { } income, Net: { } net } || (income == 0 && profit!.Source == "tax"))
        {
            ProfitTile.Visibility = Visibility.Collapsed;
            return;
        }
        ProfitTile.Visibility = Visibility.Visible;
        var cur = profit!.Currency;
        ProfitValue.Text = $"{net.ToString("+0.00;-0.00", De)} {cur}";
        ProfitValue.Foreground = net >= 0 ? Green : Red;
        string Line(string key, string label) => profit.Window(key) is { Net: { } n } ? $"{label} {n.ToString("+0.00;-0.00", De)}" : "";
        var others = string.Join(" · ", new[] { Line("7d", L.T("7 Tage")), Line("30d", L.T("30 Tage")), Line("month", L.T("Dieser Monat")) }.Where(s => s.Length > 0));
        ProfitSub.Text = L.T("Ertrag {0} − Strom {1}", income.ToString("0.00", De), day.Cost.ToString("0.00", De)) +
                         (day.Partial ? " · " + L.T("ohne Coins ohne Kurs") : "") + (others.Length > 0 ? "\n" + others : "") + "\n" +
                         (profit.Source == "pool" ? L.T("Ertrag: Gutschriften beim Pool zum aktuellen Kurs (Schätzung).") : L.T("Ertrag: dokumentierte Zuflüsse aus dem Steuer-Bereich."));
    }

    private void RenderMinerCards()
    {
        MinerCards.Children.Clear();
        var width = ActualWidth > 0 ? ActualWidth : 1200;
        MinerCards.Columns = Math.Clamp((int)(width / 280), 1, Math.Max(1, _states.Count));
        foreach (var s in _states)
        {
            var host = s.Config.Host;
            var maintenance = _host.Maintenance.IsActive(host);
            var status = s.Online ? Green : maintenance ? Gold : Red;
            var stack = new StackPanel();

            var head = new DockPanel();
            var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = status, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(dot);
            head.Children.Add(new TextBlock { Text = s.Config.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Normal });
            stack.Children.Add(head);
            var model = s.Info?.AsicModel is { Length: > 0 } asic ? asic : "";
            stack.Children.Add(new TextBlock { Text = model.Length > 0 ? $"{model} · {host}" : host, Style = (Style)FindResource("TileSub") });

            if (s.Online && s.Info is { } i)
            {
                stack.Children.Add(new TextBlock { Text = FormatHash(i.hashRate), Style = (Style)FindResource("TileValue"), Foreground = Green, Margin = new Thickness(0, 4, 0, 0) });
                var th = i.hashRate / 1000.0;
                stack.Children.Add(new TextBlock
                {
                    Text = $"{i.temp.ToString("0.0", De)} °C · {i.power.ToString("0.0", De)} W" + (th > 0.01 ? $" · {(i.power / th).ToString("0.0", De)} J/TH" : ""),
                    Style = (Style)FindResource("TileSub"), Foreground = TempBrush(i.temp),
                });
                var pool = i.isUsingFallbackStratum != 0 ? $"{i.fallbackStratumURL} ({L.T("Ersatz-Pool")})" : i.stratumURL;
                if (!string.IsNullOrWhiteSpace(pool))
                    stack.Children.Add(new TextBlock { Text = pool, Style = (Style)FindResource("TileSub"), Foreground = i.isUsingFallbackStratum != 0 ? Gold : ThemeBrush("MutedTextBrush") });
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = maintenance ? L.T("Neustart/Tuning …") : s.Error ?? "offline",
                    Style = (Style)FindResource("TileValue"), FontSize = 14, Foreground = status, Margin = new Thickness(0, 4, 0, 0),
                });
            }

            var card = new Border
            {
                Style = (Style)FindResource("Tile"), Child = stack, Cursor = Cursors.Hand,
                BorderBrush = status, BorderThickness = new Thickness(3, 0, 0, 0),
                ToolTip = L.T("{0} öffnen", s.Config.Name),
            };
            card.MouseLeftButtonUp += (_, _) => MinerRequested?.Invoke(host);
            MinerCards.Children.Add(card);
        }
    }

    private static Brush ThemeBrush(string key) => Themes.ThemeManager.Brush(key);
}
