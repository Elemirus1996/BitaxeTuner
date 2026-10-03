using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Profiles;

namespace BitaxeTuner.App.Views;

/// <summary>
/// Geräteprofile bearbeiten (0.9.9, wie im Browser): Liste links, Formular rechts. Eigene Profile anlegen (als Kopie),
/// eingebaute anpassen oder zurücksetzen; Grenzen über dem eingebauten Profil nur nach Bestätigung.
/// </summary>
public sealed class ProfilesWindow : Window
{
    private readonly MinerHub _hub;
    private readonly ListBox _list = new() { Width = 250, Margin = new Thickness(0, 0, 14, 0) };
    private readonly StackPanel _form = new();
    private readonly Button _save = new() { Content = L.T("Profil speichern"), Padding = new Thickness(14, 4, 14, 4) };
    private readonly Button _copy = new() { Content = L.T("Kopie anlegen"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _remove = new() { Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    private ProfileEntry? _entry;
    private DeviceProfile? _draft;
    private bool _isNew;
    private readonly List<Action> _commit = [];

    public ProfilesWindow(MinerHub hub)
    {
        _hub = hub;
        Title = L.T("Geräteprofile");
        Width = 900;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var intro = Muted(L.T("Ein Profil legt je Modell die Grenzen für Frequenz, Spannung, Temperatur und Leistung fest. Eingebaute Profile lassen sich anpassen und jederzeit zurücksetzen; für Sonderfälle (z. B. Umbau mit größerer Kühlung) eine Kopie anlegen und dem Miner unter Gerät → Live zuweisen."));
        intro.Margin = new Thickness(0, 0, 0, 12);
        _save.SetResourceReference(StyleProperty, "PrimaryButton");
        _save.Click += (_, _) => Save();
        _copy.Click += (_, _) => StartCopy();
        _remove.Click += (_, _) => Remove();
        var openFile = new Button { Content = L.T("profiles.json öffnen"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0),
            ToolTip = L.T("Für Fortgeschrittene: die Datei direkt bearbeiten (danach das Programm neu starten).") };
        openFile.Click += (_, _) =>
        {
            var path = ProfileRegistry.WriteUserTemplate(_hub.ProfilesDirectory);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_save);
        buttons.Children.Add(_copy);
        buttons.Children.Add(_remove);
        buttons.Children.Add(openFile);

        var right = new DockPanel();
        var bottom = new StackPanel();
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom);
        right.Children.Add(bottom);
        right.Children.Add(new ScrollViewer { Content = _form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var main = new DockPanel();
        DockPanel.SetDock(_list, Dock.Left);
        main.Children.Add(_list);
        main.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);
        root.Children.Add(main);
        Content = root;

        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListBoxItem { Tag: ProfileEntry e }) Show(e, isNew: false); };
        Reload(null);
    }

    private void Reload(string? select)
    {
        _list.Items.Clear();
        foreach (var e in _hub.ListProfiles())
        {
            var mark = !e.IsBuiltIn ? L.T("eigen") : e.IsCustomized ? L.T("angepasst") : null;
            var item = new ListBoxItem { Content = mark is null ? e.Profile.Name : $"{e.Profile.Name}  ({mark})", Tag = e, Padding = new Thickness(4, 3, 4, 3) };
            _list.Items.Add(item);
            if (select is not null && string.Equals(e.Profile.Id, select, StringComparison.OrdinalIgnoreCase)) _list.SelectedItem = item;
        }
        if (_list.SelectedItem is null && _list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void Show(ProfileEntry entry, bool isNew)
    {
        _entry = entry;
        _isNew = isNew;
        _draft = entry.Profile.Clone();
        _commit.Clear();
        _form.Children.Clear();
        _status.Text = "";
        var p = _draft;
        var b = entry.BuiltIn;

        _form.Children.Add(new TextBlock { Text = isNew ? L.T("Neues Profil") : entry.Profile.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var devices = _hub.Devices.Where(d => string.Equals(d.Profile.Id, entry.Profile.Id, StringComparison.OrdinalIgnoreCase)).Select(d => d.Title).ToList();
        if (!isNew && devices.Count > 0) _form.Children.Add(Muted(L.T("Verwendet von: {0}", string.Join(", ", devices))));

        Grid(
            Text(L.T("Kennung"), p.Id, v => p.Id = v, enabled: isNew),
            Text(L.T("Name"), p.Name, v => p.Name = v),
            Text(L.T("Familie"), p.Family, v => p.Family = v),
            Text(L.T("ASIC-Modell"), p.AsicModel, v => p.AsicModel = v),
            Number(L.T("ASIC-Anzahl"), p.AsicCount, v => p.AsicCount = (int)v, b?.AsicCount),
            Number(L.T("Small-Cores je ASIC"), p.SmallCoresPerAsic, v => p.SmallCoresPerAsic = (int)v, b?.SmallCoresPerAsic));
        Heading(L.T("Frequenz und Spannung"));
        Grid(
            Number(L.T("Min. Frequenz (MHz)"), p.MinFrequencyMhz, v => p.MinFrequencyMhz = (int)v, b?.MinFrequencyMhz),
            Number(L.T("Standard-Frequenz (MHz)"), p.DefaultFrequencyMhz, v => p.DefaultFrequencyMhz = (int)v, b?.DefaultFrequencyMhz),
            Number(L.T("Max. Frequenz (MHz)"), p.MaxFrequencyMhz, v => p.MaxFrequencyMhz = (int)v, b?.MaxFrequencyMhz),
            Number(L.T("Min. Spannung (mV)"), p.MinVoltageMv, v => p.MinVoltageMv = (int)v, b?.MinVoltageMv),
            Number(L.T("Standard-Spannung (mV)"), p.DefaultVoltageMv, v => p.DefaultVoltageMv = (int)v, b?.DefaultVoltageMv),
            Number(L.T("Max. Spannung (mV)"), p.MaxVoltageMv, v => p.MaxVoltageMv = (int)v, b?.MaxVoltageMv));
        Heading(L.T("Sicherheitsgrenzen"));
        Grid(
            Number(L.T("Max. Chiptemperatur (°C)"), p.MaxChipTempC, v => p.MaxChipTempC = v, b?.MaxChipTempC),
            Number(L.T("Max. VR-Temperatur (°C)"), p.MaxVrTempC, v => p.MaxVrTempC = v, b?.MaxVrTempC),
            Number(L.T("Max. Leistung (W)"), p.MaxPowerW, v => p.MaxPowerW = v, b?.MaxPowerW),
            Optional(L.T("Min. Eingangsspannung (mV)"), p.MinInputVoltageMv, v => p.MinInputVoltageMv = v, b?.MinInputVoltageMv),
            Optional(L.T("Max. Eingangsspannung (mV)"), p.MaxInputVoltageMv, v => p.MaxInputVoltageMv = v, b?.MaxInputVoltageMv));
        Heading(L.T("Erkennung"));
        Grid(
            Text(L.T("Gerätemodell enthält (durch Komma getrennt)"), string.Join(", ", p.DeviceModelMatches), v => p.DeviceModelMatches = Split(v)),
            Text(L.T("Board-Versionen (durch Komma getrennt)"), string.Join(", ", p.BoardVersions), v => p.BoardVersions = Split(v)),
            Text(L.T("Notiz"), p.Notes ?? "", v => p.Notes = v));
        var note = Muted(L.T("Frequenz und Spannung eines Miners werden nur innerhalb dieser Grenzen gesetzt – vom Benchmark, der Automatik und beim manuellen Einstellen. Die Grenzen selbst ändern nichts am Miner."));
        note.Margin = new Thickness(0, 10, 0, 0);
        _form.Children.Add(note);

        _copy.IsEnabled = !isNew;
        _remove.Visibility = !isNew && (!entry.IsBuiltIn || entry.IsCustomized) ? Visibility.Visible : Visibility.Collapsed;
        _remove.Content = entry.IsBuiltIn ? L.T("Zurücksetzen") : L.T("Löschen");
    }

    private void StartCopy()
    {
        if (_entry is null) return;
        var copy = ProfileEditor.CopyOf(_entry.Profile, _hub.ListProfiles().Select(e => e.Profile.Id));
        _list.SelectedItem = null;
        Show(new ProfileEntry(copy, _entry.BuiltIn, false, false), isNew: true);
    }

    private void Save()
    {
        if (_entry is null || _draft is null) return;
        try
        {
            foreach (var c in _commit) c();
        }
        catch (FormatException)
        {
            _status.Text = L.T("Bitte nur Zahlen eintragen.");
            return;
        }
        try
        {
            var original = _isNew ? null : _entry.Profile.Id;
            var check = _hub.SaveProfile(_draft, original, confirmed: false);
            if (check.Warnings.Count > 0)
            {
                var text = L.T("Diese Werte liegen über den Grenzen, die BitaxeTuner für dieses Modell vorsieht. Höhere Grenzen können den Miner beschädigen – nur übernehmen, wenn du dir sicher bist.")
                           + "\n\n• " + string.Join("\n• ", check.Warnings)
                           + (check.Changes.Count > 0 ? "\n\n" + string.Join("\n", check.Changes) : "");
                if (MessageBox.Show(this, text, L.T("Grenzen über dem eingebauten Profil"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                    return;
                _hub.SaveProfile(_draft, original, confirmed: true);
            }
            var id = _draft.Id.Trim().ToLowerInvariant();
            Reload(id);
            _status.Text = L.T("Profil gespeichert.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            _status.Text = ex.Message;
        }
    }

    private void Remove()
    {
        if (_entry is null) return;
        var reset = _entry.IsBuiltIn;
        var question = reset
            ? L.T("„{0}“ auf den eingebauten Stand zurücksetzen? Deine Änderungen gehen verloren.", _entry.Profile.Name)
            : L.T("„{0}“ löschen? Miner, denen es fest zugewiesen ist, werden wieder automatisch erkannt.", _entry.Profile.Name);
        if (MessageBox.Show(this, question, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            _hub.RemoveProfile(_entry.Profile.Id);
            Reload(reset ? _entry.Profile.Id : null);
            _status.Text = reset ? L.T("Profil zurückgesetzt.") : L.T("Profil gelöscht.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or IOException)
        {
            _status.Text = ex.Message;
        }
    }

    // ---------- Formular-Bausteine ----------

    private void Heading(string text) =>
        _form.Children.Add(new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });

    private void Grid(params FrameworkElement[] fields)
    {
        var wrap = new WrapPanel();
        foreach (var f in fields) wrap.Children.Add(f);
        _form.Children.Add(wrap);
    }

    private FrameworkElement Text(string label, string value, Action<string> set, bool enabled = true)
    {
        var box = new TextBox { Text = value, IsEnabled = enabled, Padding = new Thickness(4, 3, 4, 3) };
        _commit.Add(() => set(box.Text));
        return Field(label, box, null);
    }

    private FrameworkElement Number(string label, double value, Action<double> set, double? builtIn)
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), Padding = new Thickness(4, 3, 4, 3) };
        _commit.Add(() => set(double.Parse(box.Text.Trim(), CultureInfo.CurrentCulture)));
        return Field(label, box, builtIn);
    }

    private FrameworkElement Optional(string label, double? value, Action<double?> set, double? builtIn)
    {
        var box = new TextBox { Text = value?.ToString(CultureInfo.CurrentCulture) ?? "", Padding = new Thickness(4, 3, 4, 3) };
        _commit.Add(() => set(box.Text.Trim().Length == 0 ? null : double.Parse(box.Text.Trim(), CultureInfo.CurrentCulture)));
        return Field(label, box, builtIn);
    }

    private static FrameworkElement Field(string label, Control input, double? builtIn)
    {
        var panel = new StackPanel { Width = 190, Margin = new Thickness(0, 0, 12, 8) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 2), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(input);
        if (builtIn is { } b)
            panel.Children.Add(Muted(L.T("eingebaut: {0}", b.ToString(CultureInfo.CurrentCulture))));
        return panel;
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return t;
    }

    private static List<string> Split(string v) => v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
}
