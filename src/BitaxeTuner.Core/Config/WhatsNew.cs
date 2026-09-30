using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Config;

/// <summary>Eine Neuerung für „Neu in dieser Version“. Section = Einstellungs-Abschnitt zum Hinspringen (Desktop und Browser gleich benannt).</summary>
public sealed record NewFeature(string Version, string Title, string Text, string? Section);

/// <summary>
/// „Neu in dieser Version“: Wer ein Update installiert, wird einmal gefragt, ob er eine kurze Einführung nur in die
/// neuen Funktionen möchte. Neue Installationen bekommen stattdessen „Erste Schritte“.
/// Bei jeder Neuerung hier einen Eintrag ergänzen (zweisprachig).
/// </summary>
public static class WhatsNew
{
    /// <summary>Alle Einträge, älteste Version zuerst.</summary>
    public static List<NewFeature> All(bool server) =>
    [
        new("0.8.0", L.T("Mehrere Push-Dienste gleichzeitig"),
            L.T("Z. B. ntfy für dich und Discord für eine Community-Gruppe – je Dienst eigene Meldungen und Miner. Dein bisheriger Dienst wurde übernommen."),
            L.T("Push-Benachrichtigungen")),
        new("0.8.0", L.T("Einstellungen mit Inhaltsverzeichnis"),
            server ? L.T("Oben auf der Einstellungsseite springt eine Leiste direkt zu jedem Abschnitt.")
                   : L.T("Links in den Einstellungen springt ein Inhaltsverzeichnis zu jedem Abschnitt; Push-Dienste und Smart Plugs stehen jetzt direkt dort."),
            null),
        new("0.8.0", L.T("SSH-Terminal mit einem Klick"),
            server ? L.T("In der Desktop-App öffnet „SSH-Terminal“ oben direkt eine Konsole auf dem Server – ohne Passwort.")
                   : L.T("„SSH-Terminal“ oben öffnet direkt eine Konsole auf deinem Server – ohne Passwort. Einrichtung unter „Betriebsart …“."),
            null),
        new("0.8.0", L.T("Miner-Log mit Filtern"),
            L.T("Im Protokoll-Tab eines Miners blendest du Arten von Meldungen ein und aus – Shares, Pool/Stratum, ASIC, Temperatur/Lüfter, System – mit Anzahl je Art."),
            null),
        new("0.8.0", L.T("Pool-Difficulty"),
            L.T("Vergleich und Live-Ansicht zeigen, mit welcher Share-Difficulty der Pool den Miner gerade arbeiten lässt."),
            null),
        new("0.8.0", L.T("Einführung „Erste Schritte“"),
            server ? L.T("Checkliste für die Einrichtung – jederzeit wieder einblenden unter Einstellungen → Allgemein.")
                   : L.T("Checkliste für die Einrichtung – jederzeit wieder aufrufen unter Einstellungen → Programm und Tuning."),
            server ? L.T("Allgemein") : L.T("Programm und Tuning")),
    ];

    /// <summary>
    /// Neuerungen seit <paramref name="lastSeen"/> bis einschließlich <paramref name="current"/>.
    /// Unbekannte letzte Version (Update von vor 0.8.0): nur die Neuerungen der aktuellen Version.
    /// </summary>
    public static List<NewFeature> Since(string? lastSeen, string current, bool server)
    {
        var cur = Parse(current);
        var from = lastSeen is null ? null : Parse(lastSeen);
        return All(server).Where(f =>
        {
            var v = Parse(f.Version);
            return v <= cur && (from is null ? v == cur : v > from);
        }).ToList();
    }

    /// <summary>
    /// Nach dem Start: neue Installation → Version merken (sie bekommt „Erste Schritte“); Update mit Neuerungen → true
    /// (fragen). Ändert <see cref="AppConfig.LastSeenVersion"/> nur bei neuen Installationen; sonst erst nach der Antwort.
    /// </summary>
    public static bool ShouldAsk(AppConfig c, string current, bool server)
    {
        if (c.LastSeenVersion is null && c.Devices.Count == 0)
        {
            c.LastSeenVersion = current; // neue Installation
            return false;
        }
        return c.LastSeenVersion != current && Since(c.LastSeenVersion, current, server).Count > 0;
    }

    /// <summary>Gesehen bzw. abgelehnt – bis zum nächsten Update nicht mehr fragen.</summary>
    public static void MarkSeen(AppConfig c, string current) => c.LastSeenVersion = current;

    private static Version Parse(string v) =>
        Version.TryParse(v.TrimStart('v').Split('-', '+')[0], out var p) ? new Version(p.Major, p.Minor, Math.Max(0, p.Build)) : new Version(0, 0, 0);
}
