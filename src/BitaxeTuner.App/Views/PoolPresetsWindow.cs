using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.14 Pool-Steuerung wie im Browser („Pools“): Pool-Vorlagen anlegen und bearbeiten, dann je Miner als Haupt- bzw.
/// Ersatz-Pool übertragen – mit Vorschau alt → neu, Sicherung, Neustart und Protokoll.
/// </summary>
public sealed class PoolPresetsWindow : Window
{
    private readonly AppHost _host;
    private readonly List<PoolPreset> _presets;
    private readonly Dictionary<PoolPreset, PasswordBox> _passwords = new();
    private readonly HashSet<PoolPreset> _clearPassword = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _assign = new();
    private readonly Dictionary<string, (ComboBox Primary, ComboBox Fallback, TextBlock Current)> _rows = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    private MinerHub Hub => _host.Hub;

    private static (string Value, string Label)[] WorkerModes =>
        [("keep", L.T("Worker des Miners behalten")), ("name", L.T("Minername als Worker")), ("none", L.T("Benutzer genau so übernehmen"))];

    private static string[] TlsModes => [L.T("aus"), "TLS", L.T("TLS (eigenes Zertifikat)")];

    public PoolPresetsWindow(AppHost host)
    {
        _host = host;
        _presets = host.Config.PoolPresets.Select(p => p.Clone()).ToList();
        Title = L.T("Pool-Steuerung");
        Width = 900;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(Heading(L.T("Pool-Vorlagen")));
        root.Children.Add(Text(L.T("Trage hier deine Pools einmal ein und übertrage sie dann auf einzelne oder alle Miner – als Haupt- oder Ersatz-Pool. Funktioniert mit AxeOS ab 2.15 (Gamma, Pool-Liste) und älterer Firmware."), true));
        root.Children.Add(_list);

        var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        tools.Children.Add(Btn(L.T("Vorlage hinzufügen"), () =>
        {
            _presets.Add(new PoolPreset());
            _dirty = true;
            DrawPresets();
            return Task.CompletedTask;
        }));
        var from = new ComboBox { MinWidth = 240, Margin = new Thickness(0, 0, 8, 0) };
        from.Items.Add(new ComboBoxItem { Content = L.T("Pools eines Miners übernehmen …"), IsSelected = true });
        foreach (var d in Hub.Devices) from.Items.Add(new ComboBoxItem { Content = d.Title, Tag = d });
        from.SelectionChanged += async (_, _) =>
        {
            if ((from.SelectedItem as ComboBoxItem)?.Tag is not HubDevice d) return;
            from.SelectedIndex = 0;
            await FromDeviceAsync(d);
        };
        tools.Children.Add(from);
        tools.Children.Add(Btn(L.T("Vorlagen speichern"), () => { Save(); return Task.CompletedTask; }, primary: true));
        root.Children.Add(tools);

        root.Children.Add(Heading(L.T("Auf Miner übertragen")));
        root.Children.Add(Text(L.T("Wähle je Miner die Vorlage für Haupt- und Ersatz-Pool. „unverändert“ lässt den Eintrag, wie er ist. Der Worker-Name richtet sich nach der Vorlage."), true));
        root.Children.Add(_assign);
        root.Children.Add(Text(L.T("Umschalten zwischen Haupt- und Ersatz-Pool sowie die Pool-Automatik findest du unter „Pool …“."), true));

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DrawPresets();
        DrawAssign();
        Loaded += async (_, _) => await LoadCurrentAsync();
        Closing += (_, e) =>
        {
            if (_dirty && MessageBox.Show(this, L.T("Die geänderten Vorlagen sind noch nicht gespeichert. Trotzdem schließen?"), Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
    }

    private static TextBlock Text(string text, bool muted = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        if (muted) t.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return t;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 4) };

    private static Button Btn(string text, Func<Task> click, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        if (primary) b.SetResourceReference(StyleProperty, "PrimaryButton");
        b.Click += async (_, _) => await click();
        return b;
    }

    private static FrameworkElement Field(string label, FrameworkElement input)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 10, 6) };
        var l = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        s.Children.Add(l);
        s.Children.Add(input);
        return s;
    }

    // ---------- Vorlagen ----------

    private void DrawPresets()
    {
        _list.Children.Clear();
        _passwords.Clear();
        if (_presets.Count == 0) _list.Children.Add(Text(L.T("Noch keine Vorlage. Lege eine an oder übernimm die Pools eines Miners."), true));
        foreach (var p in _presets)
        {
            var preset = p;
            var grid = new Grid();
            for (var c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            void Put(FrameworkElement e, int row, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); grid.Children.Add(e); }
            TextBox Box(string value, Action<string> set, bool mono = false)
            {
                var t = new TextBox { Text = value };
                if (mono) t.FontFamily = new System.Windows.Media.FontFamily("Consolas");
                t.TextChanged += (_, _) => { set(t.Text); _dirty = true; };
                return t;
            }
            Put(Field(L.T("Name"), Box(preset.Name, v => preset.Name = v)), 0, 0);
            Put(Field(L.T("Pool-Adresse (stratum+tcp://… geht auch)"), Box(preset.Url, v => preset.Url = v.Trim(), true)), 0, 1);
            Put(Field(L.T("Port"), Box(preset.Port.ToString(L.Culture), v => preset.Port = int.TryParse(v, out var x) ? x : 0)), 0, 2);
            Put(Field(L.T("Benutzer (Wallet-Adresse oder Pool-Konto)"), Box(preset.User, v => preset.User = v.Trim(), true)), 0, 3);
            var worker = new ComboBox();
            foreach (var (value, label) in WorkerModes) worker.Items.Add(new ComboBoxItem { Content = label, Tag = value, IsSelected = value == preset.Worker });
            worker.SelectionChanged += (_, _) => { preset.Worker = (string)((ComboBoxItem)worker.SelectedItem).Tag; _dirty = true; };
            Put(Field(L.T("Worker-Name"), worker), 1, 0);
            var pw = new PasswordBox { ToolTip = preset.Password.Length > 0 ? L.T("gespeichert – leer lassen = unverändert") : L.T("leer = Passwort auf dem Miner bleibt") };
            pw.PasswordChanged += (_, _) => _dirty = true;
            _passwords[preset] = pw;
            Put(Field(preset.Password.Length > 0 ? L.T("Passwort (gespeichert)") : L.T("Passwort"), pw), 1, 1);
            var tls = new ComboBox { ItemsSource = TlsModes, SelectedIndex = Math.Clamp(preset.Tls, 0, 2) };
            tls.SelectionChanged += (_, _) => { preset.Tls = tls.SelectedIndex; _dirty = true; };
            Put(Field("TLS", tls), 1, 2);
            Put(Field(L.T("Notiz"), Box(preset.Notes, v => preset.Notes = v)), 1, 3);

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var remove = Btn(L.T("Entfernen"), () => { _presets.Remove(preset); _dirty = true; DrawPresets(); return Task.CompletedTask; });
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);
            if (preset.Password.Length > 0)
            {
                var clear = new CheckBox { Content = L.T("Gespeichertes Passwort löschen"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                clear.Checked += (_, _) => { _clearPassword.Add(preset); _dirty = true; };
                clear.Unchecked += (_, _) => _clearPassword.Remove(preset);
                DockPanel.SetDock(clear, Dock.Right);
                head.Children.Add(clear);
            }
            head.Children.Add(new TextBlock
            {
                Text = preset.Name.Length > 0 ? preset.Name : preset.Url.Length > 0 ? preset.Url : L.T("Neue Vorlage"),
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            });

            var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 4), Margin = new Thickness(0, 0, 0, 8) };
            card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
            var inner = new StackPanel();
            inner.Children.Add(head);
            inner.Children.Add(grid);
            card.Child = inner;
            _list.Children.Add(card);
        }
    }

    private bool Save()
    {
        try
        {
            var list = _presets.Select(p =>
            {
                var copy = p.Clone();
                if (_clearPassword.Contains(p)) copy.Password = "";
                if (_passwords.TryGetValue(p, out var box) && box.Password.Length > 0) copy.Password = box.Password;
                return copy;
            }).ToList();
            Hub.SavePoolPresets(list, L.T("Desktop"));
        }
        catch (Exception ex) when (ex is LocalizedException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _presets.Clear();
        _presets.AddRange(Hub.Config.PoolPresets.Select(p => p.Clone()));
        _clearPassword.Clear();
        _dirty = false;
        DrawPresets();
        DrawAssign();
        return true;
    }

    private async Task FromDeviceAsync(HubDevice d)
    {
        try
        {
            var known = _presets.Select(p => $"{p.Url.ToLowerInvariant()}:{p.Port}|{p.User}").ToHashSet();
            var add = (await Hub.PoolPresetsFromDeviceAsync(d)).Where(p => !known.Contains($"{p.Url.ToLowerInvariant()}:{p.Port}|{p.User}")).ToList();
            _presets.AddRange(add);
            if (add.Count > 0) _dirty = true;
            DrawPresets();
            MessageBox.Show(this, add.Count > 0 ? L.T("{0} Vorlagen übernommen – Namen prüfen und speichern.", add.Count) : L.T("Diese Pools gibt es schon als Vorlage."), Title);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Übertragen ----------

    private ComboBox PresetChoice()
    {
        var c = new ComboBox { MinWidth = 180, Margin = new Thickness(0, 2, 8, 2) };
        c.Items.Add(new ComboBoxItem { Content = L.T("– unverändert –"), IsSelected = true });
        foreach (var p in Hub.Config.PoolPresets) c.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
        return c;
    }

    private void DrawAssign()
    {
        _assign.Children.Clear();
        var old = _rows.ToDictionary(kv => kv.Key, kv => kv.Value.Current.Text);
        _rows.Clear();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var r = 0;
        void Add(UIElement e, int col) { Grid.SetRow(e, r); Grid.SetColumn(e, col); grid.Children.Add(e); }
        grid.RowDefinitions.Add(new RowDefinition());
        foreach (var (text, col) in new[] { (L.T("Miner"), 0), (L.T("Aktuell"), 1), (L.T("Haupt-Pool"), 2), (L.T("Ersatz-Pool"), 3) })
            Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 4) }, col);

        // „alle Miner“ setzt die Auswahl aller Zeilen
        r++;
        grid.RowDefinitions.Add(new RowDefinition());
        Add(new TextBlock { Text = L.T("alle Miner"), FontStyle = FontStyles.Italic, VerticalAlignment = VerticalAlignment.Center }, 0);
        var allPrimary = PresetChoice();
        var allFallback = PresetChoice();
        allPrimary.SelectionChanged += (_, _) => { foreach (var row in _rows.Values) row.Primary.SelectedIndex = allPrimary.SelectedIndex; };
        allFallback.SelectionChanged += (_, _) => { foreach (var row in _rows.Values) row.Fallback.SelectedIndex = allFallback.SelectedIndex; };
        Add(allPrimary, 2);
        Add(allFallback, 3);

        foreach (var d in Hub.Devices)
        {
            r++;
            grid.RowDefinitions.Add(new RowDefinition());
            Add(new TextBlock { Text = d.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 0);
            var current = new TextBlock { Text = old.GetValueOrDefault(d.Host, L.T("Lade …")), FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            current.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            Add(current, 1);
            var primary = PresetChoice();
            var fallback = PresetChoice();
            Add(primary, 2);
            Add(fallback, 3);
            _rows[d.Host] = (primary, fallback, current);
        }
        _assign.Children.Add(grid);
        var go = Btn(L.T("Vorschau und übertragen …"), TransferAsync, primary: true);
        go.HorizontalAlignment = HorizontalAlignment.Left;
        go.Margin = new Thickness(0, 10, 0, 0);
        _assign.Children.Add(go);
    }

    private async Task LoadCurrentAsync()
    {
        foreach (var d in Hub.Devices)
        {
            string text;
            try
            {
                var layout = await Hub.PoolLayoutAsync(d);
                text = L.T("Haupt: {0}", layout.Primary?.Text ?? "–") + (layout.Active == layout.Primary ? " ✓" : "") + "\n" +
                       L.T("Ersatz: {0}", layout.Secondary?.Text ?? "–") + (layout.UsingFallback ? " ✓" : "");
            }
            catch (Exception)
            {
                text = L.T("nicht erreichbar");
            }
            if (_rows.TryGetValue(d.Host, out var row)) row.Current.Text = text;
        }
    }

    private static string? IdOf(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag as string;

    private async Task TransferAsync()
    {
        if (_dirty && !Save()) return;
        var items = _rows.Select(kv => new PoolAssignment(kv.Key, IdOf(kv.Value.Primary), IdOf(kv.Value.Fallback)))
            .Where(a => a.PrimaryId is not null || a.FallbackId is not null).ToList();
        if (items.Count == 0)
        {
            MessageBox.Show(this, L.T("Bitte für mindestens einen Miner eine Vorlage wählen."), Title);
            return;
        }
        var previews = new List<PoolAssignItem>();
        foreach (var a in items) previews.Add(await Hub.PoolAssignPreviewAsync(Hub.Device(a.Host)!, a));
        var text = string.Join("\n\n", previews.Select(p => p.Device.Title + "\n" + (p.Skip is { } s
            ? "  " + L.T("übersprungen – {0}", s)
            : string.Join("\n", p.Changes.Select(c => $"  • {c.Label}: {c.Old} → {c.New}")))));
        var todo = previews.Where(p => p.Skip is null).Select(p => p.Device.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (todo.Count == 0)
        {
            MessageBox.Show(this, text, L.T("Nichts zu übertragen"));
            return;
        }
        text += "\n\n" + L.T("Vorher wird eine Sicherung angelegt. Danach startet der Miner neu (Frequenz und Spannung bleiben). Alles wird protokolliert.");
        if (MessageBox.Show(this, text, L.T("Pools auf {0} Miner übertragen?", todo.Count), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var results = await Hub.ApplyPoolAssignmentsAsync(items.Where(a => todo.Contains(a.Host)).ToList(), L.T("Desktop"));
        var ok = results.Count(x => x.Skip is null);
        var failed = string.Join("\n", results.Where(x => x.Skip is not null).Select(x => $"{x.Device.Title}: {x.Skip}"));
        MessageBox.Show(this, L.T("Pools übertragen: {0} von {1} Minern. Sie starten jetzt neu.", ok, results.Count) + (failed.Length > 0 ? "\n\n" + failed : ""),
            Title, MessageBoxButton.OK, failed.Length > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        DrawAssign();
        await Task.Delay(15000);
        if (IsLoaded) await LoadCurrentAsync();
    }
}
