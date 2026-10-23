using System.Globalization;
using System.Security.Cryptography;
using BitaxeTuner.Core.Chat;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.26 Steuern per Chat (Teil 2 der Chat-Bots): nur wenn eingeschaltet (<see cref="Config.ChatBotSettings.ControlEnabled"/>),
/// nur für freigegebene Nutzer (Discord-Rolle, Telegram-Nutzer-ID im privaten Chat) und jede Aktion erst nach Bestätigung
/// durch denselben Nutzer (Knopf, 2 Minuten gültig). Möglich: Neustart, gespeicherte Voreinstellung (nie freie Werte),
/// Wartungsmodus, Motor pausieren/fortsetzen, Sicherung, Wartung erledigt. Alles ins Protokoll mit „ausgelöst von …“.
/// </summary>
public sealed partial class MinerHub
{
    /// <summary>Steuer-Befehle (englischer Name, deutscher Name).</summary>
    public static readonly (string Name, string De)[] ChatControlNames =
    [
        ("restart", "neustart"), ("preset", "voreinstellung"), ("maintenance", "wartung"), ("pause", "pause"), ("resume", "weiter"), ("backup", "sicherung"),
    ];

    public static readonly TimeSpan ChatConfirmWindow = TimeSpan.FromMinutes(2);

    private sealed record PendingChatAction(string Kind, string? Host, string? Arg, double? Hours, string? Reason, ChatActor Actor, DateTime Expires);
    private readonly Dictionary<string, PendingChatAction> _pendingChat = new();

    /// <summary>Server: Motor über den HubService pausieren (Zustand wird dort gespeichert). Ohne: direkt.</summary>
    public Func<bool, Task>? PauseHandler { get; set; }

    public static string ChatControlDescription(string name, Loc? loc = null) => (loc ?? Loc.Current) is var l ? name switch
    {
        "restart" => l.T("Miner neu starten (mit Bestätigung)"),
        "preset" => l.T("Gespeicherte Voreinstellung anwenden: Miner | Name"),
        "maintenance" => l.T("Wartungsmodus: Miner | an/aus | Stunden | Grund"),
        "pause" => l.T("Motor pausieren"),
        "resume" => l.T("Motor fortsetzen"),
        "backup" => l.T("Sicherung jetzt"),
        _ => "",
    } : "";

    public static string? NormalizeControlCommand(string text)
    {
        var word = text.Trim().TrimStart('/').Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
        return ChatControlNames.FirstOrDefault(c => c.Name == word || c.De == word).Name;
    }

    private static string WhoOf(ChatActor a) => $"{a.Platform} {a.Name}";

