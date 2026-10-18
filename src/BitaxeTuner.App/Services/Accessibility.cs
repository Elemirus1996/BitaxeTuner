using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BitaxeTuner.App.Services;

/// <summary>
/// 0.9.24 Barrierefreiheit: Knöpfe ohne Text (nur Symbol) und Felder ohne Namen bekommen ihren Tooltip als Namen für
/// Screenreader (UI Automation). Gilt für alle Fenster der App, ohne jedes XAML einzeln anzufassen.
/// </summary>
public static class Accessibility
{
    public static void Register()
    {
        foreach (var type in new[] { typeof(ButtonBase), typeof(TextBox), typeof(ComboBox), typeof(Slider), typeof(PasswordBox) })
            EventManager.RegisterClassHandler(type, FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || AutomationProperties.GetName(el).Length > 0) return;
        if (el is ContentControl { Content: string { Length: > 0 } }) return;   // Text am Knopf genügt
        if (el.ToolTip is string { Length: > 0 } tip) AutomationProperties.SetName(el, tip);
        else if (el.ToolTip is TextBlock { Text.Length: > 0 } tb) AutomationProperties.SetName(el, tb.Text);
    }
}
