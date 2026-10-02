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

    /// <summary>
    /// 0.8.2: Teile des Tages-/Monatsberichts, die dieses Ziel NICHT bekommt (<see cref="ReportParts"/>); leer = alles.
    /// Mit Miner-Auswahl enthalten die Berichte außerdem nur diese Miner, Zuflüsse dann nie (nicht zuordenbar).
    /// </summary>
    public List<string> ReportExclude { get; set; } = [];

    /// <summary>0.9.1: Meldungen für alle Miner dieser Gruppen (zusätzlich zu <see cref="Miners"/>); neue Mitglieder automatisch.</summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>Auf bestimmte Miner oder Gruppen beschränkt (sonst: alle).</summary>
    public bool HasMinerFilter => Miners.Count > 0 || Groups.Count > 0;

    /// <summary>Gehört dieser Miner zu den Meldungen des Ziels? <paramref name="hostGroups"/> = Gruppen des Miners.</summary>
    public bool CoversHost(string host, IEnumerable<string>? hostGroups) =>
        !HasMinerFilter || Miners.Contains(host, StringComparer.OrdinalIgnoreCase) ||
        (hostGroups ?? []).Any(g => Groups.Contains(g, StringComparer.OrdinalIgnoreCase));

    public bool ReportIncludes(string part) =>
        !ReportExclude.Contains(part, StringComparer.OrdinalIgnoreCase) && !(part == ReportParts.Income && HasMinerFilter);

    public static List<string> DefaultCategories() =>
        Enum.GetNames<NotifyCategory>().Where(c => c != nameof(NotifyCategory.Record)).ToList();

    public bool Wants(NotifyCategory category) =>
        category == NotifyCategory.Other || Categories.Contains(category.ToString(), StringComparer.OrdinalIgnoreCase);

    public bool Accepts(NotifyCategory category, string? host, Func<string, IReadOnlyCollection<string>>? groupsOf = null) =>
        Enabled && Wants(category) && (host is null || CoversHost(host, groupsOf?.Invoke(host)));

    public PushTarget Clone()
    {
        var c = (PushTarget)MemberwiseClone();
        c.Categories = [.. Categories];
        c.Miners = [.. Miners];
        c.ReportExclude = [.. ReportExclude];
        c.Groups = [.. Groups];
        return c;
    }

    /// <summary>Anzeigename: eigener Name oder Dienst.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? Provider : Name;
}

/// <summary>Abwählbare Teile von Tages- und Monatsbericht je Push-Ziel (z. B. für eine Community-Gruppe).</summary>
public static class ReportParts
{
    public const string Costs = "costs";
    public const string Income = "income";
    public const string Tips = "tips";
    public const string BestDiff = "bestDiff";
    public const string Tuning = "tuning";
    public const string Plugs = "plugs";
    public static readonly string[] All = [Costs, Income, Tips, BestDiff, Tuning, Plugs];
}
