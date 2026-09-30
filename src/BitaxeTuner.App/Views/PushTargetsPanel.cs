using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.App.Views;

/// <summary>
/// Mehrere Push-Dienste gleichzeitig (z. B. ntfy privat + Discord für eine Community-Gruppe), je Dienst eigene
/// Meldungen und Miner – eingebettet in die Einstellungen (wie in der Server-Oberfläche), gespeichert mit „Speichern“.
/// </summary>
public sealed class PushTargetsPanel : StackPanel
{
    private static readonly (NotifyCategory Cat, string Label)[] Categories =
    [
        (NotifyCategory.Offline, L.T("Miner offline")), (NotifyCategory.Overheat, L.T("Überhitzung")),
        (NotifyCategory.Finds, L.T("Blockfund und Zufluss")), (NotifyCategory.Maintenance, L.T("Watchdog, Firmware, Automatik")),
        (NotifyCategory.Record, L.T("Neuer Best-Diff-Rekord")), (NotifyCategory.LogAlerts, L.T("Treffer in Miner-Logs")),
        (NotifyCategory.Pool, L.T("Pool (Fallback, Ablehnungen, langsam)")), (NotifyCategory.Plugs, L.T("Smart Plugs (nicht erreichbar, Mehrverbrauch)")),
        (NotifyCategory.Health, L.T("Gesundheit (Kühlung, Effizienz, Lüfter, Shares)")),
        (NotifyCategory.DailyReport, L.T("Tagesbericht")), (NotifyCategory.MonthlyReport, L.T("Monatsbericht")),
    ];

    private static readonly (string Value, string Label)[] Providers =
        [("ntfy", "ntfy"), ("telegram", "Telegram"), ("discord", "Discord"), ("pushover", "Pushover"), ("webhook", L.T("Eigener Webhook"))];

    private readonly List<PushTarget> _targets;
    private readonly IReadOnlyList<(string Name, string Host)> _miners;
    private readonly StackPanel _list = new();
    private readonly List<Func<PushTarget>> _readers = [];

    /// <summary>Aktueller Stand aller Karten.</summary>
    public List<PushTarget> Targets
    {
        get
        {
            Collect();
            return _targets.Select(t => t.Clone()).ToList();
        }
    }

