using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.25 Firmware-Update wie im Browser (Einstellungen → Miner → „Firmware aktualisieren“): Miner auswählen, einer nach dem
/// anderen aktualisieren, Fortschritt je Miner, nach dem Update abweichende Einstellungen nur nach Bestätigung zurückspielen.
/// </summary>
public sealed class FirmwareWindow : Window
{
    private readonly AppHost _host;
    private readonly StackPanel _candidates = new();
    private readonly StackPanel _job = new();
    private readonly Dictionary<string, CheckBox> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _start, _cancel;

    private MinerHub Hub => _host.Hub;

    private static Dictionary<string, string> StatusText => new()
    {
        ["waiting"] = L.T("wartet"), ["backup"] = L.T("sichert"), ["download"] = L.T("lädt"), ["www"] = L.T("Weboberfläche"),
        ["firmware"] = L.T("spielt auf"), ["restart"] = L.T("Neustart"), ["verify"] = L.T("prüft"), ["done"] = L.T("fertig"),
        ["changed"] = L.T("fertig – Einstellungen geändert"), ["failed"] = L.T("Fehler"), ["skipped"] = L.T("übersprungen"), ["cancelled"] = L.T("abgebrochen"),
    };

    public FirmwareWindow(AppHost host)
    {
        _host = host;
        Title = L.T("Firmware aktualisieren");
        Width = 820;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(Text(L.T("Nur offizielle Releases von ESP-Miner (AxeOS) und NerdQAxe, direkt von GitHub; Größe und Prüfsumme werden vor dem Aufspielen geprüft. Vorabversionen nur bei Minern mit „Beta-Versionen erlauben“ (Geräte → Details). Miner mit „nur überwachen“ werden nie aktualisiert."), true));
        root.Children.Add(_candidates);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        _start = Btn(L.T("Ausgewählte aktualisieren"), StartAsync, primary: true);
        _cancel = Btn(L.T("Nach dem laufenden Miner anhalten"), () => { Hub.CancelFirmwareUpdate(L.T("Desktop")); return Task.CompletedTask; });
        buttons.Children.Add(_start);
        buttons.Children.Add(_cancel);
        root.Children.Add(buttons);
        root.Children.Add(_job);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Hub.FirmwareJobChanged += OnChanged;
        Closed += (_, _) => Hub.FirmwareJobChanged -= OnChanged;
        Draw();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Draw);

    private void Draw()
    {
        var job = Hub.FirmwareJobState;
        var running = job?.Running == true;
        var keep = _boxes.Where(b => b.Value.IsChecked == false).Select(b => b.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _boxes.Clear();
        _candidates.Children.Clear();
        var grid = new Grid();
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, new GridLength(2, GridUnitType.Star) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        void Cell(int row, int col, UIElement e) { Grid.SetRow(e, row); Grid.SetColumn(e, col); grid.Children.Add(e); }
        grid.RowDefinitions.Add(new RowDefinition());
        foreach (var (h, i) in new[] { L.T("Miner"), L.T("Installiert"), L.T("Neu"), "" }.Select((h, i) => (h, i + 1)))
            Cell(0, i, Header(h));
        var row = 1;
        foreach (var c in Hub.FirmwareCandidates())
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var box = new CheckBox { IsChecked = c.CanUpdate && !keep.Contains(c.Host), IsEnabled = c.CanUpdate && !running, Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(box, c.Name);
            if (c.CanUpdate) _boxes[c.Host] = box;
            Cell(row, 0, box);
            Cell(row, 1, Text(c.Name));
            Cell(row, 2, Text(c.Installed ?? "–"));
            Cell(row, 3, Text((c.Target ?? "–") + (c.Prerelease ? " (Beta)" : "")));
            Cell(row, 4, Text(c.CanUpdate ? L.T("Update verfügbar") : c.Reason ?? "", !c.CanUpdate));
            row++;
        }
        _candidates.Children.Add(grid);
        _start.IsEnabled = !running && _boxes.Count > 0;
        _cancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = job is { CancelRequested: false };
        if (job?.CancelRequested == true) _cancel.Content = L.T("Hält nach dem laufenden Miner an …");

        _job.Children.Clear();
        if (job is null) return;
        _job.Children.Add(new TextBlock
        {
            Text = running ? L.T("Läuft seit {0} ({1})", L.Short(job.Started), job.Who) : L.T("Zuletzt: {0} ({1})", L.Short(job.Finished ?? job.Started), job.Who),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4),
        });
        foreach (var item in job.Items)
        {
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            if (item.Status == "changed" && item.Changes.Count > 0)
            {
                var restore = Btn(L.T("Zurückspielen …"), () => RestoreAsync(item));
                DockPanel.SetDock(restore, Dock.Right);
                line.Children.Add(restore);
            }
            var status = StatusText.TryGetValue(item.Status, out var st) ? st : item.Status;
            line.Children.Add(Text($"{item.Name}: {item.From ?? "?"} → {item.To ?? "?"} · {status}" + (item.Message is { Length: > 0 } m ? " – " + m : "")));
            _job.Children.Add(line);
        }
    }

    private async Task StartAsync()
    {
        var hosts = _boxes.Where(b => b.Value.IsChecked == true).Select(b => b.Key).ToList();
        if (hosts.Count == 0) return;
        var list = string.Join("\n", Hub.FirmwareCandidates().Where(c => hosts.Contains(c.Host, StringComparer.OrdinalIgnoreCase))
            .Select(c => $"• {c.Name}: {c.Installed} → {c.Target}{(c.Prerelease ? " (Beta)" : "")}"));
        if (MessageBox.Show(this, L.T("Firmware auf diesen Minern aktualisieren – einer nach dem anderen?\n\n{0}\n\nVorher werden die Einstellungen gesichert. Jeder Miner startet neu und liefert ein paar Minuten keine Hashrate. Bei einem Fehler hält der Ablauf an. Strom und WLAN währenddessen nicht unterbrechen.", list),
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            Hub.StartFirmwareUpdate(hosts, L.T("Desktop"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Draw();
        await Task.CompletedTask;
    }

    private async Task RestoreAsync(FirmwareJobItem item)
    {
        var changes = string.Join("\n", item.Changes.Select(c => $"• {c.Label}: {c.Current} → {c.Saved}"));
        if (MessageBox.Show(this, L.T("Diese Werte von vor dem Update wieder setzen? Frequenz und Spannung werden gegen die Grenzwerte des Modells geprüft; bei Pool-Änderungen startet der Miner neu.") + "\n\n" + changes,
                L.T("Einstellungen zurückspielen"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            await Hub.RestoreAfterFirmwareAsync(item.Host, L.T("Desktop"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Draw();
    }

    private static TextBlock Header(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 0, 10, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return t;
    }

    private static TextBlock Text(string text, bool muted = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center };
        if (muted) t.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return t;
    }

    private static Button Btn(string text, Func<Task> click, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        if (primary) b.SetResourceReference(StyleProperty, "PrimaryButton");
        b.Click += async (_, _) => await click();
        return b;
    }
}
