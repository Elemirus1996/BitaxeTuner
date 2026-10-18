using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BitaxeTuner.Core.Help;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.24 Schnellsuche (Strg+K) der Desktop-App: Ansichten, Miner und Einstellungsabschnitte per Tippen finden.
/// Pfeiltasten wählen, Enter öffnet, Esc schließt. Löst selbst nichts am Miner aus.
/// </summary>
public sealed class QuickSearchWindow : Window
{
    private readonly List<QuickSearchItem> _all;
    private readonly string _settingKind;
    private readonly TextBox _input = new() { FontSize = 15, Padding = new Thickness(8, 6, 8, 6) };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), MaxHeight = 420, Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>Gewählter Eintrag nach dem Schließen (null = abgebrochen).</summary>
    public QuickSearchItem? Chosen { get; private set; }

    public QuickSearchWindow(List<QuickSearchItem> items, string settingKind)
    {
        (_all, _settingKind) = (items, settingKind);
        Title = L.T("Schnellsuche");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "CardBrush");
        _list.SetResourceReference(BackgroundProperty, "CardBrush");
        System.Windows.Automation.AutomationProperties.SetName(_input, L.T("Miner, Seite, Einstellung oder Aktion …"));
        System.Windows.Automation.AutomationProperties.SetName(_list, L.T("Treffer"));
        _list.ItemTemplate = BuildTemplate();
        _list.MouseDoubleClick += (_, _) => Pick();
        var hint = new TextBlock { Text = L.T("↑ ↓ wählen · Enter öffnen · Esc schließen"), Margin = new Thickness(2, 6, 0, 0) };
        if (TryFindResource("Muted") is Style muted) hint.Style = muted;
        Content = new StackPanel { Margin = new Thickness(10), Children = { _input, _list, hint } };
        _input.TextChanged += (_, _) => Update();
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => { Update(); _input.Focus(); };
    }

    private static DataTemplate BuildTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(DockPanel));
        var hint = new FrameworkElementFactory(typeof(TextBlock));
        hint.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(QuickSearchItem.KindAndHint)));
        hint.SetValue(DockPanel.DockProperty, Dock.Right);
        hint.SetValue(MarginProperty, new Thickness(12, 0, 0, 0));
        hint.SetValue(OpacityProperty, 0.65);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(QuickSearchItem.Label)));
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        panel.AppendChild(hint);
        panel.AppendChild(label);
        return new DataTemplate { VisualTree = panel };
    }

    private void Update()
    {
        _list.ItemsSource = QuickSearch.Find(_all, _input.Text, _settingKind);
        _list.SelectedIndex = _list.Items.Count > 0 ? 0 : -1;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var n = _list.Items.Count;
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Enter: Pick(); e.Handled = true; break;
            case Key.Down when n > 0: _list.SelectedIndex = (_list.SelectedIndex + 1) % n; _list.ScrollIntoView(_list.SelectedItem); e.Handled = true; break;
            case Key.Up when n > 0: _list.SelectedIndex = (_list.SelectedIndex - 1 + n) % n; _list.ScrollIntoView(_list.SelectedItem); e.Handled = true; break;
        }
    }

    private void Pick()
    {
        if (_list.SelectedItem is not QuickSearchItem item) return;
        Chosen = item;
        Close();
    }
}
