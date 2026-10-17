using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.24: Chat-Bots wie in der Server-Oberfläche (Karte „Chat-Bots“): Telegram über den Bot eines Push-Dienstes, Discord als
/// fertige Bot-Vorlage (Token einfügen → Einrichten → Einladen → Kanal-ID). Nur lesend; gespeichert mit „Speichern“.
/// </summary>
public sealed class ChatBotPanel : StackPanel
{
    private readonly AppHost _host;
    private readonly Func<IReadOnlyList<PushTarget>> _targets;
    private readonly CheckBox _tg = new() { Margin = new Thickness(0, 4, 0, 4) }, _dc = new() { Margin = new Thickness(0, 8, 0, 4) };
    private readonly CheckBox _live = new() { Margin = new Thickness(0, 8, 0, 4) };
    // 0.9.23
    private readonly CheckBox _presence = new() { Margin = new Thickness(0, 4, 0, 4) }, _alerts = new() { Margin = new Thickness(0, 8, 0, 4) };
    private readonly Dictionary<string, CheckBox> _private = new();
    private readonly ComboBox _tgTarget = new() { DisplayMemberPath = "Title", SelectedValuePath = "Id", Margin = new Thickness(0, 0, 0, 6) };
    private readonly PasswordBox _token = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _guild = new() { Margin = new Thickness(0, 0, 0, 6) }, _channel = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _minutes = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _invite = new() { Content = L.T("Bot einladen"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };

    public ChatBotPanel(AppHost host, Func<IReadOnlyList<PushTarget>> targets)
    {
        _host = host;
        _targets = targets;
        var s = host.Hub.Config.ChatBot;
        Children.Add(Muted(L.T("Frag BitaxeTuner im Chat: /status, /miner, /history, /records, /pool, /power, /system, /bitcoin, /help – mit Bild der passenden E-Paper-Seite. Nur lesen: keine Frequenz, Spannung oder Lüfter, keine IP-Adressen, Wallets oder Tokens. Der Bot läuft in BitaxeTuner selbst, ohne fremden Server und ohne Portfreigabe.")));

        _tg.Content = L.T("Telegram-Bot einschalten");
        _tg.IsChecked = s.TelegramEnabled;
        Children.Add(_tg);
        Children.Add(Label(L.T("Bot des Push-Dienstes")));
        _tgTarget.DropDownOpened += (_, _) => RefreshTargets();
        RefreshTargets();
        _tgTarget.SelectedValue = s.TelegramTargetId;
        Children.Add(_tgTarget);
        Children.Add(Muted(L.T("Es antwortet derselbe Bot wie bei den Push-Meldungen – nur im eingetragenen Chat. Andere Chats bekommen keine Antwort.")));

        _dc.Content = L.T("Discord-Bot einschalten");
        _dc.IsChecked = s.DiscordEnabled;
        Children.Add(_dc);
        Children.Add(Muted(L.T("Im Discord Developer Portal „New Application“ anlegen (Name egal), unter „Bot“ → „Reset Token“ das Token kopieren.") + " " +
                           L.T("Token hier einfügen und „Einrichten“ – BitaxeTuner setzt Name und Bild, trägt die Befehle ein und erzeugt den Einladungslink.") + " " +
                           L.T("Über den Einladungslink den Bot auf deinen Discord-Server holen.") + " " +
                           L.T("Kanal-ID eintragen (Discord: Einstellungen → Erweitert → Entwicklermodus, dann Rechtsklick auf den Kanal → „Kanal-ID kopieren“) und speichern.")));
        Children.Add(Label(L.T("Bot-Token")));
        Children.Add(_token);
        Children.Add(Label(L.T("Server-ID (optional)")));
        _guild.Text = s.DiscordGuildId;
        Children.Add(_guild);
        Children.Add(Label(L.T("Kanal-ID")));
        _channel.Text = s.DiscordChannelId;
        Children.Add(_channel);
        var setup = new Button { Content = L.T("Einrichten …"), Padding = new Thickness(12, 4, 12, 4) };
        setup.Click += async (_, _) => await SetupAsync();
        _invite.Click += (_, _) =>
        {
            if (InviteUrl() is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(setup);
        row.Children.Add(_invite);
        row.Children.Add(_result);
        Children.Add(row);
        _invite.IsEnabled = InviteUrl() is not null;

        // 0.9.23 Bot-Status, private Antworten, Warnungen mit Knöpfen
        _presence.Content = L.T("Bot-Status unter dem Namen zeigen (z. B. „3/3 online · 3,2 TH/s“)");
        _presence.IsChecked = s.Presence;
        Children.Add(_presence);
        Children.Add(Label(L.T("Nur für den Fragenden sichtbar")));
        var priv = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (name, _) in MinerHub.ChatCommandNames.Where(c => c.Name != "help"))
        {
            var box = new CheckBox { Content = "/" + name, IsChecked = s.PrivateCommands.Contains(name), Margin = new Thickness(0, 2, 14, 2) };
            _private[name] = box;
            priv.Children.Add(box);
        }
        Children.Add(priv);
        Children.Add(Muted(L.T("Antworten kommen bei Discord als farbige Karte (grün/gelb/rot nach Zustand); /miner und /history schlagen die Minernamen vor.")));
        _alerts.Content = L.T("Warnungen (offline, Überhitzung, Lüfter steht) zusätzlich in den Chat – mit Knöpfen [Details] [Verlauf]");
        _alerts.IsChecked = s.AlertButtons;
        Children.Add(_alerts);
        Children.Add(Muted(L.T("Die Knöpfe zeigen nur Werte und Verlauf an, sie ändern nichts. Telegram und Discord; dieselbe Warnung höchstens alle 30 Minuten.")));

        _live.Content = L.T("Angepinnte Nachricht mit dem aktuellen Stand pflegen");
        _live.IsChecked = s.LiveStatus;
        Children.Add(_live);
        Children.Add(Label(L.T("alle (Minuten)")));
        _minutes.Text = s.LiveMinutes.ToString(L.Culture);
        Children.Add(_minutes);
        Children.Add(Muted(L.T("Berichte und Feiern (Rekord, Blockfund, Auszahlung) kommen wie bisher über die Push-Dienste – bei Telegram in denselben Chat, bei Discord über den Webhook.")));
    }

    private void RefreshTargets()
    {
        var keep = _tgTarget.SelectedValue;
        _tgTarget.ItemsSource = _targets().Where(t => t.Provider == "telegram").ToList();
        _tgTarget.SelectedValue = keep;
    }

    private string? InviteUrl()
    {
        var s = _host.Hub.Config.ChatBot;
        return s.DiscordAppId.Length == 0 ? null
            : $"https://discord.com/oauth2/authorize?client_id={s.DiscordAppId}&scope=bot%20applications.commands&permissions={Core.Chat.DiscordBot.Permissions}" +
              (s.DiscordGuildId.Length > 0 ? $"&guild_id={s.DiscordGuildId}&disable_guild_select=true" : "");
    }

    private async Task SetupAsync()
    {
        _result.Text = L.T("Richte ein …");
        try
        {
            var r = await _host.Hub.SetupDiscordAsync(_token.Password, _guild.Text, null);
            _result.Text = L.T("Discord eingerichtet: {0} Befehle eingetragen.", r.Commands);
            _invite.IsEnabled = true;
        }
        catch (LocalizedException ex) { _result.Text = ex.In(Loc.Current); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { _result.Text = L.T("Fehler: {0}", ex.Message); }
    }

    private static TextBlock Label(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 11 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return tb;
    }

    private static TextBlock Muted(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return tb;
    }

    /// <summary>Fehlertext oder null (vor dem Speichern).</summary>
    public string? Validate()
    {
        if (_tg.IsChecked == true && _tgTarget.SelectedValue is not string { Length: > 0 })
            return L.T("Bitte den Telegram-Push-Dienst wählen, dessen Bot antworten soll.");
        if (_dc.IsChecked == true && new string(_channel.Text.Where(char.IsAsciiDigit).ToArray()).Length == 0)
            return L.T("Bitte die Kanal-ID eintragen (in Discord: Rechtsklick auf den Kanal → „Kanal-ID kopieren“).");
        return null;
    }

    /// <summary>In die Konfiguration übernehmen; Token nur, wenn neu eingegeben.</summary>
    public void Apply(AppConfig config)
    {
        if (_token.Password.Trim() is { Length: > 0 } token) _host.Hub.Secrets.Set(MinerHub.DiscordTokenKey, token);
        var s = new ChatBotSettings
        {
            TelegramEnabled = _tg.IsChecked == true,
            TelegramTargetId = _tgTarget.SelectedValue as string ?? "",
            DiscordEnabled = _dc.IsChecked == true,
            DiscordChannelId = _channel.Text,
            DiscordGuildId = _guild.Text,
            DiscordAppId = config.ChatBot.DiscordAppId,
            LiveStatus = _live.IsChecked == true,
            LiveMinutes = int.TryParse(_minutes.Text.Trim(), out var m) ? m : 5,
            Presence = _presence.IsChecked == true,
            AlertButtons = _alerts.IsChecked == true,
            PrivateCommands = _private.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToList(),
        };
        s.Normalize();
        config.ChatBot = s;
    }
}
