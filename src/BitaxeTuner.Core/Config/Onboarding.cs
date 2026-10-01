using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// Ein Schritt der Einführung „Erste Schritte“. Section = Überschrift des Einstellungs-Abschnitts, zu dem der Schritt
/// springt (in Desktop und Browser gleich benannt); Action = besondere Aktion statt Abschnitt (z. B. "mode").
/// </summary>
public sealed record OnboardingStep(string Id, string Title, string Text, bool Done, string? Section, string? Action = null);

/// <summary>
/// Einführung beim ersten Start: kurze Checkliste, die selbst erkennt, was schon erledigt ist.
/// Erscheint nur bei neuen Installationen (noch kein Miner) und bis zum Ausblenden.
/// </summary>
public static class Onboarding
{
    /// <summary>
    /// Anzeigen? Eine neue Installation (noch kein Miner) startet die Einführung; sie bleibt dann bis zum Ausblenden –
    /// auch wenn inzwischen Miner eingetragen sind. Bestehende Installationen bekommen sie nur auf Wunsch.
    /// </summary>
    public static bool ShouldShow(AppConfig c)
    {
        if (!c.OnboardingDone && c.Devices.Count == 0) c.OnboardingStarted = true;
        return !c.OnboardingDone && c.OnboardingStarted;
    }

    /// <summary>Ausblenden bzw. wieder einblenden.</summary>
    public static void SetVisible(AppConfig c, bool show)
    {
        c.OnboardingDone = !show;
        if (show) c.OnboardingStarted = true;
    }

    /// <param name="server">Schritte für den Server (Sicherung) statt für die Desktop-App (24/7-Betrieb).</param>
    /// <param name="loc">Sprache der Texte; Standard ist die des Programms. Der Server liefert Deutsch (= Schlüssel),
    /// jeder Browser übersetzt selbst – der Status geht an alle Browser gleichzeitig.</param>
    public static List<OnboardingStep> Steps(AppConfig c, bool server, Loc? loc = null)
    {
        var tr = loc ?? Loc.Current;
        var steps = new List<OnboardingStep>
        {
            new("miners", tr.T("Miner hinzufügen"),
                tr.T("Adresse eintragen oder „Im Netz suchen“ – das passende Geräteprofil mit sicheren Grenzen wird erkannt."),
                c.Devices.Count > 0, server ? tr.T("Geräte") : tr.T("Miner")),
            new("price", tr.T("Strompreis eintragen"),
                tr.T("Arbeitspreis deines Vertrags (brutto oder netto) – für Stromkosten, Tagesbericht und Effizienz-Ratgeber."),
                c.ElectricityCtPerKwh != 30 || c.ElectricityPriceIsNet || c.PriceSource.Source != "none",
                server ? tr.T("Allgemein") : tr.T("Strom und Abfrage")),
            new("push", tr.T("Push-Dienst einrichten"),
                tr.T("Meldungen aufs Handy bei Ausfall, Überhitzung oder Blockfund – ntfy, Telegram, Discord, Pushover oder Webhook, auch mehrere."),
                c.Notifications.EffectiveTargets().Any(t => t.Enabled), tr.T("Push-Benachrichtigungen")),
        };
        steps.Add(server
            ? new("backup", tr.T("Sicherung einrichten"),
                tr.T("Tägliche Sicherung zusätzlich auf USB-Stick oder Netzlaufwerk – falls die SD-Karte ausfällt."),
                c.Backup.Folder.Enabled || c.Backup.Smb.Enabled, tr.T("Sicherung"))
            : new("backup", tr.T("Sicherung einrichten"),
                tr.T("Gesichert wird täglich im Datenordner – zusätzlich auf USB-Stick, zweite Festplatte oder NAS, falls der PC ausfällt."),
                c.Backup.Folder.Enabled || c.Backup.Smb.Enabled, tr.T("Sicherung")));
        if (!server)
            steps.Add(new("server", tr.T("Optional: 24/7-Server"),
                tr.T("Soll rund um die Uhr überwacht werden, ohne dass der PC läuft? Raspberry Pi, zweiter PC oder Docker."),
                c.Server.Enabled, null, "mode"));
        return steps;
    }
}
