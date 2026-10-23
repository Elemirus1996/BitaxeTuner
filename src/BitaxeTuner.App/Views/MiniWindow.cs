using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.25 Mini-Anzeige: kleines, immer oben liegendes Fenster mit Hashrate, Temperatur, Leistung und Kosten/Tag.
/// Verschiebbar (ziehen), durchscheinend, Position und Deckkraft bleiben gespeichert. Rechtsklick: Deckkraft, Schließen.
/// </summary>
public sealed class MiniWindow : Window
{
    private readonly AppConfig _config;
    private readonly System.Windows.Shapes.Ellipse _dot = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hash = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly TextBlock _detail = new() { FontSize = 12, Opacity = 0.85 };
    private readonly TextBlock _warn = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240, Visibility = Visibility.Collapsed };

    /// <summary>Vom Nutzer geschlossen (Menü) – die App schaltet die Einstellung dann aus.</summary>
    public event Action? ClosedByUser;

    public MiniWindow(AppConfig config)
    {
        _config = config;
        Title = L.T("Mini-Anzeige");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        Opacity = Math.Clamp(config.Mini.Opacity, 0.4, 1.0);

        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 14, 9), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        foreach (var tb in new[] { _hash, _detail }) tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _warn.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dot, _hash } };
        card.Child = new StackPanel { Children = { head, _detail, _warn } };
        Content = card;
        System.Windows.Automation.AutomationProperties.SetName(card, L.T("Mini-Anzeige"));

        if (config.Mini.Left is { } left && config.Mini.Top is { } top && IsOnScreen(left, top)) { Left = left; Top = top; }
        else { WindowStartupLocation = WindowStartupLocation.Manual; Left = SystemParameters.WorkArea.Right - 280; Top = SystemParameters.WorkArea.Bottom - 110; }

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        LocationChanged += (_, _) => { _config.Mini.Left = Left; _config.Mini.Top = Top; };
        Deactivated += (_, _) => SaveQuietly();

        var menu = new ContextMenu();
        foreach (var pct in new[] { 100, 85, 70, 55 })
        {
            var item = new MenuItem { Header = L.T("Deckkraft {0} %", pct), IsCheckable = true, IsChecked = Math.Abs(Opacity * 100 - pct) < 1 };
            item.Click += (_, _) =>
            {
                Opacity = pct / 100.0;
                _config.Mini.Opacity = Opacity;
                foreach (MenuItem m in menu.Items.OfType<MenuItem>().Where(m => m.IsCheckable)) m.IsChecked = m == item;
                SaveQuietly();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var close = new MenuItem { Header = L.T("Mini-Anzeige schließen") };
        close.Click += (_, _) => { ClosedByUser?.Invoke(); CloseByCode(); };
        menu.Items.Add(close);
        ContextMenu = menu;
        Update(new MiniStatus(0, 0, 0, 0, null, null, "€", []));
    }

    public void Update(MiniStatus s)
    {
        _dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, s.Level switch
        {
            "ok" => "OkBrush", "warn" => "WarnBrush", "error" => "DangerBrush", _ => "IdleBrush",
        });
        _hash.Text = s.Total == 0 ? L.T("keine Miner") : s.HashText;
        _detail.Text = L.T("{0}/{1} online", s.Online, s.Total) + " · " + s.DetailText;
        _warn.Text = s.Warnings.Count == 0 ? "" : s.Warnings.Count == 1 ? "▲ " + s.Warnings[0] : "▲ " + L.T("{0} Warnungen", s.Warnings.Count);
        _warn.Visibility = s.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = s.Tooltip();
    }

    public void CloseByCode()
    {
        SaveQuietly();
        Close();
    }

    private void SaveQuietly()
    {
        try { _config.Save(); } catch { /* Position ist nicht wichtig */ }
    }

    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 50 &&
        left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 &&
        top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
}
