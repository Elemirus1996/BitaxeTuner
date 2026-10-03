using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.Core.Help;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>Hilfe (0.9.9): Anleitung, Protokoll und Miner-Logs erklärt, häufige Fragen – gleicher Inhalt wie im Browser.</summary>
public sealed class HelpWindow : Window
{
    private readonly StackPanel _list = new();
    private readonly TextBox _search = new() { Width = 280, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly IReadOnlyList<HelpSection> _sections = HelpContent.Sections(server: false);

    public HelpWindow()
    {
        Title = L.T("Hilfe");
        Width = 820;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var root = new DockPanel { Margin = new Thickness(18) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = L.T("Hilfe"), FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = L.T("Suchen:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 10) });
        row.Children.Add(_search);
        head.Children.Add(row);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        _search.TextChanged += (_, _) => Draw();
        Draw();
    }

    private void Draw()
    {
        var q = _search.Text.Trim();
        _list.Children.Clear();
        foreach (var sec in _sections)
        {
            var items = sec.Items.Where(i => q.Length == 0 || (i.Title + " " + i.Text).Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (q.Length > 0 && items.Count == 0) continue;
            _list.Children.Add(new TextBlock { Text = sec.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            if (sec.Intro.Length > 0)
            {
                var intro = new TextBlock { Text = sec.Intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
                intro.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                _list.Children.Add(intro);
            }
            foreach (var item in items)
            {
                var header = new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                var body = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 4, 0, 8) };
                header.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                body.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                _list.Children.Add(new Expander { Header = header, Content = body, IsExpanded = q.Length > 0, Margin = new Thickness(0, 2, 0, 2) });
            }
        }
    }
}
