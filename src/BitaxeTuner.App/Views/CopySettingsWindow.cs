using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Storage;

namespace BitaxeTuner.App.Views;

/// <summary>
/// Pool und Lüfter eines Miners auf andere übertragen (wie in der Server-Oberfläche): Vorschau alt → neu,
/// Bestätigung, vorher Sicherung je Ziel. Frequenz und Spannung werden nie übertragen.
/// </summary>
public sealed class CopySettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly ComboBox _source = new() { MinWidth = 220, Margin = new Thickness(8, 0, 0, 0), DisplayMemberPath = nameof(HubDevice.Title) };
    private readonly CheckBox _pool = new() { Content = L.T("Pool und Fallback-Pool"), IsChecked = true, Margin = new Thickness(0, 3, 16, 3) };
    private readonly CheckBox _fan = new() { Content = L.T("Lüfter und Zieltemperatur"), IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
    private readonly StackPanel _targets = new();
    private readonly List<(CheckBox Box, HubDevice Device)> _boxes = [];
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _copy = new() { Content = L.T("Übertragen …"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private List<CopyPreview> _last = [];

    public CopySettingsWindow(AppHost host)
    {
        _host = host;
        Title = L.T("Einstellungen übertragen");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = L.T("Pool und Lüfter eines Miners auf andere übernehmen. Beim Pool-Benutzer wird nur das Wallet übernommen, der Worker-Name jedes Miners bleibt. Frequenz und Spannung werden nie übertragen."),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var sourceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        sourceRow.Children.Add(new TextBlock { Text = L.T("Von"), VerticalAlignment = VerticalAlignment.Center });
        sourceRow.Children.Add(_source);
        _source.ItemsSource = _host.Hub.Devices;
        _source.SelectionChanged += (_, _) => FillTargets();

        var groups = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        groups.Children.Add(_pool);
        groups.Children.Add(_fan);
        _pool.Click += (_, _) => Invalidate();
        _fan.Click += (_, _) => Invalidate();

        var previewButton = new Button { Content = L.T("Vorschau"), Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        previewButton.SetResourceReference(StyleProperty, "PrimaryButton");
        previewButton.Click += async (_, _) => await PreviewAsync();
        _copy.Click += async (_, _) => await CopyAsync();
        var close = new Button { Content = L.T("Schließen"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(previewButton);
        buttons.Children.Add(_copy);
        buttons.Children.Add(close);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(hint);
        root.Children.Add(sourceRow);
        root.Children.Add(groups);
        root.Children.Add(new TextBlock { Text = L.T("Auf"), Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(new ScrollViewer { Content = _targets, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 200 });
        root.Children.Add(new ScrollViewer { Content = _preview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 300 });
        root.Children.Add(buttons);
        Content = root;

        if (_host.Hub.Devices.Count > 0) _source.SelectedIndex = 0;
        else _preview.Text = L.T("Noch keine Miner eingetragen.");
    }

    private HubDevice? Source => _source.SelectedItem as HubDevice;

    private IReadOnlySet<SettingGroup> Groups()
    {
        var set = new HashSet<SettingGroup>();
        if (_pool.IsChecked == true) set.Add(SettingGroup.Pool);
        if (_fan.IsChecked == true) set.Add(SettingGroup.Fan);
        return set;
    }

    private List<HubDevice> Targets() => _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Device).ToList();

    private void FillTargets()
    {
        _targets.Children.Clear();
        _boxes.Clear();
        foreach (var d in _host.Hub.Devices.Where(d => d != Source))
        {
            var box = new CheckBox { Content = d.Title, IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
            box.Click += (_, _) => Invalidate();
            _boxes.Add((box, d));
            _targets.Children.Add(box);
        }
        if (_boxes.Count == 0) _targets.Children.Add(new TextBlock { Text = L.T("Kein Miner ausgewählt.") });
        Invalidate();
    }

    /// <summary>Nach jeder Änderung der Auswahl muss die Vorschau neu erstellt werden, bevor übertragen wird.</summary>
    private void Invalidate()
    {
        _last = [];
        _copy.IsEnabled = false;
        _preview.Text = "";
    }

    private async Task PreviewAsync()
    {
        if (Source is not { } source) return;
        var targets = Targets();
        if (targets.Count == 0) { MessageBox.Show(this, L.T("Kein Miner ausgewählt."), Title); return; }
        try
        {
            IsEnabled = false;
            _last = await _host.Hub.CopySettingsPreviewAsync(source, targets, Groups());
            _preview.Text = Describe(_last);
            _copy.IsEnabled = _last.Any(p => p.Error is null && p.Changes.Count > 0);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; }
    }

    private static string Describe(IEnumerable<CopyPreview> previews) => string.Join("\n\n", previews.Select(p =>
        p.Device.Title + "\n" + (p.Error is { } err
            ? "  ⚠ " + err
            : p.Changes.Count == 0
                ? "  " + L.T("Keine Unterschiede.")
                : string.Join("\n", p.Changes.Select(c => $"  {c.Label}: {c.Current} → {c.Saved}")))));

    private async Task CopyAsync()
    {
        if (Source is not { } source) return;
        var ready = _last.Where(p => p.Error is null && p.Changes.Count > 0).ToList();
        if (ready.Count == 0) return;
        var count = ready.Sum(p => p.Changes.Count);
        if (MessageBox.Show(this, L.T("{0} Änderung(en) an {1} Miner(n) setzen?\n\nVorher wird jeder Miner gesichert. Bei Pool-Änderungen startet der Miner neu. Frequenz und Spannung bleiben unverändert.", count, ready.Count)
                + "\n\n" + Describe(ready), Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            IsEnabled = false;
            var results = await _host.Hub.CopySettingsAsync(source, ready.Select(p => p.Device).ToList(), Groups());
            var failed = results.Count(r => r.Error is not null);
            _preview.Text = Describe(results);
            _copy.IsEnabled = false;
            _last = [];
            MessageBox.Show(this, failed == 0 ? L.T("Einstellungen übertragen.") : L.T("Übertragen, {0} Miner mit Fehler – siehe Liste.", failed), Title,
                MessageBoxButton.OK, failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; }
    }
}
