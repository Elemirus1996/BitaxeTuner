using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Help;

/// <summary>Ein Eintrag der Hilfe: Überschrift und Text (Absätze mit Leerzeile getrennt).</summary>
public sealed record HelpItem(string Title, string Text);

/// <summary>Abschnitt der Hilfe (Anleitung, Protokoll, Miner-Logs, Fragen).</summary>
public sealed record HelpSection(string Id, string Title, string Intro, IReadOnlyList<HelpItem> Items);

/// <summary>
/// Hilfe im Programm (0.9.9) – gleicher Inhalt für Desktop-App und Browser, in der Sprache des Programms.
/// Die Erklärungen des Protokolls kommen aus <see cref="EventExplanations"/>, damit Hilfe und Protokoll übereinstimmen.
/// </summary>
public static class HelpContent
{
    public static IReadOnlyList<HelpSection> Sections(bool server) =>
    [
        Guide(server),
        Journal(),
        MinerLogs(),
        Faq(server),
    ];

    private static HelpSection Guide(bool server) => new("guide", L.T("Anleitung"),
        L.T("Die wichtigsten Abläufe in Kürze. Alles, was Frequenz oder Spannung ändert, passiert nur nach einer Bestätigung mit altem und neuem Wert und innerhalb der Grenzen des Geräteprofils."),
        [
            new(L.T("Miner hinzufügen"), server
                ? L.T("Einstellungen → Geräte: „Im Netz suchen“ findet Bitaxe und NerdAxe im Heimnetz, „Hinzufügen“ übernimmt sie. Alternativ Name und IP-Adresse von Hand eintragen. Tipp: im Router eine feste IP-Adresse vergeben.")
                : L.T("„Hinzufügen“ oder „Im Netz suchen“ in der Geräteliste. Name und IP-Adresse genügen; das Geräteprofil (ASIC-Modell und Grenzen) erkennt BitaxeTuner selbst. Tipp: im Router eine feste IP-Adresse vergeben.")),
            new(L.T("Übersicht und Gerät"), L.T("Die Übersicht zeigt je Miner Hashrate, Chip- und VR-Temperatur, Leistung und Effizienz (J/TH), oben die Summen. Ein Klick auf einen Miner öffnet Live-Werte, Verlauf, Automatik, Ergebnisse, Sicherungen und Logs.\n\nDer Punkt vor dem Namen: grün = online, grau = Wartung/Neustart, rot = nicht erreichbar.")),
            new(L.T("Frequenz und Spannung ändern"), L.T("Gerät → Live → „Manuell einstellen“. Vor dem Senden zeigt ein Dialog den aktuellen und den neuen Wert und die Grenzen des Profils; Werte außerhalb werden abgelehnt. Jede Änderung wird mit Zeit und altem/neuem Wert protokolliert und im Verlauf markiert.")),
            new(L.T("Benchmark"), L.T("Gerät → Benchmark testet Einstellungen Schritt für Schritt (Hashrate, Leistung, Temperaturen, Fehlerrate) und findet die schnellste bzw. effizienteste stabile Einstellung. Grenzen für Temperatur und Leistung lassen sich setzen; Werte über dem Profil werden deutlich genannt. Am Ende stellt BitaxeTuner die vorherige Einstellung wieder her. Ergebnisse lassen sich anwenden oder als Voreinstellung für die Automatik speichern.")),
            new(L.T("Dauertest"), L.T("Beobachtet die aktuelle Einstellung über Stunden (Hashrate gegenüber Soll, Fehlerrate, Temperaturen, Erreichbarkeit), ohne etwas zu ändern. Schlägt er fehl, kommt ein Vorschlag mit der nächstniedrigeren stabilen Einstellung – angewendet wird er nur nach Bestätigung.")),
            new(L.T("Automatik"), L.T("Gerät → Automatik: Temperaturschutz (senkt die Frequenz in Schritten, wenn es zu warm wird), Zeitplan und Strompreis-Regel (wechseln zwischen Voreinstellungen). Jede Regel handelt erst nach ausdrücklicher Freigabe; wird sie geändert, ist eine neue Freigabe nötig.\n\nGruppen-Automatik: eine Regel für eine ganze Miner-Gruppe, jeder Miner nutzt seine eigene Voreinstellung gleichen Namens.")),
            new(L.T("Lüfter, E-Paper und Taster"), L.T("Ein Raspberry Pi Pico regelt bis zu sechs Zusatzlüfter, zeigt ein 7,5\"-E-Paper an und liest vier Taster – per USB am Server oder über WLAN (Pico 2 WH). Ausfallschutz: ohne Befehl, ohne Daten oder bei Kabelbruch laufen die Lüfter mit 100 %. Einstellungen und Vorschau der Anzeige unter „Lüfter & Anzeige“.")),
            new(L.T("Meldungen aufs Handy"), L.T("Einstellungen → Benachrichtigungen: ntfy, Telegram, Discord, Pushover oder eigener Webhook, je Ziel mit eigenen Bereichen und Minern. Meldungen, die nicht ankommen, werden bis zu 6 Stunden lang erneut versucht.")),
            new(L.T("Steuer"), L.T("Dokumentiert Zuflüsse (Blockfunde, Pool-Auszahlungen) mit EUR-Kurs zum Zuflusszeitpunkt, Verkäufe mit Haltefrist (FIFO, ein Jahr) und CSV-Export. Rohrechnung nach deutschem Steuerrecht für die eigene Übersicht – keine Steuerberatung.")),
            new(L.T("Sicherung"), L.T("Täglich werden Einstellungen, Verlauf und Steuerdaten gesichert, zusätzlich auf USB-Stick, zweite Festplatte oder NAS, wenn eingerichtet. Passwörter und Schlüssel liegen getrennt in secrets.json und gehen nie in Sicherungen.")),
            new(server ? L.T("Kiosk / Wand-Tablet") : L.T("Server-Betrieb"), server
                ? L.T("Einstellungen → Kiosk / Wand-Tablet: Kiosk-Link anlegen und einmal auf dem Tablet öffnen. Im Kiosk-Designer lassen sich Farben, Animationen und die Anordnung der Panels gestalten.")
                : L.T("Soll rund um die Uhr überwacht werden, ohne dass der PC läuft: „Betriebsart …“ richtet einen Server ein (Raspberry Pi, zweiter PC, Docker). Die Daten lassen sich hin und zurück übertragen.")),
        ]);

