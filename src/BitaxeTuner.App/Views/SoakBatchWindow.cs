using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Host;

namespace BitaxeTuner.App.Views;

/// <summary>Dauertest für mehrere Miner: Auswahl, eine Dauer, eine Bestätigung (wie in der Server-Oberfläche).</summary>
public sealed class SoakBatchWindow : Window
{
    private readonly AppHost _host;
    private readonly ComboBox _hours = new() { Width = 90, Margin = new Thickness(8, 0, 0, 0) };
    private readonly StackPanel _list = new();
    private readonly List<(CheckBox Box, MinerHub.SoakBatchEntry Entry)> _boxes = [];

    public SoakBatchWindow(AppHost host)
    {
        _host = host;
        Title = "Dauertest für mehrere Miner";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        foreach (var h in new[] { 6, 12, 24, 48, 72 }) _hours.Items.Add(new ComboBoxItem { Content = $"{h} h", Tag = h });
        _hours.SelectedIndex = 2;

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = "Beobachtet wird jeweils die aktuelle Einstellung. Am Miner wird nichts geändert; Zeitplan/Strompreis-Regeln " +
                   "pausieren so lange. Schlägt ein Test fehl, gibt es einen Vorschlag (nur nach Bestätigung).",
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var durationRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        durationRow.Children.Add(new TextBlock { Text = "Dauer", VerticalAlignment = VerticalAlignment.Center });
        durationRow.Children.Add(_hours);

        var start = new Button { Content = "Starten …", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        start.SetResourceReference(StyleProperty, "PrimaryButton");
        start.Click += (_, _) => Start();
        var stopAll = new Button { Content = "Alle abbrechen", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
        stopAll.SetResourceReference(StyleProperty, "DangerButton");
        stopAll.Click += (_, _) => StopAll();
        var close = new Button { Content = "Schließen", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(start);
        buttons.Children.Add(stopAll);
        buttons.Children.Add(close);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(hint);
        root.Children.Add(durationRow);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 });
        root.Children.Add(buttons);
        Content = root;
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        _boxes.Clear();
        foreach (var e in _host.Hub.SoakBatchPreview())
        {
            var text = e.Eligible
                ? $"{e.Device.Title}: {e.FrequencyMhz} MHz / {e.CoreVoltageMv} mV"
                : $"{e.Device.Title}: {e.Reason}" + (e.Running && e.Device.Config.Soak is { } s ? $" (bis {s.Until:dd.MM. HH:mm})" : "");
            var box = new CheckBox { Content = text, IsChecked = e.Eligible, IsEnabled = e.Eligible, Margin = new Thickness(0, 3, 0, 3) };
            _boxes.Add((box, e));
            _list.Children.Add(box);
        }
        if (_boxes.Count == 0) _list.Children.Add(new TextBlock { Text = "Noch keine Miner eingetragen." });
    }

    private void Start()
    {
        var chosen = _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Entry).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, "Kein Miner ausgewählt.", Title);
            return;
        }
        var hours = (int)((ComboBoxItem)_hours.SelectedItem).Tag;
        var list = string.Join("\n", chosen.Select(e => $"• {e.Device.Title}: {e.FrequencyMhz} MHz / {e.CoreVoltageMv} mV"));
        if (MessageBox.Show(this, $"Dauertest für {chosen.Count} Miner starten ({hours} h)?\n\n{list}\n\nAm Miner wird dabei nichts geändert.",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            var results = _host.Hub.StartSoakBatch(chosen.Select(e => e.Device.Host), hours);
            var skipped = results.Where(r => !r.Started).ToList();
            MessageBox.Show(this, $"Dauertest gestartet für {results.Count(r => r.Started)} Miner." +
                (skipped.Count > 0 ? "\n\nÜbersprungen:\n" + string.Join("\n", skipped.Select(r => $"• {r.Device.Title}: {r.Message}")) : ""), Title);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Fill();
    }

    private void StopAll()
    {
        var running = _boxes.Count(b => b.Entry.Running);
        if (running == 0)
        {
            MessageBox.Show(this, "Es läuft kein Dauertest.", Title);
            return;
        }
        if (MessageBox.Show(this, $"{running} laufende(n) Dauertest(s) abbrechen? Die Einstellungen der Miner bleiben, wie sie sind.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _host.Hub.StopAllSoaks();
        Fill();
    }
}
