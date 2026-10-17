using System.Globalization;
using BitaxeTuner.Core.Chat;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.I18n;
using SixLabors.ImageSharp;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.24 Chat-Bots: Antworten auf Befehle (nur lesend – nie Frequenz, Spannung, Lüfter, keine IP-Adressen, Wallets oder
/// Tokens) und Betrieb der Bots für Telegram und Discord. Die Bots laufen außerhalb des Hub-Kontexts und holen ihre
/// Antworten über <see cref="InvokeAsync{T}(Func{T})"/>.
/// </summary>
public sealed partial class MinerHub
{
    public const string DiscordTokenKey = "ChatBot.DiscordToken";

    /// <summary>Befehle (englischer Name, deutscher Name) – Telegram und Discord.</summary>
    public static readonly (string Name, string De)[] ChatCommandNames =
    [
        ("status", "status"), ("miner", "miner"), ("history", "verlauf"), ("records", "rekorde"), ("pool", "pool"),
        ("power", "strom"), ("system", "system"), ("bitcoin", "bitcoin"), ("help", "hilfe"),
    ];

    public static string ChatCommandDescription(string name, Loc? loc = null) => (loc ?? Loc.Current) is var l ? name switch
    {
        "status" => l.T("Überblick: Hashrate, Leistung, Miner online, Warnungen"),
        "miner" => l.T("Werte eines Miners (Name angeben)"),
        "history" => l.T("Verlauf als Diagramm (optional Miner und 24h/7d)"),
        "records" => l.T("Best-Diff-Rekorde"),
        "pool" => l.T("Pool-Konten: Guthaben und Auszahlungen"),
        "power" => l.T("Strompreis, günstige Stunden, PV-Leistung"),
        "system" => l.T("Systemzustand des Servers"),
        "bitcoin" => l.T("Bitcoin-Netz: Blockhöhe, Gebühren, Halving"),
        _ => l.T("Alle Befehle"),
    } : "";

    /// <summary>Befehl auf den englischen Namen abbilden (auch deutsche Namen, „/status@MeinBot“).</summary>
    public static string? NormalizeChatCommand(string text)
    {
        var word = text.Trim().TrimStart('/').Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
        return ChatCommandNames.FirstOrDefault(c => c.Name == word || c.De == word).Name;
    }

    /// <summary>Antwort auf einen Befehl (im Hub-Kontext aufrufen).</summary>
    public ChatReply AnswerChat(string text, DateTime now)
    {
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var args = parts.Length > 1 ? parts[1].Trim() : "";
        var cmd = NormalizeChatCommand(text);
        try
        {
            return cmd switch
            {
                "status" => new ChatReply(ChatStatusText(now), ScenePng(DisplayScene.Overview, now)),
                "miner" => ChatMiner(args, now),
                "history" => ChatHistory(args, now),
                "records" => new ChatReply(ChatRecordsText()),
                "pool" => new ChatReply(ChatPoolText(), ScenePng(DisplayScene.PoolAccount, now)),
                "power" => new ChatReply(ChatPowerText(now), ScenePng(DisplayScene.Power, now)),
                "system" => new ChatReply(ChatSystemText(now), ScenePng(DisplayScene.System, now)),
                "bitcoin" => new ChatReply(L.T("Bitcoin-Netz"), ScenePng(DisplayScene.Bitcoin, now)),
                "help" => new ChatReply(ChatHelpText()),
                _ => new ChatReply(L.T("Unbekannter Befehl. /help zeigt alle Befehle.")),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ChatReply(L.T("Das hat gerade nicht geklappt: {0}", ex.Message));
        }
    }

    private byte[] ScenePng(DisplayScene scene, DateTime now) => Png(PreviewScene(scene, now));

    private static byte[] Png(DisplayModel model)
    {
        using var img = StatusRenderer.RenderImage(model, "spectra73");
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    public string ChatStatusText(DateTime now)
    {
        var m = BuildDisplayModel(now, null);
        var c = L.Culture;
        var lines = new List<string>
        {
            L.T("{0}/{1} Miner online · {2} · {3} W", m.Online, m.Count, StatusRenderer.FormatHash(m.TotalGh), m.TotalW.ToString("0", c)) +
            (m.EfficiencyJth is { } j ? " · " + L.T("{0} J/TH", j.ToString("0.0", c)) : ""),
        };
        if (Profit(now)?.Window("24h") is { Net: { } net } w)
            lines.Add(L.T("Ertrag 24 h: {0} {1} − Strom {2} {1} = {3} {1}", w.Income?.ToString("N2", c) ?? "–", Currencies.Of(Config).Symbol, w.Cost.ToString("N2", c), net.ToString("+0.00;-0.00", c)));
        foreach (var a in m.Alerts.Take(5)) lines.Add("⚠ " + a);
        lines.Add(L.T("Stand {0:t}", now));
        return string.Join("\n", lines);
    }

    private ChatReply ChatMiner(string name, DateTime now)
    {
        var m = BuildDisplayModel(now, null);
        if (name.Length == 0)
            return new ChatReply(L.T("Welcher Miner? Zum Beispiel: /miner {0}", m.Miners.FirstOrDefault()?.Name ?? "Gamma") + "\n" +
                                 string.Join(", ", m.Miners.Select(x => x.Name)));
        var hit = m.Miners.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                  ?? m.Miners.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (hit is null) return new ChatReply(L.T("Keinen Miner „{0}“ gefunden. Bekannt: {1}", name, string.Join(", ", m.Miners.Select(x => x.Name))));
        var c = L.Culture;
        var lines = new List<string>
        {
            hit.Name + " – " + (hit.Maintenance ? L.T("Wartung") : hit.Online ? L.T("online") : L.T("offline")),
            L.T("Hashrate {0}", hit.HashGh is { } gh ? StatusRenderer.FormatHash(gh) : "–") +
            " · " + L.T("ASIC {0} °C", hit.ChipTemp?.ToString("0.0", c) ?? "–") + " · " + L.T("VR {0} °C", hit.VrTemp?.ToString("0.0", c) ?? "–") +
            (hit.FanPercent is { } f ? " · " + L.T("Lüfter {0} %", f) : ""),
        };
        var dev = Devices.FirstOrDefault(d => d.Title == hit.Name);
        if (dev is not null && BestDiffs.Where(b => string.Equals(b.Host, dev.Host, StringComparison.OrdinalIgnoreCase)).MaxBy(b => b.Value) is { } best)
            lines.Add(L.T("Best Diff {0} ({1:d})", best.Raw, best.AchievedAt));
        if (hit.Error is { Length: > 0 } err) lines.Add("⚠ " + err);
        return new ChatReply(string.Join("\n", lines));
    }

    private ChatReply ChatHistory(string args, DateTime now)
    {
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var week = words.RemoveAll(w => w.Equals("7d", StringComparison.OrdinalIgnoreCase) || w.Equals("7t", StringComparison.OrdinalIgnoreCase)) > 0;
        words.RemoveAll(w => w.Equals("24h", StringComparison.OrdinalIgnoreCase));
        var name = string.Join(' ', words);
        var model = PreviewScene(DisplayScene.Chart, now);
        string title = L.T("Verlauf aller Miner");
        if (History is not null && (name.Length > 0 || week))
        {
            var dev = name.Length == 0 ? null : Devices.FirstOrDefault(d => d.Title.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (name.Length > 0 && dev is null) return new ChatReply(L.T("Keinen Miner „{0}“ gefunden.", name));
            var host = dev?.Host ?? Monitoring.HistoryStore.AggregateHost;
            var pts = History.Query(host, now.AddDays(week ? -7 : -1), now, 300);
            model = model with { ChartKind = "hashrate", Chart = pts.Select(p => new DisplayPoint(p.Time, p.HashRateGh, p.Temp)).ToList() };
            if (dev is not null) title = L.T("Verlauf von {0}", dev.Title);
        }
        return new ChatReply(title + (week ? " · " + L.T("7 Tage") : " · " + L.T("24 Stunden")), Png(model));
    }

    private string ChatRecordsText()
    {
        var names = Devices.ToDictionary(d => d.Host, d => d.Title, StringComparer.OrdinalIgnoreCase);
        var top = BestDiffs.OrderByDescending(b => b.Value).Take(8).ToList();
        if (top.Count == 0) return L.T("Noch keine Rekorde.");
        return L.T("Best-Diff-Rekorde") + "\n" + string.Join("\n", top.Select(b =>
            $"• {(names.TryGetValue(b.Host, out var n) ? n : "Miner")} ({b.Coin}): {b.Raw} – {b.AchievedAt.ToString("d", L.Culture)}"));
    }

    private string ChatPoolText()
    {
        var lines = new List<string> { L.T("Pool-Konten") };
        if (Config.PoolAccount.Enabled)
            foreach (var c in PoolAccount.Coins.Where(c => c.Confirmed + c.Unconfirmed > 0))
                lines.Add($"• Mining-Dutch {c.Symbol}: {(c.Confirmed + c.Unconfirmed).ToString("0.########", L.Culture)}");
        foreach (var x in ExtraPoolSummaries())
            lines.Add($"• {x.Name}: " + (x.Balance is { } b ? $"{b.ToString("0.########", L.Culture)} {x.Coin}" : "–") +
                      (x.LastPayoutUtc is { } p ? " · " + L.T("letzte Auszahlung {0:d}", p.ToLocalTime()) : ""));
        if (lines.Count == 1) lines.Add(L.T("Kein Pool-Konto eingerichtet."));
        return string.Join("\n", lines);
    }

    private string ChatPowerText(DateTime now)
    {
        var c = L.Culture;
        var lines = new List<string>();
        var ct = Plugs.EnergyCost.CurrentCt(Config, Prices.PriceAt(DateTime.UtcNow));
        lines.Add(L.T("Strompreis jetzt: {0} {1}", ct.ToString("0.0", c), Currencies.Of(Config).CentPerKwh));
        if (SolarPowerW(now) is { } pv) lines.Add(L.T("PV-Leistung: {0} W", pv.ToString("0", c)));
        return string.Join("\n", lines);
    }

    private string ChatSystemText(DateTime now)
    {
        var checks = SystemChecks(now);
        var problems = checks.Where(x => x.Problem).ToList();
        return problems.Count == 0
            ? L.T("Systemzustand: alles in Ordnung.")
            : L.T("Systemzustand: {0} Punkt(e) brauchen Aufmerksamkeit", problems.Count) + "\n" +
              string.Join("\n", problems.Select(p => $"⚠ {p.Label}: {p.Value}"));
    }

    private static string ChatHelpText() =>
        L.T("BitaxeTuner – Befehle (nur lesen):") + "\n" +
        string.Join("\n", ChatCommandNames.Select(c => $"/{c.Name}" + (c.De != c.Name ? $" (/{c.De})" : "") + " – " + ChatCommandDescription(c.Name)));

    // ---------- Betrieb ----------

    private TelegramBot? _telegramBot;
    private DiscordBot? _discordBot;
    private string _chatBotKey = "";

    /// <summary>Für Tests: eigener HTTP-Handler für Telegram/Discord.</summary>
    internal HttpMessageHandler? ChatBotHandler { get; set; }

    /// <summary>Zustand der Bots für die Oberfläche.</summary>
    public (string? Telegram, string? Discord) ChatBotStatus => (_telegramBot?.Status, _discordBot?.Status);

    /// <summary>Bots nach den Einstellungen starten oder stoppen (aus dem Abfragetakt; nur bei Änderung).</summary>
    private void TickChatBots(DateTime now)
    {
        var s = Config.ChatBot;
        var tg = Config.Notifications.EffectiveTargets().FirstOrDefault(t => t.Id == s.TelegramTargetId && t.Provider == "telegram");
        var discordToken = s.DiscordEnabled ? Secrets.Get(DiscordTokenKey) : null;
        var key = string.Join("|", s.TelegramEnabled && tg is not null ? tg.TelegramBotToken + tg.TelegramChatId : "", discordToken ?? "",
            s.DiscordChannelId, s.DiscordGuildId, s.LiveStatus, s.LiveMinutes, Loc.Current.Language);
        if (key == _chatBotKey) return;
        _chatBotKey = key;
        StopChatBots();
        if (!Options.OnlineChecks && ChatBotHandler is null) return;
        if (s.TelegramEnabled && tg is not null && tg.TelegramBotToken.Length > 0 && tg.TelegramChatId.Length > 0)
        {
            _telegramBot = new TelegramBot(tg.TelegramBotToken.Trim(), tg.TelegramChatId.Trim(), ChatBotHandler, ChatAnswerAsync, ChatLiveTextAsync,
                s.LiveStatus ? TimeSpan.FromMinutes(s.LiveMinutes) : null, Path.Combine(DataDirectory, "chatbot-telegram.json"));
            _telegramBot.Start();
        }
        if (s.DiscordEnabled && discordToken is { Length: > 0 } && s.DiscordChannelId.Length > 0)
        {
            _discordBot = new DiscordBot(discordToken.Trim(), s.DiscordChannelId, ChatBotHandler, ChatAnswerAsync, ChatLiveTextAsync,
                s.LiveStatus ? TimeSpan.FromMinutes(s.LiveMinutes) : null, Path.Combine(DataDirectory, "chatbot-discord.json"));
            _discordBot.Start();
        }
    }

    private void StopChatBots()
    {
        _telegramBot?.Dispose();
        _discordBot?.Dispose();
        _telegramBot = null;
        _discordBot = null;
    }

    private Task<ChatReply> ChatAnswerAsync(string text) => InvokeAsync(() => AnswerChat(text, DateTime.Now));

    private Task<string> ChatLiveTextAsync() => InvokeAsync(() => "📊 BitaxeTuner\n" + ChatStatusText(DateTime.Now));

    /// <summary>Discord einrichten: Token prüfen, Anwendung ermitteln, Slash-Befehle (mit Übersetzungen) eintragen, Name/Bild setzen.</summary>
    public async Task<DiscordSetupResult> SetupDiscordAsync(string? newToken, string? guildId, byte[]? avatarPng, CancellationToken ct = default)
    {
        var token = string.IsNullOrWhiteSpace(newToken) ? Secrets.Get(DiscordTokenKey) : newToken.Trim();
        if (string.IsNullOrWhiteSpace(token)) throw new LocalizedException("Bitte zuerst das Bot-Token aus dem Discord Developer Portal einfügen.");
        var result = await DiscordBot.SetupAsync(token, guildId, avatarPng, ChatBotHandler, ct);
        if (!string.IsNullOrWhiteSpace(newToken)) Secrets.Set(DiscordTokenKey, token);
        await InvokeAsync(() =>
        {
            Config.ChatBot.DiscordAppId = result.AppId;
            if (!string.IsNullOrWhiteSpace(guildId)) Config.ChatBot.DiscordGuildId = guildId.Trim();
            Config.ChatBot.Normalize();
            Config.Save();
            _chatBotKey = "";   // neu starten
            return true;
        });
        return result;
    }
}
