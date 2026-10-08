using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.11 Pool-Umschaltung wie im Browser: je Miner (Pool wählen, Vorschau alt → neu, Bestätigung) oder je Gruppe
/// (alle auf Ersatz-Pool bzw. zurück), dazu die Pool-Automatik mit Freigabe.
/// </summary>
public sealed class PoolSwitchWindow : Window
{
    private readonly AppHost _host;
    private readonly ComboBox _target = new() { MinWidth = 260, Margin = new Thickness(8, 0, 0, 0) };
    private readonly StackPanel _body = new();

    private MinerHub Hub => _host.Hub;

    public PoolSwitchWindow(AppHost host, HubDevice? selected)
    {
        _host = host;
        Title = L.T("Pool-Umschaltung");
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 820;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        foreach (var d in Hub.Devices) _target.Items.Add(new ComboBoxItem { Content = d.Title, Tag = d });
        foreach (var g in MinerGroups.All(Hub.Config.Devices)) _target.Items.Add(new ComboBoxItem { Content = L.T("Gruppe: {0}", g), Tag = g });
        _target.SelectedIndex = Math.Max(0, Hub.Devices.ToList().FindIndex(d => d == selected));
        _target.SelectionChanged += async (_, _) => await ShowAsync();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(new TextBlock { Text = L.T("Miner oder Gruppe"), VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(_target);

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(top);
        root.Children.Add(_body);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += async (_, _) => await ShowAsync();
    }

    private static TextBlock Text(string text, bool muted = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        if (muted) t.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return t;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 4) };

    private static Button Btn(string text, Func<Task> click, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        if (primary) b.SetResourceReference(StyleProperty, "PrimaryButton");
        b.Click += async (_, _) => await click();
        return b;
    }

    private void Error(Exception ex) => MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private async Task ShowAsync()
    {
        _body.Children.Clear();
        switch ((_target.SelectedItem as ComboBoxItem)?.Tag)
        {
            case HubDevice d: await ShowDeviceAsync(d); break;
            case string g: ShowGroup(g); break;
        }
    }

    // ---------- Miner ----------

    private async Task ShowDeviceAsync(HubDevice d)
    {
        _body.Children.Add(Text(L.T("Lade …"), true));
        Core.Pools.PoolLayout layout;
        try { layout = await Hub.PoolLayoutAsync(d); }
        catch (Exception ex) { _body.Children.Clear(); _body.Children.Add(Text(ex.Message)); return; }
        _body.Children.Clear();

        if (layout.UsingFallback) _body.Children.Add(Text(L.T("Die Firmware meldet: Haupt-Pool nicht erreichbar, der Miner arbeitet auf dem Ersatz-Pool.")));
        if (layout.Pools.Count == 0) _body.Children.Add(Text(L.T("Der Miner meldet keine Pools."), true));
        var home = layout.Home(d.Config.HomePool);
        var makeHome = new CheckBox { Content = L.T("künftig als Haupt-Pool merken"), Margin = new Thickness(0, 6, 0, 6) };
        foreach (var pool in layout.Pools)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var role = new List<string> { pool == layout.Primary ? L.T("Haupt-Pool (Miner)") : pool == layout.Secondary ? L.T("Ersatz-Pool (Miner)") : L.T("weiterer Pool") };
            if (pool == home) role.Add(L.T("gemerkt als Haupt-Pool"));
            var label = Text($"{pool.Text}{(pool == layout.Active ? "  ✓ " + L.T("aktiv") : "")}\n{pool.User} · {string.Join(" · ", role)}");
            if (pool != layout.Active)
            {
                var key = pool.Key;
                var b = Btn(L.T("Umschalten …"), () => SwitchDeviceAsync(d, key, makeHome.IsChecked == true));
                DockPanel.SetDock(b, Dock.Right);
                b.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(b);
            }
            row.Children.Add(label);
            _body.Children.Add(row);
        }
        _body.Children.Add(makeHome);
        if (!layout.Indexed)
            _body.Children.Add(Text(L.T("Ältere Firmware: Umschalten tauscht Haupt- und Ersatz-Pool (Adresse und Benutzer); die Passwörter bleiben am Platz."), true));
        if (Hub.EffectivePoolRule(d) is { Group: { } grp })
            _body.Children.Add(Text(L.T("Für diesen Miner gilt die Pool-Automatik der Gruppe „{0}“ (eine eigene Regel hätte Vorrang).", grp), true));

        var rule = d.Config.PoolAuto.Clone();
        AddRuleEditor(rule, d.Config.PoolAuto.IsApproved(d.Host),
            () => { Hub.SavePoolRule(d, rule); return d.Config.PoolAuto.IsApproved(d.Host); },
            () => MinerHub.PoolRuleText(d.Config.PoolAuto, d.Title),
            () => Hub.ApprovePoolRule(d));
    }

