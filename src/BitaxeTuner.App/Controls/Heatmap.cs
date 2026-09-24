using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BitaxeTuner.Core.Benchmark;

namespace BitaxeTuner.App.Controls;

/// <summary>Zeigt alle getesteten Kombinationen als Raster Frequenz (x) × Spannung (y).</summary>
public sealed class Heatmap : FrameworkElement
{
    public static readonly DependencyProperty ResultsProperty = DependencyProperty.Register(
        nameof(Results), typeof(IEnumerable), typeof(Heatmap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnResultsChanged));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(string), typeof(Heatmap),
        new FrameworkPropertyMetadata("hashrate", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(
        nameof(Highlight), typeof(StepResult), typeof(Heatmap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Results { get => (IEnumerable?)GetValue(ResultsProperty); set => SetValue(ResultsProperty, value); }
    public string Metric { get => (string)GetValue(MetricProperty); set => SetValue(MetricProperty, value); }
    public StepResult? Highlight { get => (StepResult?)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    private static readonly Color Low = Color.FromRgb(0xFE, 0xF3, 0xE2);
    private static readonly Color High = Color.FromRgb(0xE8, 0x7A, 0x00);
    private static readonly Brush Unstable = Frozen(Color.FromRgb(0xE5, 0xE7, 0xEB));
    private static readonly Brush Limit = Frozen(Color.FromRgb(0xF8, 0xB4, 0xB4));

    private static void OnResultsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (Heatmap)d;
        if (e.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= map.OnChanged;
        if (e.NewValue is INotifyCollectionChanged n) n.CollectionChanged += map.OnChanged;
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var textBrush = (Brush?)TryFindResource("TextBrush") ?? Brushes.Black;
        var muted = (Brush?)TryFindResource("MutedTextBrush") ?? Brushes.Gray;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var results = Results?.OfType<StepResult>().ToList() ?? [];
        if (results.Count == 0)
        {
            dc.DrawText(Text("Noch keine Messergebnisse.", muted, 13, dpi), new Point(8, 8));
            return;
        }

        var freqs = results.Select(r => r.FrequencyMhz).Distinct().OrderBy(f => f).ToList();
        var volts = results.Select(r => r.CoreVoltageMv).Distinct().OrderByDescending(v => v).ToList();
        // Pro Zelle zählt das zuletzt gemessene Ergebnis.
        var cells = results.GroupBy(r => (r.FrequencyMhz, r.CoreVoltageMv)).ToDictionary(g => g.Key, g => g.Last());

        var stableValues = cells.Values.Where(r => r.IsStable).Select(Value).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        double min = stableValues.Count > 0 ? stableValues.Min() : 0, max = stableValues.Count > 0 ? stableValues.Max() : 1;
        var lowerIsBetter = Metric is "efficiency" or "temp" or "power";

        const double left = 64, top = 8, bottom = 36;
        var cellW = Math.Max(24, (ActualWidth - left - 8) / freqs.Count);
        var cellH = Math.Max(18, Math.Min(44, (ActualHeight - top - bottom) / volts.Count));

        for (var yi = 0; yi < volts.Count; yi++)
        {
            var y = top + yi * cellH;
            var label = Text($"{volts[yi]} mV", muted, 11, dpi);
            dc.DrawText(label, new Point(left - label.Width - 6, y + (cellH - label.Height) / 2));

            for (var xi = 0; xi < freqs.Count; xi++)
            {
                var rect = new Rect(left + xi * cellW + 1, y + 1, cellW - 2, cellH - 2);
                if (!cells.TryGetValue((freqs[xi], volts[yi]), out var r)) continue;

                Brush fill;
                string text;
                if (r.IsStable && Value(r) is { } v)
                {
                    var t = max > min ? (v - min) / (max - min) : 1;
                    if (lowerIsBetter) t = 1 - t;
                    fill = Frozen(Lerp(Low, High, 0.15 + 0.85 * t));
                    text = v.ToString(v >= 100 ? "F0" : "F1", CultureInfo.CurrentCulture);
                }
                else
                {
                    fill = r.Outcome == StepOutcome.LimitExceeded ? Limit : Unstable;
                    text = r.Outcome == StepOutcome.LimitExceeded ? "Grenze" : "×";
                }

                dc.DrawRoundedRectangle(fill, null, rect, 3, 3);
                if (Highlight is { } h && h.FrequencyMhz == r.FrequencyMhz && h.CoreVoltageMv == r.CoreVoltageMv)
                    dc.DrawRoundedRectangle(null, new Pen(textBrush, 2), rect, 3, 3);

                var ft = Text(text, Brushes.Black, 11, dpi);
                if (ft.Width < rect.Width - 2)
                    dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
            }
        }

        var axisY = top + volts.Count * cellH + 4;
        var step = Math.Max(1, (int)Math.Ceiling(40 / cellW));
        for (var xi = 0; xi < freqs.Count; xi += step)
        {
            var ft = Text($"{freqs[xi]}", muted, 11, dpi);
            dc.DrawText(ft, new Point(left + xi * cellW + (cellW - ft.Width) / 2, axisY));
        }
        var unit = Text("MHz", muted, 11, dpi);
        dc.DrawText(unit, new Point(left - unit.Width - 6, axisY));
    }

    private double? Value(StepResult r) => Metric switch
    {
        "efficiency" => r.EfficiencyJth,
        "temp" => r.MaxChipTempC,
        "power" => r.AvgPowerW,
        _ => r.AvgHashRateGh,
    };

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static FormattedText Text(string s, Brush brush, double size, double dpi) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);
}
