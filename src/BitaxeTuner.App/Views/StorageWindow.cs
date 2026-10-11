using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.15 Speicherplatz wie im Browser (Einstellungen → Sicherung): freier Platz, was BitaxeTuner belegt, und Sicherungen
/// bzw. vorherige Stände („backup-…“) einzeln löschen – jeweils nach Bestätigung, im Protokoll.
/// </summary>
public sealed class StorageWindow : Window
{
    private readonly MinerHub _hub;
    private readonly StackPanel _body = new() { Margin = new Thickness(14) };

    public StorageWindow(MinerHub hub)
    {
        _hub = hub;
        Title = L.T("Speicherplatz");
        Width = 720;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Draw();
    }

    private static string Mb(long bytes) => L.T("{0} MB", (bytes / 1048576.0).ToString("0.0", L.Culture));

    private TextBlock Text(string text, bool muted = false, bool bold = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        if (muted) t.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        if (bold) { t.FontWeight = FontWeights.SemiBold; t.FontSize = 14; t.Margin = new Thickness(0, 10, 0, 4); }
        return t;
    }

    private void Draw()
    {
        _body.Children.Clear();
        var s = _hub.Storage();
        _body.Children.Add(Text(L.T("Speicherplatz"), bold: true));
        if (MinerHub.StorageLow(s))
        {
            var warn = Text(L.T("Wenig freier Speicher – alte Sicherungen oder vorherige Stände löschen, sonst schlagen Sicherungen und Updates fehl."));
            warn.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            _body.Children.Add(warn);
        }
        if (s.TotalBytes is { } total && s.FreeBytes is { } free)
        {
            _body.Children.Add(new ProgressBar { Maximum = total, Value = total - free, Height = 10, Margin = new Thickness(0, 2, 0, 6) });
            _body.Children.Add(Text(L.T("Frei") + ": " + Mb(free) + " · " + L.T("von {0} ({1})", Mb(total), s.Drive)));
        }
        _body.Children.Add(Text(L.T("Datenordner gesamt") + ": " + Mb(s.DataBytes) + " · " + L.T("Verlauf (history.db)") + ": " + Mb(s.HistoryBytes) +
                                " · " + L.T("Sicherungen") + ": " + Mb(s.BackupsBytes) + " · " + L.T("Vorherige Stände") + ": " + Mb(s.PreviousBytes), muted: true));

        _body.Children.Add(Text(L.T("Sicherung im Datenordner"), bold: true));
        var files = _hub.LocalBackups();
        if (files.Count == 0) _body.Children.Add(Text(L.T("keine"), muted: true));
        foreach (var f in files)
            _body.Children.Add(Row(f.Name, f.Length, () => _hub.DeleteLocalBackup(f.Name, L.T("Desktop"))));

        _body.Children.Add(Text(L.T("Vorheriger Stand (beim Einspielen angelegt)"), bold: true));
        if (s.Previous.Count == 0) _body.Children.Add(Text(L.T("keine"), muted: true));
        foreach (var p in s.Previous)
            _body.Children.Add(Row(p.Name, p.Bytes, () => _hub.DeletePreviousState(p.Name, L.T("Desktop"))));
        _body.Children.Add(Text(L.T("Vorherige Stände sind vollständige Kopien des Datenordners, die beim Einspielen einer Sicherung entstehen. Sie werden nicht automatisch gelöscht."), muted: true));
    }

    private DockPanel Row(string name, long bytes, Action delete)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var b = new Button { Content = L.T("Löschen …"), Padding = new Thickness(10, 2, 10, 2) };
        b.Click += (_, _) =>
        {
            if (MessageBox.Show(this, L.T("{0} ({1}) wird endgültig gelöscht und kann nicht mehr eingespielt werden.", name, Mb(bytes)),
                    L.T("Endgültig löschen?"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try { delete(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
            Draw();
        };
        DockPanel.SetDock(b, Dock.Right);
        row.Children.Add(b);
        var size = new TextBlock { Text = Mb(bytes), Width = 90, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(size, Dock.Right);
        row.Children.Add(size);
        row.Children.Add(new TextBlock { Text = name, FontFamily = new System.Windows.Media.FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}
