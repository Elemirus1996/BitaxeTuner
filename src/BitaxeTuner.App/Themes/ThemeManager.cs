using System.Windows;
using System.Windows.Media;

namespace BitaxeTuner.App.Themes;

/// <summary>Schaltet zwischen dunklem (BitaxeMonitor) und hellem (BitaxeTuner) Design um – zur Laufzeit.</summary>
public static class ThemeManager
{
    public const string Dark = "dark";
    public const string Light = "light";

    public static string Current { get; private set; } = Dark;

    /// <summary>Für selbst gezeichnete Elemente (Diagramme), die neu rendern müssen.</summary>
    public static event Action? Changed;

    public static void Apply(string? theme)
    {
        Current = theme == Light ? Light : Dark;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/BitaxeTuner;component/Themes/{(Current == Light ? "Light" : "Dark")}.xaml")
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        // Alle bisherigen Design-Wörterbücher entfernen ("Themes/Dark.xaml" aus App.xaml oder eine frühere Umschaltung)
        foreach (var old in merged.Where(d => d.Source?.OriginalString.Contains("Themes/") == true).ToList())
            merged.Remove(old);
        merged.Add(dict);
        Changed?.Invoke();
    }

    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static Color Color(string key) =>
        Application.Current?.TryFindResource(key) is Color c ? c : Colors.Gray;
}
