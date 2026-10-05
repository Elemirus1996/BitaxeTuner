using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using Microsoft.Win32;

namespace BitaxeTuner.App.Views;

/// <summary>0.9.11: gespeicherte Miner-Logs eines Miners (Standard 48 h) mit Filter und Export – wie im Browser.</summary>
public sealed class StoredLogWindow : Window
{
    private readonly MinerHub _hub;
    private readonly HubDevice _device;
    private readonly ComboBox _hours = new() { Width = 110, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _levels = new() { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _query = new() { Width = 200, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(4, 3, 4, 3) };
    private readonly TextBox _text = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBlock _info = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };

    public StoredLogWindow(MinerHub hub, HubDevice device)
    {
        _hub = hub;
        _device = device;
        Title = L.T("Gespeicherte Miner-Logs – {0}", device.Title);
        Width = 1000;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        _text.SetResourceReference(BackgroundProperty, "WindowBrush");
        _text.SetResourceReference(ForegroundProperty, "TextBrush");
        _info.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        foreach (var (h, label) in new[] { (1, L.T("1 h")), (6, L.T("6 h")), (24, L.T("24 h")), (48, L.T("48 h")), (168, L.T("7 Tage")) })
            _hours.Items.Add(new ComboBoxItem { Content = label, Tag = h });
        _hours.SelectedIndex = 3;
        foreach (var (v, label) in new[] { ("", L.T("alle Stufen")), ("EW", L.T("Fehler + Warnungen")), ("E", L.T("nur Fehler")) })
            _levels.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        _levels.SelectedIndex = 0;
        _hours.SelectionChanged += (_, _) => Load();
        _levels.SelectionChanged += (_, _) => Load();
        _query.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Load(); };

        var refresh = new Button { Content = L.T("Aktualisieren"), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += (_, _) => Load();
        var export = new Button { Content = L.T("Als Textdatei speichern …"), Padding = new Thickness(12, 3, 12, 3) };
        export.Click += (_, _) => Export();

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(_hours);
        bar.Children.Add(_levels);
        bar.Children.Add(new TextBlock { Text = L.T("Suchen:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        bar.Children.Add(_query);
        bar.Children.Add(refresh);
        bar.Children.Add(export);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_info, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_info);
        root.Children.Add(_text);
        Content = root;
        Loaded += (_, _) => Load();
    }

    private int Hours => _hours.SelectedItem is ComboBoxItem { Tag: int h } ? h : 48;
    private string Levels => _levels.SelectedItem is ComboBoxItem { Tag: string v } ? v : "";

    private List<Core.Monitoring.HistoryStore.StoredLogLine> Query(int limit)
    {
        _hub.MinerLogs.Flush();
        var now = DateTime.Now;
        return _hub.History?.QueryMinerLog(_device.Host, now.AddHours(-Hours), now, Levels, _query.Text, limit) ?? [];
    }

    private static string Line(Core.Monitoring.HistoryStore.StoredLogLine l) =>
        $"{l.Time:yyyy-MM-dd HH:mm:ss} {l.Level} {(l.Tag.Length > 0 ? l.Tag + ": " : "")}{l.Message}";

    private void Load()
    {
        var lines = Query(5000);
        _text.Text = string.Join(Environment.NewLine, lines.Select(Line));
        _text.ScrollToEnd();
        var total = _hub.History?.CountMinerLog(_device.Host) ?? 0;
        _info.Text = _device.Config.LogArchive
            ? L.T("{0} Zeilen angezeigt · {1} gespeichert · Aufbewahrung {2} h", lines.Count, total, _hub.Config.MinerLogKeepHours)
            : L.T("Speichern ist für diesen Miner aus (Einstellungen → Geräte).") + (total > 0 ? " " + L.T("{0} ältere Zeilen vorhanden.", total) : "");
    }

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Filter = L.T("Textdatei (*.txt)|*.txt"),
            FileName = string.Concat(_device.Title.Select(c => char.IsLetterOrDigit(c) ? c : '-')) + $"-log-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllLines(dialog.FileName, Query(50000).Select(Line));
        _info.Text = L.T("Gespeichert: {0}", dialog.FileName);
    }
}
