using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// Einführung „Erste Schritte“ (wie die Karte im Browser): Checkliste, die sich selbst abhakt; jeder Schritt öffnet
/// die Einstellungen am passenden Abschnitt bzw. die Betriebsart.
/// </summary>
public sealed class OnboardingWindow : Window
{
    private readonly AppConfig _config;
    private readonly Action<string, Window> _openSection;
    private readonly Action<Window> _openMode;
    private readonly StackPanel _list = new();
    private readonly TextBlock _progress = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

    public OnboardingWindow(AppConfig config, Action<string, Window> openSection, Action<Window> openMode)
    {
        _config = config;
        _openSection = openSection;
        _openMode = openMode;
        Title = L.T("Erste Schritte");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var title = new TextBlock { Text = L.T("Willkommen bei BitaxeTuner!"), FontSize = 18, FontWeight = FontWeights.SemiBold };
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
            Text = L.T("Diese Schritte richten das Wichtigste ein – jeder hakt sich selbst ab. Später wieder aufrufen: Einstellungen → Programm und Tuning.")
                + "\n" + L.T("Fragen? Unter „Hilfe“ stehen eine Kurzanleitung, Erklärungen zu jedem Protokolleintrag und zu den Miner-Logs."),
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _progress.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var later = new Button { Content = L.T("Später"), Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        var done = new Button { Content = L.T("Fertig – nicht mehr anzeigen"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
        done.SetResourceReference(StyleProperty, "PrimaryButton");
        done.Click += (_, _) =>
        {
            Onboarding.SetVisible(_config, false);
            _config.Save();
            Close();
        };
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(later);
        right.Children.Add(done);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(_progress);

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(title);
        root.Children.Add(intro);
        root.Children.Add(_list);
        root.Children.Add(buttons);
        Content = root;
        Draw();
    }

    private void Draw()
    {
        _list.Children.Clear();
        var steps = Onboarding.Steps(_config, server: false);
        _progress.Text = L.T("{0} von {1} erledigt", steps.Count(s => s.Done), steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var mark = new TextBlock
            {
                Text = step.Done ? "✔" : (i + 1).ToString(), FontSize = 16, Width = 26, VerticalAlignment = VerticalAlignment.Top,
            };
            mark.SetResourceReference(TextBlock.ForegroundProperty, step.Done ? "OkBrush" : "MutedTextBrush");
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = step.Title, FontWeight = FontWeights.SemiBold });
            var sub = new TextBlock { Text = step.Text, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            text.Children.Add(sub);
            var go = new Button
            {
                Content = step.Done ? L.T("Ansehen") : L.T("Einrichten"), Padding = new Thickness(12, 3, 12, 3),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            };
            if (!step.Done) go.SetResourceReference(StyleProperty, "PrimaryButton");
            go.Click += (_, _) =>
            {
                if (step.Action == "mode") _openMode(this);
                else if (step.Section is { } section) _openSection(section, this);
                Draw(); // Häkchen aktualisieren
            };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(mark, Dock.Left);
            DockPanel.SetDock(go, Dock.Right);
            row.Children.Add(mark);
            row.Children.Add(go);
            row.Children.Add(text);
            _list.Children.Add(row);
        }
    }
}
