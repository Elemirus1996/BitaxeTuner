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
    /// <param name="loc">Sprache der Texte (siehe <see cref="Onboarding.Steps"/>); Standard ist die des Programms.</param>
    public static List<NewFeature> All(bool server, Loc? loc = null)
    {
        var tr = loc ?? Loc.Current;
        return
        [
            new("0.8.0", tr.T("Mehrere Push-Dienste gleichzeitig"),
                tr.T("Z. B. ntfy für dich und Discord für eine Community-Gruppe – je Dienst eigene Meldungen und Miner. Dein bisheriger Dienst wurde übernommen."),
                tr.T("Push-Benachrichtigungen")),
            new("0.8.0", tr.T("Einstellungen mit Inhaltsverzeichnis"),
                server ? tr.T("Oben auf der Einstellungsseite springt eine Leiste direkt zu jedem Abschnitt.")
                       : tr.T("Links in den Einstellungen springt ein Inhaltsverzeichnis zu jedem Abschnitt; Push-Dienste und Smart Plugs stehen jetzt direkt dort."),
                null),
            new("0.8.0", tr.T("SSH-Terminal mit einem Klick"),
                server ? tr.T("In der Desktop-App öffnet „SSH-Terminal“ oben direkt eine Konsole auf dem Server – ohne Passwort.")
                       : tr.T("„SSH-Terminal“ oben öffnet direkt eine Konsole auf deinem Server – ohne Passwort. Einrichtung unter „Betriebsart …“."),
                null),
            new("0.8.0", tr.T("Miner-Log mit Filtern"),
                tr.T("Im Protokoll-Tab eines Miners blendest du Arten von Meldungen ein und aus – Shares, Pool/Stratum, ASIC, Temperatur/Lüfter, System – mit Anzahl je Art."),
                null),
            new("0.8.0", tr.T("Pool-Difficulty"),
                tr.T("Vergleich und Live-Ansicht zeigen, mit welcher Share-Difficulty der Pool den Miner gerade arbeiten lässt."),
                null),
            new("0.8.0", tr.T("Einführung „Erste Schritte“"),
                server ? tr.T("Checkliste für die Einrichtung – jederzeit wieder einblenden unter Einstellungen → Allgemein.")
                       : tr.T("Checkliste für die Einrichtung – jederzeit wieder aufrufen unter Einstellungen → Programm und Tuning."),
                server ? tr.T("Allgemein") : tr.T("Programm und Tuning")),
            new("0.8.1", tr.T("Sicherung einfacher"),
                server ? tr.T("Jede Sicherung lässt sich jetzt mit einem Klick einspielen – oder eine Datei vom PC, USB-Stick oder NAS hochladen, z. B. auf einem neu aufgesetzten Server. Der vorherige Stand bleibt erhalten.")
                       : tr.T("Neuer Abschnitt „Sicherung“: zweiter Ordner (USB, Festplatte, NAS), Ordner öffnen und Sicherung einspielen. Im Modus „Server“ holt die App auf Wunsch täglich eine Sicherung auf den PC (Knopf „Sicherungen“)."),
                tr.T("Sicherung")),
            new("0.8.1", tr.T("AxeOS mit einem Klick"),
                server ? tr.T("Übersicht, Vergleich und Geräteseite verlinken direkt die Weboberfläche des Miners.")
                       : tr.T("In der Browser-Oberfläche verlinken Übersicht, Vergleich und Geräteseite direkt die Weboberfläche des Miners; in der Desktop-App wie bisher über „Weboberfläche“."),
                null),
        ];
    }

    /// <summary>
    /// Neuerungen seit <paramref name="lastSeen"/> bis einschließlich <paramref name="current"/>.
    /// Unbekannte letzte Version (Update von vor 0.8.0): nur die Neuerungen der aktuellen Version.
    /// </summary>
    public static List<NewFeature> Since(string? lastSeen, string current, bool server, Loc? loc = null)
    {
        var cur = Parse(current);
        var from = lastSeen is null ? null : Parse(lastSeen);
        return All(server, loc).Where(f =>
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