    public PushTargetsPanel(IEnumerable<PushTarget> targets, IReadOnlyList<(string Name, string Host)> miners)
    {
        _targets = targets.Select(t => t.Clone()).ToList();
        _miners = miners;

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = L.T("Mehrere Dienste gleichzeitig möglich – z. B. ntfy für dich und Discord für eine Community-Gruppe. Je Dienst wählst du die Meldungen und die Miner."),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var add = new Button { Content = L.T("Push-Dienst hinzufügen"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        add.Click += (_, _) =>
        {
            Collect();
            _targets.Add(new PushTarget());
            Draw();
        };
        Children.Add(hint);
        Children.Add(_list);
        Children.Add(add);
        Draw();
    }

    /// <summary>Eingaben aller Karten in die Liste übernehmen (vor Neuaufbau, Test und OK).</summary>
    private void Collect()
    {
        for (var i = 0; i < _readers.Count && i < _targets.Count; i++) _targets[i] = _readers[i]();
    }

    private void Draw()
    {
        _list.Children.Clear();
        _readers.Clear();
        if (_targets.Count == 0)
            _list.Children.Add(new TextBlock { Text = L.T("Noch kein Push-Dienst eingerichtet."), Margin = new Thickness(0, 4, 0, 4) });
        for (var i = 0; i < _targets.Count; i++) _list.Children.Add(Card(_targets[i], i));
    }

    private Border Card(PushTarget t, int index)
    {
        var name = new TextBox { Text = t.Name, Width = 170 };
        var enabled = new CheckBox { Content = L.T("aktiv"), IsChecked = t.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var provider = new ComboBox { Width = 150, DisplayMemberPath = "Label", SelectedValuePath = "Value", Margin = new Thickness(12, 0, 0, 0) };
        provider.ItemsSource = Providers.Select(p => new { p.Value, p.Label }).ToList();
        provider.SelectedValue = t.Provider;

        // Zugangsdaten je Dienst
        TextBox Box(string value) => new() { Text = value };
        var ntfyServer = Box(t.NtfyServer); var ntfyTopic = Box(t.NtfyTopic);
        var tgToken = Box(t.TelegramBotToken); var tgChat = Box(t.TelegramChatId);
        var discord = Box(t.DiscordWebhookUrl);
        var poUser = Box(t.PushoverUserKey); var poToken = Box(t.PushoverAppToken);
        var hook = Box(t.WebhookUrl);
        var fields = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        var groups = new Dictionary<string, List<FrameworkElement>>();
        void Field(string prov, int col, string label, TextBox box)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
            var tb = new TextBlock { Text = label, FontSize = 11 };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            sp.Children.Add(tb);
            sp.Children.Add(box);
            Grid.SetColumn(sp, col);
            if (col == 0 && label.Length > 40) Grid.SetColumnSpan(sp, 2);
            fields.Children.Add(sp);
            (groups.TryGetValue(prov, out var l) ? l : groups[prov] = []).Add(sp);
        }
        Field("ntfy", 0, L.T("ntfy-Server"), ntfyServer); Field("ntfy", 1, L.T("ntfy-Topic"), ntfyTopic);
        Field("telegram", 0, L.T("Telegram-Bot-Token"), tgToken); Field("telegram", 1, L.T("Telegram-Chat-ID"), tgChat);
        Field("discord", 0, L.T("DISCORD-WEBHOOK-URL (KANAL → INTEGRATIONEN → WEBHOOKS)"), discord);
        Field("pushover", 0, L.T("Pushover-User-Key"), poUser); Field("pushover", 1, L.T("Pushover-App-Token"), poToken);
        Field("webhook", 0, L.T("WEBHOOK-URL (JSON-POST: TITLE, MESSAGE, PRIORITY)"), hook);
        void ShowFields()
        {
            foreach (var (prov, els) in groups)
                foreach (var el in els) el.Visibility = (provider.SelectedValue as string) == prov ? Visibility.Visible : Visibility.Collapsed;
        }
        provider.SelectionChanged += (_, _) => ShowFields();
        ShowFields();

        var cats = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var catBoxes = Categories.Select(c =>
        {
            var cb = new CheckBox { Content = c.Label, IsChecked = t.Wants(c.Cat) && c.Cat != NotifyCategory.Other, Margin = new Thickness(0, 2, 14, 2) };
            cats.Children.Add(cb);
            return (c.Cat, cb);
        }).ToList();

        var minerPanel = new WrapPanel { Margin = new Thickness(18, 0, 0, 0) };
        var minerBoxes = _miners.Select(m =>
        {
            var cb = new CheckBox { Content = m.Name, IsChecked = t.Miners.Contains(m.Host, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 2, 14, 2) };
            minerPanel.Children.Add(cb);
            return (m.Host, cb);
        }).ToList();
        var all = new CheckBox { Content = L.T("alle Miner"), IsChecked = t.Miners.Count == 0, Margin = new Thickness(0, 4, 0, 2) };
        void ShowMiners() => minerPanel.Visibility = all.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        all.Click += (_, _) => ShowMiners();
        ShowMiners();

        PushTarget Read() => new()
        {
            Id = t.Id, Name = name.Text.Trim(), Enabled = enabled.IsChecked == true, Provider = provider.SelectedValue as string ?? "ntfy",
            NtfyServer = ntfyServer.Text.Trim(), NtfyTopic = ntfyTopic.Text.Trim(), TelegramBotToken = tgToken.Text.Trim(), TelegramChatId = tgChat.Text.Trim(),
            DiscordWebhookUrl = discord.Text.Trim(), PushoverUserKey = poUser.Text.Trim(), PushoverAppToken = poToken.Text.Trim(), WebhookUrl = hook.Text.Trim(),
            Categories = catBoxes.Where(c => c.cb.IsChecked == true).Select(c => c.Cat.ToString()).ToList(),
            Miners = all.IsChecked == true ? [] : minerBoxes.Where(m => m.cb.IsChecked == true).Select(m => m.Host).ToList(),
        };
        _readers.Add(Read);

        var result = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
        var test = new Button { Content = L.T("Test senden"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(12, 0, 0, 0) };
        test.Click += async (_, _) =>
        {
            var target = Read();
            result.Text = L.T("Sende …");
            using var service = new NotificationService(() => new NotificationSettings { Targets = [target] });
            var error = await service.TestAsync(target);
            result.Text = error is null ? L.T("Gesendet – kam sie an?") : L.T("Fehler: ") + error;
        };
        var remove = new Button { Content = L.T("Entfernen"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        remove.SetResourceReference(StyleProperty, "DangerButton");
        remove.Click += (_, _) =>
        {
            Collect();
            _targets.RemoveAt(index);
            Draw();
        };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(name);
        head.Children.Add(provider);
        head.Children.Add(enabled);
        head.Children.Add(test);
        head.Children.Add(remove);
        head.Children.Add(result);

        var label = new TextBlock { Text = L.T("Meldungen"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var content = new StackPanel();
        content.Children.Add(head);
        content.Children.Add(fields);
        content.Children.Add(label);
        content.Children.Add(cats);
        var minerLabel = new TextBlock { Text = L.T("Miner"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        minerLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        content.Children.Add(minerLabel);
        content.Children.Add(all);
        content.Children.Add(minerPanel);
        var card = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = content };
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        return card;
    }
}
