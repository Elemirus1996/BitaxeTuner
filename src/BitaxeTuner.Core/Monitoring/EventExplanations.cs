using System.Text.RegularExpressions;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>Erklärung zu einem Protokolleintrag: was er bedeutet und was man tun kann.</summary>
public sealed record EventExplanation(string Id, string Title, string Meaning, string Action);

/// <summary>
/// Erklärungen für das Protokoll. Zugeordnet wird über die Textvorlage aus dem Code (deutscher Schlüssel und – über die
/// Übersetzungstabelle – die englische Fassung), nicht über den fertigen Text; Platzhalter passen auf alles.
/// Ohne passende Vorlage gilt die allgemeine Erklärung der Kategorie.
/// </summary>
public static class EventExplanations
{
    private sealed record Entry(string Id, string Category, string Template, Func<EventExplanation> Make);

    private static EventExplanation E(string id, string title, string meaning, string action) => new(id, title, meaning, action);

    // Reihenfolge zählt: Genauere Vorlagen vor allgemeinen (z. B. Lüfter „steht“ vor „Lüfter K… %“)
    private static readonly Entry[] Entries =
    [
        // ---------- Automatik / Watchdog ----------
        new("auto-limit", EventCategories.Automation, "„{0}“ liegt außerhalb der Grenzen für {1} – wird nicht gesetzt.", () => E("auto-limit",
            L.T("Voreinstellung außerhalb der Profilgrenzen"),
            L.T("Eine Automatik-Regel wollte eine Voreinstellung setzen, deren Frequenz oder Spannung außerhalb der Grenzen des Geräteprofils liegt. Zum Schutz des Miners wurde sie nicht gesetzt. Steht dort „Generisch / unbekannt“, war das Gerät in dem Moment noch nicht erkannt (betraf Versionen vor 0.9.1 direkt nach dem Start)."),
            L.T("Prüfe das Profil des Miners (Gerät → Live → Profil) und die Voreinstellung (Gerät → Automatik). Liegt sie wirklich außerhalb, passe sie an oder wähle das richtige Profil."))),
        new("preset-limit", EventCategories.Automation, "{0} liegt außerhalb der Grenzen für {1}.", () => E("preset-limit",
            L.T("Voreinstellung außerhalb der Profilgrenzen"),
            L.T("Die Voreinstellung passt nicht zu den Grenzen des Geräteprofils und wird deshalb nicht verwendet."),
            L.T("Voreinstellung anpassen oder das passende Geräteprofil wählen."))),
        new("auto-failed", EventCategories.Automation, "Automatik fehlgeschlagen ({0}): {1}", () => E("auto-failed",
            L.T("Automatik konnte nicht ausgeführt werden"),
            L.T("Eine freigegebene Regel wollte eine Einstellung ändern, aber der Miner hat nicht oder mit einem Fehler geantwortet. Es wurde nichts geändert."),
            L.T("Erreichbarkeit des Miners prüfen (WLAN, Strom). Die Regel versucht es beim nächsten Durchlauf erneut."))),
        new("watchdog", EventCategories.Automation, "Watchdog: {0} min ohne Hashrate – {1}", () => E("watchdog",
            L.T("Watchdog-Neustart"),
            L.T("Der Miner hat die eingestellte Zeit lang keine Hashrate geliefert, obwohl er erreichbar war (z. B. hängender ASIC oder Pool-Problem). Der Watchdog hat ihn deshalb neu gestartet."),
            L.T("Kommt das öfter vor: Pool-Verbindung, Stromversorgung und Kühlung prüfen; eventuell ist die Einstellung zu knapp – ein Dauertest zeigt, ob sie stabil ist."))),
        new("auto-run", EventCategories.Automation, "Automatik: {0}", () => E("auto-run",
            L.T("Automatik hat gehandelt"),
            L.T("Eine von dir freigegebene Regel (Temperaturschutz, Zeitplan oder Strompreis) hat eine Einstellung geändert. Die genaue Änderung steht auch unter „Frequenz/Spannung“."),
            L.T("Nichts zu tun, wenn das so gewollt ist. Regeln änderst du unter Gerät → Automatik; jede Änderung braucht eine neue Freigabe."))),
        new("rule-approved", EventCategories.Automation, "{0} freigegeben.", () => E("rule-approved",
            L.T("Regel freigegeben"),
            L.T("Du hast eine Automatik-Regel ausdrücklich freigegeben. Ab jetzt darf sie innerhalb der Profilgrenzen handeln."),
            L.T("Nichts zu tun. Jede spätere Änderung an der Regel hebt die Freigabe wieder auf."))),

        // ---------- Verbindung ----------
        new("offline", EventCategories.Connection, "offline: {0}", () => E("offline",
            L.T("Miner nicht erreichbar"),
            L.T("Der Miner hat dreimal hintereinander nicht geantwortet. Häufige Ursachen: WLAN-Aussetzer, Stromausfall oder Netzteil, Neustart von Hand, geänderte IP-Adresse (DHCP) oder ein hängender Miner."),
            L.T("Strom und WLAN prüfen, AxeOS im Browser öffnen. Feste IP-Adresse im Router vergeben hilft gegen wechselnde Adressen. „wieder online“ folgt automatisch."))),
        new("online", EventCategories.Connection, "wieder online", () => E("online",
            L.T("Miner wieder erreichbar"),
            L.T("Der Miner antwortet wieder. Die Dauer des Ausfalls ergibt sich aus dem Abstand zur Meldung „offline“."),
            L.T("Nichts zu tun. Häufen sich Ausfälle, lohnt ein Blick auf WLAN-Empfang und Stromversorgung."))),
        new("restart", EventCategories.Connection, "Neustart ausgelöst (Browser).", () => E("restart",
            L.T("Neustart von Hand"),
            L.T("Der Miner wurde über die Oberfläche neu gestartet. Watchdog und Offline-Meldung pausieren dabei kurz."),
            L.T("Nichts zu tun."))),

        // ---------- Lüfter ----------
        new("fan-stalled", EventCategories.Fans, "Lüfter K{0} ({1}) steht!", () => E("fan-stalled",
            L.T("Lüfter steht"),
            L.T("Der Pico-Lüfter meldet keine Drehzahl, obwohl er laufen soll. Ohne Kühlung steigen VR- und Chip-Temperatur schnell."),
            L.T("Sofort prüfen: Stecker, Kabel, blockierter Lüfter, defekter Lüfter. Bis dahin Miner beobachten oder ausschalten."))),
        new("fan-back", EventCategories.Fans, "Lüfter K{0} ({1}) läuft wieder: {2} %", () => E("fan-back",
            L.T("Lüfter läuft wieder"),
            L.T("Der zuvor stehende Lüfter meldet wieder eine Drehzahl."),
            L.T("Prüfen, warum er stand (Kontakt, Verschmutzung), damit es nicht wieder passiert."))),
        new("axeos-fan", EventCategories.Fans, "AxeOS-Lüfter ({0}): {1} → {2}", () => E("axeos-fan",
            L.T("Lüfter des Miners geändert"),
            L.T("Der Lüfter des Miners (in AxeOS) wurde auf Automatik mit Zieltemperatur oder einen festen Wert gestellt – nach deiner Bestätigung."),
            L.T("Nichts zu tun. Im manuellen Modus reagiert der Lüfter nicht mehr auf die Temperatur – Temperaturen im Blick behalten."))),
        new("fan-level", EventCategories.Fans, "Lüfter K{0} ({1}): {2} % – {3}", () => E("fan-level",
            L.T("Lüfterregelung"),
            L.T("Die automatische Lüftersteuerung (Pico) hat die Drehzahl deutlich verändert, weil sich die Temperatur geändert hat. Protokolliert werden nur Sprünge ab 15 %."),
            L.T("Nichts zu tun. Läuft ein Lüfter dauerhaft auf 100 %, Kühlung oder Kurve unter „Lüfter & Anzeige“ prüfen."))),

        // ---------- Benchmark ----------
        new("bench-hash", EventCategories.Benchmark, "Hashrate nur {0:P1} der erwarteten (Soll ≥ {1:P0})", () => E("bench-hash",
            L.T("Einstellung instabil: zu wenig Hashrate"),
            L.T("Bei dieser Frequenz/Spannung lieferte der Chip deutlich weniger Hashrate als erwartet – ein Zeichen, dass er nicht mehr sauber rechnet (meist zu wenig Spannung)."),
            L.T("Nichts zu tun – der Benchmark verwirft diese Einstellung und probiert die nächste."))),
        new("bench-err", EventCategories.Benchmark, "Fehlerrate {0:F2} % > {1:F2} %", () => E("bench-err",
            L.T("Einstellung instabil: zu viele Fehler"),
            L.T("Der ASIC meldete zu viele fehlerhafte Ergebnisse – die Einstellung ist zu knapp."),
            L.T("Nichts zu tun – die Einstellung gilt als instabil und wird nicht verwendet."))),
        new("bench-chip", EventCategories.Benchmark, "Chiptemperatur {0:F1} °C > {1:F0} °C", () => E("bench-chip",
            L.T("Grenze überschritten: Chiptemperatur"),
            L.T("Der Chip wurde heißer als im Benchmark erlaubt. Der Test wird sofort beendet und die Einstellung wiederhergestellt."),
            L.T("Kühlung verbessern (Lüfter, Kühlkörper, Wärmeleitpaste, Raumtemperatur) oder kleineren Suchbereich wählen."))),
        new("bench-vr", EventCategories.Benchmark, "VR-Temperatur {0:F1} °C > {1:F0} °C", () => E("bench-vr",
            L.T("Grenze überschritten: Spannungswandler (VR)"),
            L.T("Der Spannungswandler wurde zu heiß. Er begrenzt bei vielen Minern die mögliche Leistung."),
            L.T("VR-Kühlkörper und Luftstrom verbessern, z. B. mit einem Pico-Lüfter; sonst Suchbereich verkleinern."))),
        new("bench-power", EventCategories.Benchmark, "Leistung {0:F1} W > {1:F0} W", () => E("bench-power",
            L.T("Grenze überschritten: Leistung"),
            L.T("Die Leistungsaufnahme lag über der Grenze des Profils bzw. der Benchmark-Einstellung."),
            L.T("Netzteil prüfen; höhere Einstellungen nur mit ausreichend starkem Netzteil."))),
        new("bench-vin-low", EventCategories.Benchmark, "Eingangsspannung {0:F0} mV < {1:F0} mV", () => E("bench-vin-low",
            L.T("Eingangsspannung zu niedrig"),
            L.T("Die Versorgungsspannung des Miners ist unter Last eingebrochen – meist ist das Netzteil zu schwach oder das Kabel zu dünn/lang."),
            L.T("Stärkeres Netzteil oder kürzeres, dickeres Kabel verwenden."))),
        new("bench-overheat", EventCategories.Benchmark, "Gerät meldet Überhitzungsschutz", () => E("bench-overheat",
            L.T("Überhitzungsschutz von AxeOS"),
            L.T("Der Miner hat seinen eigenen Überhitzungsschutz ausgelöst und sich gedrosselt."),
            L.T("Kühlung prüfen; Einstellung wird verworfen."))),
        new("bench-user", EventCategories.Benchmark, "Vom Benutzer abgebrochen.", () => E("bench-user",
            L.T("Benchmark abgebrochen"),
            L.T("Der Benchmark wurde von Hand gestoppt. Die vorherige bzw. beste Einstellung wird wiederhergestellt; bereits gemessene Ergebnisse bleiben erhalten."),
            L.T("Später mit „Fortsetzen“ weitermachen."))),
        new("bench-noreturn", EventCategories.Benchmark, "Gerät ist nach dem Neustart nicht wieder erreichbar.", () => E("bench-noreturn",
            L.T("Miner nach Neustart nicht erreichbar"),
            L.T("Nach einer Änderung kam der Miner nicht zurück. Der Benchmark wird beendet."),
            L.T("Miner prüfen (Strom, WLAN). Startet er nicht sauber, die letzte Einstellung in AxeOS zurücksetzen."))),
        new("bench-restore-failed", EventCategories.Benchmark, "⚠ Einstellungen konnten NICHT wiederhergestellt werden – bitte manuell im AxeOS-Webinterface prüfen!", () => E("bench-restore-failed",
            L.T("Wiederherstellen fehlgeschlagen"),
            L.T("Am Ende des Benchmarks konnten die Einstellungen nicht zurückgeschrieben werden. Der Miner läuft eventuell noch mit einer Test-Einstellung."),
            L.T("AxeOS im Browser öffnen und Frequenz/Spannung prüfen; ggf. unter Gerät → Sicherungen wiederherstellen."))),
        new("bench-few", EventCategories.Benchmark, "Zu wenige Messwerte ({0} von mindestens {1}).", () => E("bench-few",
            L.T("Zu wenige Messwerte"),
            L.T("In der Messzeit kamen nicht genug Werte vom Miner (z. B. WLAN-Aussetzer). Die Einstellung ist dadurch nicht bewertbar."),
            L.T("WLAN-Empfang prüfen oder längere Messdauer wählen."))),
        new("bench-step", EventCategories.Benchmark, "Schritt {0}: {1} MHz / {2} mV", () => E("bench-step",
            L.T("Benchmark-Schritt"),
            L.T("Der Benchmark setzt die nächste Kombination aus Frequenz und Spannung und misst sie anschließend."),
            L.T("Nichts zu tun."))),

        // ---------- Dauertest ----------
        new("soak-start", EventCategories.Soak, "Dauertest gestartet: {0} MHz / {1} mV für {2} h", () => E("soak-start",
            L.T("Dauertest gestartet"),
            L.T("Die aktuelle Einstellung wird über Stunden beobachtet (Hashrate, Fehler, Temperaturen, Ausfälle). Am Miner wird dabei nichts geändert."),
            L.T("Nichts zu tun – am Ende kommt „bestanden“ oder ein Vorschlag."))),
        new("soak-none", EventCategories.Soak, "Kein Vorschlag möglich – im letzten Benchmark gibt es keine stabile Einstellung unterhalb dieser Frequenz.", () => E("soak-none",
            L.T("Kein stabilerer Vorschlag"),
            L.T("Der Dauertest ist fehlgeschlagen, aber der letzte Benchmark kennt keine stabile Einstellung mit weniger Frequenz."),
            L.T("Neuen Benchmark mit niedrigerem Startwert laufen lassen."))),

        // ---------- Einstellungen / Profil ----------
        new("profile", EventCategories.Settings, "Profil gewählt: „{0}“ (gespeichert)", () => E("profile",
            L.T("Profil gewählt"),
            L.T("Für den Miner wurde ein Geräteprofil fest gewählt. Es bestimmt die Grenzen für Frequenz, Spannung und Temperaturen."),
            L.T("Nur das zum Gerät passende Profil verwenden."))),
        new("autosave-failed", EventCategories.Settings, "Automatische Sicherung ({0}) fehlgeschlagen: {1}", () => E("autosave-failed",
            L.T("Sicherung der Miner-Einstellungen fehlgeschlagen"),
            L.T("Vor einer Änderung sollten die AxeOS-Einstellungen gesichert werden, der Miner antwortete aber nicht."),
            L.T("Erreichbarkeit prüfen; eine Sicherung von Hand geht unter Gerät → Sicherungen."))),
        new("restored", EventCategories.Settings, "Wiederhergestellt ({0}): ", () => E("restored",
            L.T("Einstellungen wiederhergestellt"),
            L.T("Gesicherte AxeOS-Einstellungen wurden auf den Miner zurückgespielt – mit den aufgeführten Änderungen."),
            L.T("Nichts zu tun."))),

        // ---------- Server / System ----------
        new("started", EventCategories.System, "Überwachung gestartet ({0} Miner)", () => E("started",
            L.T("Überwachung gestartet"),
            L.T("Server bzw. Desktop-App wurde gestartet (z. B. nach Update, Neustart des Rechners oder Stromausfall)."),
            L.T("Nichts zu tun. Unerwartete Starts deuten auf Stromausfälle oder Abstürze hin."))),
        new("viewer-login", EventCategories.Settings, "Ansicht-Zugang „{0}“ angemeldet.", () => E("viewer-login",
            L.T("Anmeldung mit einem Ansicht-Zugang"),
            L.T("Jemand hat sich mit der PIN dieses Ansicht-Zugangs angemeldet. Er kann nur ansehen, nichts ändern – mit Gruppen nur deren Miner."),
            L.T("Nichts zu tun, wenn das erwartet war. Sonst den Zugang unter Einstellungen → Ansicht-Zugänge widerrufen – wer damit angemeldet ist, wird sofort abgemeldet."))),
        new("viewer-new", EventCategories.Settings, "Ansicht-Zugang „{0}“ angelegt (Gruppen: {1}).", () => E("viewer-new",
            L.T("Ansicht-Zugang angelegt"),
            L.T("Ein Admin hat eine eigene PIN zum Ansehen angelegt, beschränkt auf die genannten Gruppen."),
            L.T("Nichts zu tun. Die PIN wird nur als Hash gespeichert und lässt sich nicht mehr anzeigen."))),
        new("viewer-new-all", EventCategories.Settings, "Ansicht-Zugang „{0}“ angelegt (alle Miner).", () => E("viewer-new",
            L.T("Ansicht-Zugang angelegt"),
            L.T("Ein Admin hat eine eigene PIN zum Ansehen aller Miner angelegt."),
            L.T("Nichts zu tun. Die PIN wird nur als Hash gespeichert und lässt sich nicht mehr anzeigen."))),
        new("viewer-revoked", EventCategories.Settings, "Ansicht-Zugang „{0}“ widerrufen.", () => E("viewer-revoked",
            L.T("Ansicht-Zugang widerrufen"),
            L.T("Die PIN gilt nicht mehr; offene Sitzungen dieses Zugangs wurden beendet."),
            L.T("Nichts zu tun."))),
        new("backup-ok", EventCategories.System, "Sicherung erstellt: {0}", () => E("backup-ok",
            L.T("Sicherung erstellt"),
            L.T("Die tägliche Sicherung wurde geprüft und auf allen Zielen abgelegt."),
            L.T("Nichts zu tun."))),
        new("backup-err", EventCategories.System, "Sicherung mit Fehlern: {0}", () => E("backup-err",
            L.T("Sicherung mit Fehlern"),
            L.T("Mindestens ein Sicherungsziel (USB-Stick, NAS, Ordner) war nicht erreichbar oder voll. Die Sicherung im Datenordner kann trotzdem vorhanden sein."),
            L.T("Unter Einstellungen → Sicherung das Ziel prüfen („Ordner testen“, „Netzlaufwerk testen“)."))),
        new("update-blocked", EventCategories.System, "Update {0} abgebrochen – Sicherung fehlgeschlagen: {1}", () => E("update-blocked",
            L.T("Update abgebrochen"),
            L.T("Vor dem Update konnte keine vollständige Sicherung erstellt werden. Zum Schutz deiner Daten wurde nichts installiert."),
            L.T("Sicherungsziel prüfen und das Update danach erneut starten."))),
        new("update-run", EventCategories.System, "Update auf {0} wird installiert.", () => E("update-run",
            L.T("Update wird installiert"),
            L.T("Die Sicherung war erfolgreich; der Server installiert die neue Version und startet neu."),
            L.T("Nichts zu tun – nach etwa einer Minute läuft der Server wieder."))),
    ];

