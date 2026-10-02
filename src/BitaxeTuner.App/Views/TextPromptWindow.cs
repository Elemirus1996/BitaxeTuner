using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>Kleiner Eingabedialog (Text mit Hinweiszeile darunter, die sich beim Tippen anpassen kann).</summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _input = new() { Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(4, 3, 4, 3) };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };

    private TextPromptWindow(string title, string text, string value, Func<string, (string Text, bool Warn)>? note, int maxLength)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        _input.Text = value;
        _input.MaxLength = maxLength;
        void UpdateNote()
        {
            if (note is null) return;
            var (t, warn) = note(_input.Text);
            _note.Text = t;
            _note.SetResourceReference(TextBlock.ForegroundProperty, warn ? "WarnBrush" : "MutedTextBrush");
        }
        _input.TextChanged += (_, _) => UpdateNote();
        UpdateNote();

        var ok = new Button { Content = L.T("Speichern"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3) };
        ok.Click += (_, _) => { if (_input.Text.Trim().Length > 0) DialogResult = true; };
        var cancel = new Button { Content = L.T("Abbrechen"), IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_input);
        if (note is not null) root.Children.Add(_note);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    /// <summary>Ergebnis: eingegebener Text (getrimmt) oder null bei Abbruch.</summary>
    public static string? Ask(string title, string text, string value, Func<string, (string Text, bool Warn)>? note = null, int maxLength = 40)
    {
        var w = new TextPromptWindow(title, text, value, note, maxLength) { Owner = Application.Current?.MainWindow };
        return w.ShowDialog() == true ? w._input.Text.Trim() : null;
    }
}
