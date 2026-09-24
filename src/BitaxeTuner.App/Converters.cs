using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BitaxeTuner.Core.Benchmark;

namespace BitaxeTuner.App;

/// <summary>null/leer → Collapsed, sonst Visible. Mit Parameter "invert" umgekehrt.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null && (value is not string s || !string.IsNullOrWhiteSpace(s));
        if (parameter as string == "invert") hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = Brushes.Green;
    public Brush FalseBrush { get; set; } = Brushes.Gray;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class OutcomeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StepOutcome.Stable => new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),
        StepOutcome.Unstable => new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
        StepOutcome.LimitExceeded => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
        _ => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Formatiert optionale Zahlen: null → "–".</summary>
public sealed class NumberConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var format = parameter as string ?? "F1";
        return value switch
        {
            null => "–",
            double d when double.IsNaN(d) => "–",
            double d => d.ToString(format, culture),
            int i => i.ToString(culture),
            long l => l.ToString(culture),
            _ => value.ToString() ?? "–",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