    /// <summary>
    /// Antwort mit Wissen über den Fragenden: Steuer-Befehle und Bestätigungen; sonst wie <see cref="AnswerChat"/> – bei
    /// /miner für freigegebene Nutzer mit Knöpfen [Neu starten] [Wartung].
    /// </summary>
    public async Task<ChatReply> AnswerChatAsAsync(string text, ChatActor actor, DateTime now)
    {
        var t = text.Trim();
        try
        {
            if (t.StartsWith("!ok ", StringComparison.Ordinal)) return await ConfirmChatActionAsync(t[4..].Trim(), actor, now);
            if (t.StartsWith("!no ", StringComparison.Ordinal)) return CancelChatAction(t[4..].Trim(), actor);
            if (t.StartsWith("!done ", StringComparison.Ordinal)) return RequestChatAction("done", t[6..], actor, now);
            if (NormalizeControlCommand(t) is { } control)
                return RequestChatAction(control, t.Split(' ', 2) is [_, var rest] ? rest : "", actor, now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ChatReply(L.T("Das hat gerade nicht geklappt: {0}", ex.Message));
        }
        var reply = AnswerChat(text, now);
        if (NormalizeChatCommand(t) == "miner" && actor.CanOperate && Config.ChatBot.ControlEnabled
            && FindChatMiner(t.Split(' ', 2) is [_, var name] ? name.Trim() : "") is { } d)
            reply = reply with
            {
                Buttons = [new ChatButton(L.T("Neu starten"), "r:" + d.Title), new ChatButton(d.Config.MaintenanceMode ? L.T("Wartung beenden") : L.T("Wartung …"), (d.Config.MaintenanceMode ? "mo:" : "mm:") + d.Title)],
            };
        return reply;
    }

    private HubDevice? FindChatMiner(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        return Devices.FirstOrDefault(d => string.Equals(d.Title, name, StringComparison.OrdinalIgnoreCase))
               ?? Devices.FirstOrDefault(d => d.Title.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Aktion vormerken und zur Bestätigung zurückfragen.</summary>
    internal ChatReply RequestChatAction(string kind, string args, ChatActor actor, DateTime now)
    {
        if (!Config.ChatBot.ControlEnabled) return new ChatReply(L.T("Steuern per Chat ist ausgeschaltet (BitaxeTuner → Einstellungen → Chat-Bots)."));
        if (!actor.CanOperate)
            return new ChatReply(actor.Platform == "Discord"
                ? L.T("Dafür fehlt die Berechtigung – nur Mitglieder mit der Rolle „{0}“ dürfen steuern.", Config.ChatBot.DiscordAdminRole)
                : L.T("Dafür fehlt die Berechtigung – nur freigegebene Telegram-Nutzer im privaten Chat dürfen steuern."));
        var parts = args.Split('|', StringSplitOptions.TrimEntries);
        HubDevice Miner()
        {
            if (FindChatMiner(parts.ElementAtOrDefault(0) ?? "") is { } d) return d;
            throw new LocalizedException("Keinen Miner „{0}“ gefunden. Bekannt: {1}", parts.ElementAtOrDefault(0) ?? "", string.Join(", ", Devices.Select(x => x.Title)));
        }
        PendingChatAction action;
        string question;
        switch (kind)
        {
            case "restart":
            {
                var d = Miner();
                action = new("restart", d.Host, null, null, null, actor, now + ChatConfirmWindow);
                question = L.T("{0} wirklich neu starten? Watchdog und Offline-Meldung pausieren dabei.", d.Title);
                break;
            }
            case "preset":
            {
                var d = Miner();
                var name = parts.ElementAtOrDefault(1) ?? "";
                var p = d.Config.Presets.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (p is null)
                    return new ChatReply(d.Config.Presets.Count == 0
                        ? L.T("Für {0} sind keine Voreinstellungen gespeichert.", d.Title)
                        : L.T("Voreinstellung „{0}“ nicht gefunden. Gespeichert: {1}", name, string.Join(", ", d.Config.Presets.Select(x => x.Name))));
                if (CheckPreset(d, p) is { } error) return new ChatReply(error);
                var info = d.State.Normalized;
                action = new("preset", d.Host, p.Name, null, null, actor, now + ChatConfirmWindow);
                question = L.T("Voreinstellung „{0}“ auf {1} anwenden? Frequenz {2} → {3} MHz, Spannung {4} → {5} mV.", p.Name, d.Title,
                    info?.FrequencyMhz.ToString(CultureInfo.InvariantCulture) ?? "?", p.FrequencyMhz, info?.CoreVoltageMv.ToString(CultureInfo.InvariantCulture) ?? "?", p.CoreVoltageMv);
                break;
            }
            case "maintenance":
            {
                var d = Miner();
                var on = (parts.ElementAtOrDefault(1) ?? "an").ToLowerInvariant() is not ("aus" or "off" or "0" or "nein" or "no");
                double? hours = double.TryParse((parts.ElementAtOrDefault(2) ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : null;
                if (hours is < 0 or > 24 * 30) return new ChatReply(L.T("Dauer bitte zwischen 0 und 720 Stunden angeben (0 = bis zum Ausschalten)."));
                var reason = parts.ElementAtOrDefault(3) is { Length: > 0 } r ? (r.Length > 100 ? r[..100] : r) : null;
                action = new("maintenance", d.Host, on ? "on" : "off", hours, reason, actor, now + ChatConfirmWindow);
                question = on
                    ? L.T("Wartungsmodus für {0} einschalten{1}? Solange keine Offline-Meldungen und keine Automatik.", d.Title,
                        (hours is > 0 ? " " + L.T("für {0} Stunden", hours.Value.ToString("0.#", L.Culture)) : "") + (reason is null ? "" : " – " + reason))
                    : L.T("Wartungsmodus für {0} beenden?", d.Title);
                break;
            }
            case "pause":
                action = new("pause", null, null, null, null, actor, now + ChatConfirmWindow);
                question = L.T("Motor pausieren? Dann fragt BitaxeTuner keine Miner mehr ab – keine Meldungen, keine Automatik. Die Miner selbst laufen weiter.");
                break;
            case "resume":
                action = new("resume", null, null, null, null, actor, now + ChatConfirmWindow);
                question = L.T("Motor fortsetzen? BitaxeTuner fragt die Miner dann wieder ab.");
                break;
            case "backup":
                action = new("backup", null, null, null, null, actor, now + ChatConfirmWindow);
                question = L.T("Jetzt eine Sicherung an alle eingerichteten Ziele schreiben?");
                break;
            case "done":
            {
                var d = Miner();
                var key = parts.ElementAtOrDefault(1) ?? "";
                if (!UpkeepKeys.Contains(key)) return new ChatReply(L.T("Unbekannte Wartungsaufgabe."));
                action = new("done", d.Host, key, null, null, actor, now + ChatConfirmWindow);
                question = L.T("„{0}“ an {1} als erledigt eintragen?", UpkeepLabel(key), d.Title);
                break;
            }
            default:
                return new ChatReply(L.T("Unbekannter Befehl. /help zeigt alle Befehle."));
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        lock (_pendingChat)
        {
            foreach (var old in _pendingChat.Where(x => x.Value.Expires < now).Select(x => x.Key).ToList()) _pendingChat.Remove(old);
            _pendingChat[token] = action;
        }
        return new ChatReply(question)
        {
            Buttons = [new ChatButton("✔ " + L.T("Ja, ausführen"), "x:" + token), new ChatButton(L.T("Abbrechen"), "n:" + token)],
        };
    }

    private PendingChatAction? TakePending(string token, ChatActor actor, DateTime now)
    {
        lock (_pendingChat)
        {
            if (!_pendingChat.TryGetValue(token, out var a)) return null;
            if (a.Actor.Platform != actor.Platform || a.Actor.Id != actor.Id) return a with { Kind = "foreign" };
            _pendingChat.Remove(token);
            return a.Expires < now ? a with { Kind = "expired" } : a;
        }
    }

    internal ChatReply CancelChatAction(string token, ChatActor actor) =>
        TakePending(token, actor, DateTime.MinValue) is { Kind: not "foreign" } ? new ChatReply(L.T("Abgebrochen – nichts geändert.")) : new ChatReply(L.T("Nichts zum Abbrechen."));

    /// <summary>Bestätigte Aktion ausführen (nur derselbe Nutzer, nur innerhalb von 2 Minuten, Rechte erneut geprüft).</summary>
    internal async Task<ChatReply> ConfirmChatActionAsync(string token, ChatActor actor, DateTime now)
    {
        var a = TakePending(token, actor, now);
        if (a is null) return new ChatReply(L.T("Diese Rückfrage gibt es nicht mehr – bitte den Befehl neu senden."));
        if (a.Kind == "foreign") return new ChatReply(L.T("Bestätigen kann nur, wer gefragt hat."));
        if (a.Kind == "expired") return new ChatReply(L.T("Zu spät bestätigt (nach 2 Minuten) – bitte den Befehl neu senden."));
        if (!Config.ChatBot.ControlEnabled || !actor.CanOperate) return new ChatReply(L.T("Dafür fehlt die Berechtigung."));
        var who = L.T("ausgelöst von {0}", WhoOf(actor));
        var d = a.Host is null ? null : Device(a.Host) ?? throw new LocalizedException("Miner nicht mehr vorhanden.");
        switch (a.Kind)
        {
            case "restart":
                if (d!.IsBenchmarkRunning) return new ChatReply(L.T("Während eines Benchmarks nicht möglich."));
                await d.Connection.RestartAsync();
                d.AddLog(L.T("Neustart ausgelöst ({0}).", who), EventCategories.Connection);
                return new ChatReply(L.T("{0} startet neu.", d.Title));
            case "preset":
            {
                var p = d!.Config.Presets.FirstOrDefault(x => x.Name == a.Arg) ?? throw new LocalizedException("Voreinstellung nicht gefunden.");
                if (CheckPreset(d, p) is { } error) return new ChatReply(error);
                if (d.IsBenchmarkRunning) return new ChatReply(L.T("Während eines Benchmarks nicht möglich."));
                await ApplyChangeAsync(d, p.FrequencyMhz, p.CoreVoltageMv);
                d.AddLog(L.T("Voreinstellung „{0}“ angewendet ({1}).", p.Name, who), EventCategories.Automation);
                return new ChatReply(L.T("Voreinstellung „{0}“ auf {1} angewendet ({2} MHz, {3} mV).", p.Name, d.Title, p.FrequencyMhz, p.CoreVoltageMv));
            }
            case "maintenance":
                SetMaintenanceMode(d!, a.Arg == "on", a.Hours, who + (a.Reason is null ? "" : ": " + a.Reason));
                return new ChatReply(a.Arg == "on" ? L.T("Wartungsmodus für {0} an. {1}", d!.Title, MaintenanceText(d.Config)) : L.T("Wartungsmodus für {0} aus.", d!.Title));
            case "pause" or "resume":
            {
                var paused = a.Kind == "pause";
                if (PauseHandler is { } handler) _ = handler(paused);   // Server: speichert den Zustand selbst
                else SetPaused(paused);
                LogEvent(null, EventCategories.Settings, paused ? L.T("Motor pausiert ({0}).", who) : L.T("Motor läuft wieder ({0}).", who));
                return new ChatReply(paused ? L.T("Motor pausiert.") : L.T("Motor läuft wieder."));
            }
            case "backup":
            {
                LogEvent(null, EventCategories.System, L.T("Sicherung gestartet ({0}).", who));
                var st = await RunBackupAsync(now);
                return new ChatReply(st.LastOk == true ? L.T("Sicherung erstellt.") : L.T("Sicherung mit Fehlern – siehe Status."));
            }
            case "done":
                UpkeepDone(d!, a.Arg!, who);
                return new ChatReply(L.T("„{0}“ an {1} als erledigt eingetragen.", UpkeepLabel(a.Arg!), d!.Title));
            default:
                return new ChatReply(L.T("Unbekannter Befehl. /help zeigt alle Befehle."));
        }
    }

    /// <summary>Knöpfe unter Warnungen für freigegebene Nutzer: offline → [Neu starten], Wartung fällig → [Erledigt].</summary>
    internal IReadOnlyList<ChatButton> ChatAlertControls(string key, HubDevice? d)
    {
        if (!Config.ChatBot.ControlEnabled || d is null) return [];
        if (key.StartsWith("offline:", StringComparison.Ordinal)) return [new ChatButton(L.T("Neu starten"), "r:" + d.Title)];
        if (key.StartsWith("upkeep:", StringComparison.Ordinal) && key.Split(':') is [_, .., var task] && UpkeepKeys.Contains(task))
            return [new ChatButton(L.T("Erledigt"), "u:" + d.Title + "|" + task)];
        return [];
    }

    /// <summary>Knopf-Kennung (ohne „bt:“) in einen Befehl übersetzen – gemeinsam für Discord und Telegram.</summary>
    public static string? ButtonToCommand(string? id) => id switch
    {
        "s" => "/status",
        { } c when c.StartsWith("m:", StringComparison.Ordinal) => "/miner " + c[2..],
        { } c when c.StartsWith("h:", StringComparison.Ordinal) => "/history " + c[2..],
        { } c when c.StartsWith("r:", StringComparison.Ordinal) => "/restart " + c[2..],
        { } c when c.StartsWith("mo:", StringComparison.Ordinal) => "/maintenance " + c[3..] + " | aus",
        { } c when c.StartsWith("mw:", StringComparison.Ordinal) => "/maintenance " + c[3..] + " | an | 2",
        { } c when c.StartsWith("x:", StringComparison.Ordinal) => "!ok " + c[2..],
        { } c when c.StartsWith("n:", StringComparison.Ordinal) => "!no " + c[2..],
        { } c when c.StartsWith("u:", StringComparison.Ordinal) => "!done " + c[2..].Replace("|", " | "),
        _ => null,
    };
}
