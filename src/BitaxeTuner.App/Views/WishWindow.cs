using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Network;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.16 „Wunsch einreichen“ ohne GitHub-Konto: Text eingeben, über den Wunsch-Briefkasten (<see cref="WishBox"/>)
/// senden; wer ein Konto hat, kann stattdessen das GitHub-Formular öffnen.
/// </summary>
public sealed class WishWindow : Window
{
    private readonly TextBox _idea = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, MaxLength = WishBox.MaxIdea, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _why = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70, MaxLength = WishBox.MaxWhy, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _send = new() { Content = L.T("Absenden"), Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
    private readonly string _version;
    private readonly string _githubUrl;

    public WishWindow(string version, string githubUrl)
    {
        _version = version;
        _githubUrl = githubUrl;
        Title = L.T("Wunsch einreichen");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(Label(L.T("Was wünschst du dir?")));
        body.Children.Add(_idea);
        body.Children.Add(Label(L.T("Wofür brauchst du es? (optional)")));
        body.Children.Add(_why);
        var note = new TextBlock
        {
            Text = L.T("Geht ohne Anmeldung an den Entwickler (Wunsch-Briefkasten über ntfy.sh, etwa 12 Stunden zwischengespeichert, nicht verschlüsselt) und landet als Idee auf dem Projekt-Board. Mitgeschickt werden nur dein Text, die App-Version und die Sprache – bitte keine persönlichen Daten, Adressen oder Passwörter eintragen."),
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 10, 0, 10),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        body.Children.Add(note);
        var row = new DockPanel();
        var github = new Button { Content = L.T("Lieber auf GitHub (mit Konto) …"), Padding = new Thickness(10, 4, 10, 4) };
        github.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(_githubUrl) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { /* kein Browser */ }
            Close();
        };
        DockPanel.SetDock(_send, Dock.Right);
        row.Children.Add(_send);
        row.Children.Add(new ContentControl { Content = github, HorizontalAlignment = HorizontalAlignment.Left });
        body.Children.Add(row);
        Content = body;
        _send.Click += async (_, _) => await SendAsync();
        Loaded += (_, _) => _idea.Focus();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) };

    private async Task SendAsync()
    {
        _send.IsEnabled = false;
        try
        {
            await WishBox.SendAsync(_idea.Text, _why.Text, _version, Loc.Current.Language);
            MessageBox.Show(this, L.T("Danke! Dein Wunsch ist unterwegs und landet als Idee auf dem Projekt-Board."), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex) when (ex is LocalizedException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            _send.IsEnabled = true;
        }
    }
}
