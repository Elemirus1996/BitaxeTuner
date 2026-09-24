using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BitaxeTuner.App.Controls;

/// <summary>Schlankes Liniendiagramm für Live-Werte (ohne externe Bibliothek).</summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(LineChart),
        new FrameworkPropertyMetadata(Brushes.DarkOrange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (LineChart)d;
        if (e.OldValue is INotifyCollectionChanged oldCol) oldCol.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCol) newCol.CollectionChanged += chart.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40) return;

        var bg = (Brush?)TryFindResource("CardBorderBrush") ?? Brushes.LightGray;
        var textBrush = (Brush?)TryFindResource("MutedTextBrush") ?? Brushes.Gray;
        var gridPen = new Pen(bg, 1);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        const double left = 48, top = 22, bottom = 8, right = 8;
        var plot = new Rect(left, top, Math.Max(1, w - left - right), Math.Max(1, h - top - bottom));

        dc.DrawText(Text($"{Title}", textBrush, 12, dpi, FontWeights.SemiBold), new Point(0, 0));

        var values = Values?.Cast<object>().Select(Convert.ToDouble).ToList() ?? [];
        if (values.Count > 0)
        {
            var current = Text($"{values[^1]:F1} {Unit}", Stroke, 12, dpi, FontWeights.SemiBold);
            dc.DrawText(current, new Point(w - current.Width, 0));
        }
        if (values.Count < 2)
        {
            dc.DrawRectangle(null, gridPen, plot);
            return;
        }

        var min = values.Min();
        var max = values.Max();
        var pad = Math.Max((max - min) * 0.1, Math.Abs(max) * 0.01 + 0.5);
        min -= pad;
        max += pad;

        for (var i = 0; i <= 3; i++)
        {
            var y = plot.Top + plot.Height * i / 3;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var v = max - (max - min) * i / 3;
            var label = Text(v.ToString(v >= 100 ? "F0" : "F1", CultureInfo.CurrentCulture), textBrush, 10, dpi);
            dc.DrawText(label, new Point(left - label.Width - 6, y - label.Height / 2));
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var x = plot.Left + plot.Width * i / (values.Count - 1);
                var y = plot.Bottom - (values[i] - min) / (max - min) * plot.Height;
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round }, geometry);
    }

    private static FormattedText Text(string s, Brush brush, double size, double dpi, FontWeight? weight = null) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);
}