    /// <summary>Allgemeine Erklärung je Kategorie (wenn keine Vorlage passt).</summary>
    private static EventExplanation Fallback(string category) => category switch
    {
        EventCategories.Tuning => E("cat-tuning", L.T("Frequenz/Spannung geändert"),
            L.T("Frequenz und Kernspannung des Miners wurden geändert. Vorne steht die Quelle: manuell (du), Benchmark (Testschritt), Automatik (freigegebene Regel), Wiederherstellung (Ende eines Benchmarks oder Sicherung), Dauertest, Ratgeber."),
            L.T("Nichts zu tun, wenn die Änderung gewollt ist. Jede Änderung ist im Verlauf (Gerät → Live) markiert.")),
        EventCategories.Benchmark => E("cat-benchmark", L.T("Benchmark"),
            L.T("Meldung aus einem Benchmark-Lauf: Start, Messschritte, verworfene Einstellungen oder Ende."),
            L.T("Ergebnisse unter Gerät → Ergebnisse.")),
        EventCategories.Soak => E("cat-soak", L.T("Dauertest"),
            L.T("Meldung aus einem Dauertest, der eine Einstellung über Stunden beobachtet."),
            L.T("Bei „fehlgeschlagen“ gibt es einen Vorschlag, der nur nach Bestätigung angewendet wird.")),
        EventCategories.Automation => E("cat-automation", L.T("Automatik/Watchdog"),
            L.T("Meldung einer Automatik-Regel oder des Watchdogs."),
            L.T("Regeln unter Gerät → Automatik, Watchdog unter Einstellungen.")),
        EventCategories.Fans => E("cat-fans", L.T("Lüfter"),
            L.T("Meldung der Lüftersteuerung (Pico-Lüfter oder Lüfter des Miners)."),
            L.T("Einstellungen unter „Lüfter & Anzeige“ bzw. Gerät → Live.")),
        EventCategories.Connection => E("cat-connection", L.T("Verbindung"),
            L.T("Erreichbarkeit des Miners hat sich geändert."),
            L.T("Bei häufigen Wechseln WLAN und Stromversorgung prüfen.")),
        EventCategories.Settings => E("cat-settings", L.T("Einstellungen/Profil"),
            L.T("Einstellungen oder Profil eines Miners wurden gesichert, übertragen, wiederhergestellt oder geändert."),
            L.T("Nichts zu tun, wenn die Änderung gewollt ist.")),
        EventCategories.System => E("cat-system", L.T("Server/System"),
            L.T("Ereignis des Servers bzw. der App (Start, Sicherung, Update)."),
            L.T("Nichts zu tun, außer bei Fehlern.")),
        _ => E("cat-other", L.T("Sonstige Meldung"),
            L.T("Allgemeine Meldung ohne eigene Erklärung."),
            L.T("Bei Unklarheiten im Miner-Log (Gerät → Protokolle) nachsehen.")),
    };

    private static readonly Dictionary<string, Regex[]> Patterns = new();

    private static Regex[] PatternsOf(Entry e)
    {
        lock (Patterns)
        {
            if (Patterns.TryGetValue(e.Id, out var p)) return p;
            var templates = new[] { e.Template, Loc.For("en").T(e.Template) }.Distinct();
            p = templates.Select(t => new Regex(
                Regex.Replace(Regex.Escape(t), @"\\\{\d+(?::[^}]*)?\}", "(.*?)"),
                RegexOptions.CultureInvariant | RegexOptions.Singleline)).ToArray();
            return Patterns[e.Id] = p;
        }
    }

    /// <summary>Passende Erklärung (Sprache des Programms); immer eine – sonst die der Kategorie.</summary>
    public static EventExplanation For(EventEntry entry)
    {
        foreach (var e in Entries)
            if ((e.Category == entry.Category || entry.Category == EventCategories.Other) && PatternsOf(e).Any(r => r.IsMatch(entry.Message)))
                return e.Make();
        return Fallback(entry.Category);
    }

    /// <summary>Für Tests: alle Vorlagen (sie müssen im Code vorkommen).</summary>
    public static IEnumerable<string> Templates => Entries.Select(e => e.Template);
}