    private static HelpSection Journal()
    {
        var items = EventExplanations.All()
            .GroupBy(x => x.Category)
            .SelectMany(g => g.Select(x => new HelpItem($"{EventCategories.Label(g.Key)}: {x.Explanation.Title}",
                x.Explanation.Meaning + "\n\n" + L.T("Was tun: {0}", x.Explanation.Action))))
            .ToList();
        return new("journal", L.T("Protokoll erklärt"),
            L.T("Das Protokoll (Browser „Protokoll“, Desktop „Protokoll …“) hält mindestens 30 Tage fest, was passiert ist: Änderungen von Frequenz und Spannung, Benchmarks, Dauertests, Automatik, Lüfter, Verbindungen, Einstellungen und Server-Ereignisse. Ein Klick auf einen Eintrag zeigt die passende Erklärung – hier stehen alle auf einen Blick."),
            items);
    }

    private static HelpSection MinerLogs()
    {
        var patterns = string.Join("\n", new LogAlertSettings().Patterns.Select(p => "• " + p));
        return new("minerlog", L.T("Miner-Logs erklärt"),
            L.T("Bitaxe und NerdAxe (AxeOS/ESP-Miner) schreiben laufend ein Protokoll. BitaxeTuner zeigt es live unter Gerät → Logs und kann es dauerhaft mitlesen und bei auffälligen Zeilen melden. Die genauen Texte hängen von Firmware und Modell ab – die folgenden Bereiche helfen beim Einordnen."),
            [
                new(L.T("Aufbau einer Zeile"), L.T("Am Anfang steht die Stufe: I = Information, W = Warnung, E = Fehler. Die Zahl in Klammern ist die Zeit seit dem Start in Millisekunden, danach folgt das Modul, das die Zeile schreibt (z. B. Pool-Verbindung, ASIC-Ergebnisse, Stromversorgung, WLAN).\n\nEinzelne W- oder E-Zeilen sind oft harmlos (z. B. ein kurzer Verbindungsabbruch). Wichtig sind Zeilen, die sich häufen oder mit fehlender Hashrate zusammenfallen.")),
                new(L.T("Pool-Verbindung (Stratum)"), L.T("Verbindungsaufbau, neue Aufgaben („Jobs“) vom Pool und Rückmeldungen zu eingereichten Shares (angenommen oder abgelehnt).\n\nWas tun: Bei vielen abgelehnten Shares oder häufigen Neuverbindungen Pool-Adresse, Port und WLAN prüfen; eine zu hohe Einstellung kann ebenfalls Fehler erzeugen. Wechselt der Miner auf den Fallback-Pool, meldet BitaxeTuner das.")),
                new(L.T("ASIC-Ergebnisse"), L.T("Gefundene Lösungen der Chips mit ihrer Schwierigkeit (Difficulty). Liegt sie über der Pool-Difficulty, wird ein Share eingereicht; der höchste Wert seit dem Start ist die „Best Difficulty“. Ein Wert über der Netzwerk-Difficulty wäre ein Blockfund.\n\nWas tun: nichts – das ist der normale Betrieb.")),
                new(L.T("Temperatur, Lüfter, Stromversorgung"), L.T("Messwerte und Regelung von Chip- und Spannungsregler-Temperatur, Lüfter und Kernspannung. Meldungen zu Überhitzung („overheat“) bedeuten, dass sich der Miner selbst schützt und Leistung zurücknimmt oder anhält.\n\nWas tun: Kühlung verbessern (Lüfter, Kühlkörper, Luftweg), Umgebungstemperatur prüfen oder Frequenz/Spannung senken. BitaxeTuner kann das mit dem Temperaturschutz selbst übernehmen.")),
                new(L.T("Fehler des Spannungsreglers"), L.T("Meldungen wie „power fault“ oder Fehler bei Vcore/Spannung kommen vom Spannungsregler: Unter- oder Überspannung, Überstrom oder zu heiß.\n\nWas tun: Netzteil (Leistung, Kabel, Stecker) prüfen und eine niedrigere Spannung/Frequenz wählen. Treten sie bei Werkseinstellung auf, liegt eher ein Hardware- oder Netzteilproblem vor.")),
                new(L.T("ASIC nicht gefunden / Initialisierung"), L.T("Beim Start sucht die Firmware die Chips und stellt sie ein. Werden weniger Chips gefunden als erwartet oder schlägt die Initialisierung fehl, liefert der Miner keine oder zu wenig Hashrate.\n\nWas tun: Miner stromlos neu starten; passt die Firmware zum Board? Bleibt der Fehler, ist es meist ein Hardwareproblem.")),
                new(L.T("WLAN"), L.T("Verbindungsaufbau, Verlust und Wiederverbindung des WLANs sowie die erhaltene IP-Adresse.\n\nWas tun: Bei häufigen Abbrüchen Abstand zum Router, 2,4-GHz-Netz und Störquellen prüfen; eine feste IP-Adresse im Router verhindert wechselnde Adressen.")),
                new(L.T("Absturz und Watchdog"), L.T("Zeilen mit „watchdog“, „panic“, „abort“ oder „rebooting“ zeigen, dass der Miner hängen geblieben oder abgestürzt ist und neu startet.\n\nWas tun: Meist ist die Einstellung zu knapp oder das Netzteil am Limit – mit einem Dauertest prüfen und eine Stufe zurückgehen.")),
                new(L.T("Log-Alarme in BitaxeTuner"), L.T("Unter Einstellungen → Log-Alarme lässt sich festlegen, bei welchen Zeilen eine Meldung kommt: jede Zeile der Stufe E und Zeilen, die zu einem Muster passen (regulärer Ausdruck, Groß-/Kleinschreibung egal). Voreingestellt sind:\n{0}\n\nDauerhaftes Mitlesen ist je Miner einzuschalten (Gerät → Logs), weil es einen der wenigen Log-Plätze des Miners belegt.", patterns)),
            ]);
    }

