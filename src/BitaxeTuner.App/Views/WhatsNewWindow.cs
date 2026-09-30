using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>„Neu in dieser Version“ nach einem Update: kurze Liste der Neuerungen, je mit Sprung in die Einstellungen.</summary>
public sealed class WhatsNewWindow : Window
{
    public WhatsNewWindow(string version, IReadOnlyList<NewFeature> features, Action<string, Window> openSection)
    {
        Title = L.T("Neu in {0}", version);
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = L.T("Neu in {0}", version), FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var f in features)
        {
            var star = new TextBlock { Text = "★", FontSize = 15, Width = 24, VerticalAlignment = VerticalAlignment.Top };
            star.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = f.Title, FontWeight = FontWeights.SemiBold });
            var sub = new TextBlock { Text = f.Text, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            text.Children.Add(sub);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(star, Dock.Left);
            row.Children.Add(star);
            if (f.Section is { } section)
            {
                var go = new Button { Content = L.T("Ansehen"), Padding = new Thickness(12, 3, 12, 3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                go.Click += (_, _) => openSection(section, this);
                DockPanel.SetDock(go, Dock.Right);
                row.Children.Add(go);
            }
            row.Children.Add(text);
            root.Children.Add(row);
        }
        var ok = new Button { Content = L.T("Verstanden"), Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true, Margin = new Thickness(0, 6, 0, 0) };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        ok.Click += (_, _) => Close();
        root.Children.Add(ok);
        Content = root;
    }
}