    private async Task SwitchDeviceAsync(HubDevice d, string key, bool makeHome)
    {
        var item = await Hub.PoolSwitchPreviewAsync(d, key);
        if (item.Plan is not { } plan) { MessageBox.Show(this, item.Skip ?? "", Title); return; }
        var text = plan.Text + (plan.Warning is { } w ? "\n\n⚠ " + w : "") +
                   L.T("\n\nVorher wird eine Sicherung angelegt; danach startet der Miner neu. Frequenz und Spannung bleiben.");
        if (MessageBox.Show(this, text, L.T("Pool umschalten"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var done = await Hub.SwitchPoolAsync(d, key, L.T("Desktop"), makeHome);
        if (done.Plan is null) MessageBox.Show(this, done.Skip ?? "", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(this, L.T("Pool umgeschaltet – der Miner startet neu."), Title);
        await Task.Delay(3000);
        await ShowAsync();
    }

    // ---------- Gruppe ----------

    private void ShowGroup(string group)
    {
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        buttons.Children.Add(Btn(L.T("Alle auf Ersatz-Pool …"), () => SwitchGroupAsync(group, "backup")));
        buttons.Children.Add(Btn(L.T("Alle zurück zum Haupt-Pool …"), () => SwitchGroupAsync(group, "home")));
        _body.Children.Add(Text(L.T("Miner: {0}", string.Join(", ", Hub.GroupMembers(group).Select(m => m.Title)))));
        _body.Children.Add(buttons);
        var own = Hub.GroupMembers(group).Where(m => m.Config.PoolAuto.Enabled).Select(m => m.Title).ToList();
        if (own.Count > 0) _body.Children.Add(Text(L.T("Eigene Pool-Regel (Vorrang): {0}", string.Join(", ", own)), true));

        var existing = Hub.GroupPoolRuleFor(group);
        var rule = existing?.Rule.Clone() ?? new PoolSwitchRule();
        AddRuleEditor(rule, existing is not null && Hub.IsGroupPoolRuleApproved(existing),
            () => Hub.IsGroupPoolRuleApproved(Hub.SaveGroupPoolRule(group, rule)),
            () => MinerHub.PoolRuleText(Hub.GroupPoolRuleFor(group)!.Rule, L.T("Gruppe „{0}“", group)),
            () => Hub.ApproveGroupPoolRule(group));
    }

    private async Task SwitchGroupAsync(string group, string target)
    {
        try
        {
            var preview = await Hub.GroupPoolPreviewAsync(group, target);
            var count = preview.Count(i => i.Plan is not null);
            var text = MinerHub.PoolPreviewText(preview);
            if (count == 0) { MessageBox.Show(this, text, L.T("Nichts zu ändern")); return; }
            text += L.T("\n\nVor jeder Änderung wird eine Sicherung angelegt; danach startet jeder Miner neu. Jede Umschaltung wird protokolliert.");
            if (MessageBox.Show(this, text, target == "home" ? L.T("Gruppe zurück zum Haupt-Pool") : L.T("Gruppe auf Ersatz-Pool"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            MessageBox.Show(this, MinerHub.PoolPreviewText(await Hub.SwitchGroupPoolAsync(group, target)), Title);
        }
        catch (Exception ex) when (ex is InvalidOperationException or LocalizedException) { Error(ex); }
    }

    // ---------- Regel ----------

    private void AddRuleEditor(PoolSwitchRule rule, bool approved, Func<bool> save, Func<string> approvalText, Action approve)
    {
        _body.Children.Add(Heading(L.T("Pool-Automatik") + " – " + (approved ? L.T("freigegeben") : L.T("nicht freigegeben"))));
        var enabled = new CheckBox { Content = L.T("eingeschaltet"), IsChecked = rule.Enabled, Margin = new Thickness(0, 2, 0, 6) };
        _body.Children.Add(enabled);
        _body.Children.Add(Text(L.T("Ersatz-Pool nach Zeitplan – eine Zeile je Zeitfenster, z. B. „Mo-Fr 0-6“ oder „täglich 22-6“:"), true));
        var entries = new TextBox
        {
            AcceptsReturn = true, MinHeight = 60, Padding = new Thickness(4, 3, 4, 3),
            Text = string.Join("\n", rule.Entries.Select(e => $"{e.DaysText} {e.FromHour}-{e.ToHour}")),
        };
        _body.Children.Add(entries);
        var bad = new CheckBox { Content = L.T("auf den Ersatz-Pool wechseln, wenn die Pool-Überwachung zu viele abgelehnte Shares oder zu lange Antwortzeiten meldet"), IsChecked = rule.OnBadShares, Margin = new Thickness(0, 8, 0, 2) };
        var badMin = new TextBox { Text = rule.BadMinutes.ToString(CultureInfo.InvariantCulture), Width = 60, Padding = new Thickness(4, 2, 4, 2) };
        var ret = new CheckBox { Content = L.T("zurück zum Haupt-Pool, sobald er wieder erreichbar ist"), IsChecked = rule.ReturnHome, Margin = new Thickness(0, 8, 0, 2) };
        var retMin = new TextBox { Text = rule.ReturnAfterMinutes.ToString(CultureInfo.InvariantCulture), Width = 60, Padding = new Thickness(4, 2, 4, 2) };
        _body.Children.Add(bad);
        _body.Children.Add(Labeled(L.T("so lange (min)"), badMin));
        _body.Children.Add(ret);
        _body.Children.Add(Labeled(L.T("frühestens nach (min)"), retMin));

        bool Collect()
        {
            var list = new List<ScheduleEntry>();
            foreach (var line in entries.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var cut = line.LastIndexOf(' ');
                var hours = cut > 0 ? line[(cut + 1)..].Split('-') : [];
                if (cut <= 0 || hours.Length != 2 || ScheduleEntry.ParseDays(line[..cut]) is not { } days ||
                    !int.TryParse(hours[0], out var from) || !int.TryParse(hours[1], out var to))
                {
                    MessageBox.Show(this, L.T("Zeile nicht erkannt: „{0}“ – Beispiel: Mo-Fr 0-6", line), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                list.Add(new ScheduleEntry { Days = days, FromHour = from, ToHour = to });
            }
            if (!int.TryParse(badMin.Text, out var b) || !int.TryParse(retMin.Text, out var r))
            {
                MessageBox.Show(this, L.T("Bitte ganze Minuten eingeben."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            rule.Entries = list;
            rule.Enabled = enabled.IsChecked == true;
            rule.OnBadShares = bad.IsChecked == true;
            rule.ReturnHome = ret.IsChecked == true;
            rule.BadMinutes = b;
            rule.ReturnAfterMinutes = r;
            return true;
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Btn(L.T("Speichern"), async () =>
        {
            if (!Collect()) return;
            try
            {
                var ok = save();
                if (rule.Enabled && !ok) MessageBox.Show(this, L.T("Geänderte Regeln brauchen eine neue Freigabe."), Title);
                await ShowAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or LocalizedException) { Error(ex); }
        }));
        buttons.Children.Add(Btn(L.T("Speichern & freigeben …"), async () =>
        {
            if (!Collect()) return;
            try
            {
                save();
                if (MessageBox.Show(this, approvalText(), L.T("Regel freigeben"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                approve();
                await ShowAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or LocalizedException) { Error(ex); }
        }, primary: true));
        _body.Children.Add(buttons);
    }

    private static StackPanel Labeled(string label, UIElement input)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 2, 0, 2) };
        p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        p.Children.Add(input);
        return p;
    }
}
