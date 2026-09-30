namespace BitaxeTuner.Core.Config;

/// <summary>Bereich einer Push-Meldung – je Ziel einzeln wählbar.</summary>
public enum NotifyCategory
{
    Offline,
    Overheat,
    /// <summary>Blockfund und dokumentierter Zufluss.</summary>
    Finds,
    /// <summary>Watchdog, Automatik, Dauertest, Firmware, Sicherung, Server-Update.</summary>
    Maintenance,
    Record,
    LogAlerts,
    Pool,
    Plugs,
    Health,
    DailyReport,
    MonthlyReport,
    /// <summary>Sonstiges (z. B. Testnachricht) – geht an alle aktiven Ziele.</summary>
    Other,
}

/// <summary>
/// 0.8.0: ein Push-Ziel (Dienst + Zugangsdaten) mit eigener Auswahl, welche Meldungen für welche Miner es bekommt –
/// z. B. ntfy privat für alles und Discord für eine Community-Gruppe nur mit Blockfunden und Tagesbericht.
/// </summary>
public sealed class PushTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>"ntfy", "telegram", "discord", "pushover" oder "webhook".</summary>
    public string Provider { get; set; } = "ntfy";

    public string NtfyServer { get; set; } = "https://ntfy.sh";
    public string NtfyTopic { get; set; } = "";
    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
    public string DiscordWebhookUrl { get; set; } = "";
    public string PushoverUserKey { get; set; } = "";
    public string PushoverAppToken { get; set; } = "";
    public string WebhookUrl { get; set; } = "";

    /// <summary>Gewünschte Bereiche (Namen aus <see cref="NotifyCategory"/>); Standard: alle außer Rekorden.</summary>
    public List<string> Categories { get; set; } = DefaultCategories();

    /// <summary>Nur Meldungen dieser Miner (Hosts); leer = alle. Meldungen ohne Miner-Bezug kommen immer.</summary>
    public List<string> Miners { get; set; } = [];

    public static List<string> DefaultCategories() =>
        Enum.GetNames<NotifyCategory>().Where(c => c != nameof(NotifyCategory.Record)).ToList();

    public bool Wants(NotifyCategory category) =>
        category == NotifyCategory.Other || Categories.Contains(category.ToString(), StringComparer.OrdinalIgnoreCase);

    public bool Accepts(NotifyCategory category, string? host) =>
        Enabled && Wants(category) &&
        (host is null || Miners.Count == 0 || Miners.Contains(host, StringComparer.OrdinalIgnoreCase));

    public PushTarget Clone()
    {
        var c = (PushTarget)MemberwiseClone();
        c.Categories = [.. Categories];
        c.Miners = [.. Miners];
        return c;
    }

    /// <summary>Anzeigename: eigener Name oder Dienst.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? Provider : Name;
}
