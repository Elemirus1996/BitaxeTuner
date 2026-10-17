using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BitaxeTuner.Core.Help;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Controls;

/// <summary>
/// 0.9.23 Kleines „?“ neben einem Fachbegriff: Tooltip mit der Erklärung aus dem Glossar, Klick zeigt sie im Dialog
/// (gleicher Text wie im Browser und in der Hilfe unter „Fachbegriffe“).
/// </summary>
public sealed class TermHint : TextBlock
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.Register(nameof(Key), typeof(string), typeof(TermHint), new PropertyMetadata("", (d, _) => ((TermHint)d).Update()));

    /// <summary>Kennung des Begriffs im <see cref="Glossary"/> (z. B. „efficiency“).</summary>
    public string Key
    {
        get => (string)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public TermHint()
    {
        Text = "?";
        FontSize = 10;
        FontWeight = FontWeights.Bold;
        Margin = new Thickness(5, 0, 0, 0);
        Padding = new Thickness(4, 0, 4, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Help;
        SetResourceReference(ForegroundProperty, "MutedTextBrush");
        SetResourceReference(BackgroundProperty, "CardBrush");
        ToolTipService.SetInitialShowDelay(this, 200);
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (Glossary.Get(Key) is { } g)
                MessageBox.Show(Window.GetWindow(this), g.Text + "\n\n" + L.T("Alle Fachbegriffe: Hilfe → Fachbegriffe."), g.Term, MessageBoxButton.OK, MessageBoxImage.Information);
        };
    }

    private void Update()
    {
        var g = Glossary.Get(Key);
        ToolTip = g is null ? null : new TextBlock { Text = $"{g.Term}\n{g.Text}", TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
        AutomationProperties_SetName(g?.Term);
    }

    private void AutomationProperties_SetName(string? term) =>
        System.Windows.Automation.AutomationProperties.SetName(this, term is null ? "?" : L.T("Was bedeutet {0}?", term));
}