    private static HelpSection Faq(bool server) => new("faq", L.T("Häufige Fragen"), "",
        [
            new(L.T("Der Miner wird als offline gezeigt, AxeOS geht aber"), L.T("Meist hat sich die IP-Adresse geändert (DHCP). Im Router eine feste Adresse vergeben und die neue Adresse in BitaxeTuner eintragen.")),
            new(L.T("Warum lässt sich eine Frequenz nicht setzen?"), L.T("Sie liegt außerhalb der Grenzen des Geräteprofils. Die Grenzen schützen die Hardware; das Profil lässt sich unter Gerät → Live prüfen und bei Bedarf anpassen.")),
            new(L.T("Der Browser warnt vor dem Zertifikat"), L.T("Der Server nutzt ein selbst ausgestelltes Zertifikat für HTTPS. Die Warnung einmal bestätigen; die Desktop-App prüft den Fingerabdruck beim Verbinden.")),
            new(L.T("Der Pico wird nicht gefunden"), L.T("Ein Datenkabel verwenden (kein reines Ladekabel), MicroPython muss installiert sein. Unter Linux muss der Dienst zur Gruppe „dialout“ gehören (übernimmt die Installation).")),
            new(L.T("Wie kommen Updates?"), server
                ? L.T("Der Server meldet neue Versionen und installiert sie mit einem Klick (Einstellungen → Update). Jedes Update ist signiert; unsignierte werden abgelehnt.")
                : L.T("Die App meldet neue Versionen und installiert sie mit einem Klick. Jedes Update ist signiert; unsignierte werden abgelehnt.")),
            new(L.T("Wo liegen meine Daten?"), L.T("Im Datenordner (Einstellungen → Datenordner): config.json, history.db (Verlauf, Protokoll), Steuerdaten, Sicherungen. Passwörter und Schlüssel getrennt in secrets.json.")),
        ]);
}
