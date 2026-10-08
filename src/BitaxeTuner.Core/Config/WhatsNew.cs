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
            new("0.8.1", tr.T("Vergleich neu gestaltet"),
                server ? tr.T("Je Miner eine Zeile mit Suche, Status- und Modellfilter, Sortieren per Klick, Bestwerten (★) und Spaltengruppen – auch auf dem Handy.")
                       : tr.T("Der Miner-Vergleich hat jetzt Suche, Modellfilter und „nur online“."),
                null),
            new("0.8.1", tr.T("Benchmark: weniger Spannung testen"),
                server ? tr.T("Im Browser gibt es jetzt alle Benchmark-Optionen wie am Desktop – auch „Pro Frequenz auch niedrigere Spannung testen“, Neustart nach jeder Änderung und Grenzen der Eingangsspannung.")
                       : tr.T("Die Bestätigung vor dem Benchmark nennt jetzt auch die niedrigste Spannung, wenn „Pro Frequenz auch niedrigere Spannung testen“ eingeschaltet ist."),
                null),
            new("0.8.1", tr.T("Browser kann jetzt alles wie die Desktop-App"),
                tr.T("Je Miner Wallet-Adresse, Coin, Firmware-Repository und Log-Alarme (Einstellungen → Geräte → Details); Voreinstellungen aus der aktuellen Einstellung oder dem Benchmark übernehmen."),
                null),
            new("0.8.1", tr.T("AxeOS mit einem Klick"),
                server ? tr.T("Übersicht, Vergleich und Geräteseite verlinken direkt die Weboberfläche des Miners.")
                       : tr.T("In der Browser-Oberfläche verlinken Übersicht, Vergleich und Geräteseite direkt die Weboberfläche des Miners; in der Desktop-App wie bisher über „Weboberfläche“."),
                null),
            new("0.8.2", tr.T("Vergleichsbericht zum Ausdrucken"),
                server ? tr.T("Vergleich → „Bericht …“: Miner, Zeitraum, Werte und Diagramme wählen und als druckbare Seite (PDF) öffnen. Vorauswahl „Kühlung“ für die Frage, warum ein Miner heißer läuft – ohne IP- und Wallet-Adressen, zum Teilen geeignet.")
                       : tr.T("Miner-Vergleich → „Bericht …“: Miner, Zeitraum, Werte und Diagramme wählen und als druckbare Seite (PDF) öffnen. Vorauswahl „Kühlung“ für die Frage, warum ein Miner heißer läuft – ohne IP- und Wallet-Adressen, zum Teilen geeignet."),
                null),
            new("0.8.2", tr.T("Berichte je Push-Dienst anpassen"),
                tr.T("Tages- und Monatsbericht enthalten bei einem Push-Dienst mit Miner-Auswahl nur diese Miner (z. B. für eine Community-Gruppe) und nie Zuflüsse. Je Dienst abwählbar: Stromkosten, Zuflüsse, Empfehlungen, Best-Diff-Rekord, Tuning-Änderungen, Steckdosen-Details."),
                tr.T("Push-Benachrichtigungen")),
            new("0.9.0", tr.T("Protokoll"),
                server ? tr.T("Neue Seite „Protokoll“: wer wann welche Frequenz/Spannung gesetzt hat (mit Quelle), Benchmarks, Dauertests, Automatik, Lüfterregelung, Verbindungen und Server-Ereignisse – mindestens 30 Tage, filterbar, als CSV. Läuft nach Updates weiter.")
                       : tr.T("Neues Fenster „Protokoll …“: wer wann welche Frequenz/Spannung gesetzt hat (mit Quelle), Benchmarks, Dauertests, Automatik, Lüfterregelung und Verbindungen – mindestens 30 Tage, filterbar, als CSV. Läuft nach Updates weiter."),
                null),
            new("0.9.0", tr.T("Sicherung vor jedem Update"),
                tr.T("Vor jedem Server-Update entsteht automatisch eine geprüfte Sicherung (Datenordner, USB-Stick, NAS) – schlägt sie fehl, wird nichts installiert. Auf Wunsch lädt der Browser sie vorher auf den PC; in der Desktop-App landet sie im Sicherungsordner."),
                tr.T("Sicherung")),
            new("0.9.0", tr.T("Server und App mit einem Klick aktualisieren"),
                server ? tr.T("In der Desktop-App (Modus „Server“) aktualisiert ein Klick oben beides: erst Sicherung auf den PC, dann der Server, danach die App.")
                       : tr.T("Im Modus „Server“ aktualisiert ein Klick oben beides: erst Sicherung auf den PC, dann der Server (mit eigener Sicherung auf USB/NAS), danach die App – schlägt ein Schritt fehl, wird nicht weitergemacht."),
                null),
            new("0.9.0", tr.T("Lüfter des Miners einstellen"),
                server ? tr.T("Geräteseite → Live: Lüfter des Miners auf Automatik mit Zieltemperatur oder auf einen festen Wert stellen – mit Bestätigung, innerhalb der Profilgrenzen, im Protokoll.")
                       : tr.T("Miner → „Manuell einstellen“: Lüfter des Miners auf Automatik mit Zieltemperatur oder auf einen festen Wert stellen – mit Bestätigung, innerhalb der Profilgrenzen, im Protokoll."),
                null),
            new("0.9.1", tr.T("Miner-Gruppen"),
                server ? tr.T("Miner in Gruppen ordnen (Einstellungen → Geräte → Details), z. B. „Community“ oder „Keller“: Filter und Summen in der Übersicht, Filter im Vergleich, Push-Dienste für ganze Gruppen – neue Mitglieder sind automatisch dabei.")
                       : tr.T("Miner in Gruppen ordnen (Einstellungen → Miner), z. B. „Community“ oder „Keller“: Filter im Vergleich, Push-Dienste für ganze Gruppen – neue Mitglieder sind automatisch dabei."),
                null),
            new("0.9.1", tr.T("Protokoll mit Erklärungen"),
                tr.T("Ein Klick auf einen Protokolleintrag zeigt, was er bedeutet und was du tun kannst. Außerdem: keine falschen Grenzwert-Meldungen der Automatik mehr direkt nach dem Start, Watchdog-Neustarts stehen im Protokoll."),
                null),
            new("0.9.1", tr.T("Benchmark-Ergebnisse in die Automatik"),
                tr.T("In der Ergebnisliste jedes stabile Ergebnis per „In Automatik speichern …“ als Voreinstellung für Zeitplan und Strompreis übernehmen – am Miner ändert sich dabei nichts. Die Liste lässt sich per Klick auf die Spalten sortieren, z. B. J/TH von niedrig nach hoch."),
                null),
            new("0.9.1", tr.T("Ansicht-Zugänge je Gruppe"),
                server ? tr.T("Eigene PIN zum Ansehen je Person (Einstellungen → Ansicht-Zugänge), auf Wunsch nur für bestimmte Miner-Gruppen – z. B. für Mitbewohner oder Community-Miner. Einzeln widerrufbar, Anmeldungen stehen im Protokoll.")
                       : tr.T("Im Server-Betrieb: eigene PIN zum Ansehen je Person, auf Wunsch nur für bestimmte Miner-Gruppen – einzeln widerrufbar, Anmeldungen stehen im Protokoll."),
                null),
            new("0.9.2", tr.T("Sicherheits-Update nach einem unabhängigen Audit"),
                tr.T("Tokens und API-Schlüssel liegen jetzt geschützt in secrets.json statt in config.json und damit nicht mehr in Sicherungen. Aus dem Miner erkannte Wallet-Adressen werden erst nach deiner Zustimmung abgefragt. Ohne passendes Geräteprofil gelten vorsichtige Grenzen. Steuer: Restbestand im CSV-Export und Verkäufe ohne Zufluss korrigiert. Windows-Server: Datenordner und Updates nur noch für Administratoren."),
                null),
            new("0.9.2", tr.T("Fehlerbehebungen"),
                tr.T("Der Windows-Server-Dienst startet wieder (er brach beim Start ab). Der Raspberry Pi findet den Pico mit Port „auto“ wieder. Verbindungsfehler zum Pico stehen jetzt im Protokoll."),
                null),
            new("0.9.3", tr.T("Prometheus / Grafana"),
                server ? tr.T("Messwerte aller Miner unter /metrics für eigene Grafana-Dashboards – ohne IP- und Wallet-Adressen, standardmäßig aus, nur mit eigenem Token (Einstellungen → Prometheus / Grafana).")
                       : tr.T("Im Server-Betrieb: Messwerte aller Miner unter /metrics für eigene Grafana-Dashboards – ohne IP- und Wallet-Adressen, nur mit eigenem Token."),
                null),
            new("0.9.3", tr.T("Mehr Sicherheit bei Lüftern und Benchmark"),
                tr.T("Auch manuell eingestellte Lüfter laufen bei zu heißem Miner mit voller Drehzahl; Benchmark-Grenzen über dem Geräteprofil werden in der Bestätigung deutlich genannt; Lüfterwerte aus Sicherungen und Übertragungen werden geprüft."),
                null),
            new("0.9.3", tr.T("Geräteprofile geprüft"),
                tr.T("Alle Profile mit der aktuellen Firmware (ESP-Miner, NerdQAxe+) abgeglichen: Die Bitaxe GammaDuo hat die Chip-Variante BM1370XP und erlaubt jetzt 350–410 MHz statt bis 800 MHz; Standardwerte der GammaHex wie in der Firmware. Keine neuen Modelle."),
                null),
            new("0.9.4", tr.T("Signierte Updates"),
                tr.T("Jedes Release ist jetzt mit dem Schlüssel des Projekts signiert. Desktop-App und Server installieren ein Update nur, wenn die Signatur gültig ist – ein gekapertes GitHub-Konto allein reicht nicht mehr, um ein Update unterzuschieben."),
                null),
            new("0.9.4", tr.T("Verschlüsselte Verbindung (HTTPS)"),
                server ? tr.T("Neue Installationen starten mit HTTPS. Bestehende Server lassen sich unter Einstellungen → Verbindung mit einem Klick umstellen – danach gehen Passwort, Token und Sitzung im Heimnetz verschlüsselt.")
                       : tr.T("Im Server-Betrieb: Läuft der Server noch unverschlüsselt, zeigt das Serverfenster einen Knopf „Auf HTTPS umstellen“; die App übernimmt Adresse und Zertifikat selbst."),
                null),
            new("0.9.5", tr.T("Sicherere Anmeldung"),
                tr.T("Neue Ansicht-PINs brauchen mindestens 6 Ziffern und werden mit Salz gespeichert; ältere PINs gelten weiter, sollten aber einmal neu gesetzt werden. Viele Fehlversuche bremsen weitere Anmeldungen. Neue Pi-Images verlangen ein Passwort mit mindestens 10 Zeichen."),
                null),
            new("0.9.5", tr.T("Robuster im Dauerbetrieb"),
                tr.T("Weniger Schreibzugriffe auf SD-Karte und SSD; schlägt das Speichern der Einstellungen fehl, gibt es einen deutlichen Hinweis; nicht zugestellte Push-Meldungen werden bis zu 6 Stunden lang erneut versucht."),
                null),
            new("0.9.6", tr.T("Steuer im Browser bearbeiten"),
                server ? tr.T("Unter Steuer lassen sich jetzt Wallets hinzufügen (auch aus den Minern), fehlende Kurse und Notizen nachtragen, Eingänge ohne Mining-Ertrag entfernen und Verkäufe mit Haltefrist-Rechnung erfassen – direkt im Browser, gleiche Daten wie in der Desktop-App.")
                       : tr.T("Im Server-Betrieb: Wallets, Kurse und Verkäufe lassen sich jetzt auch im Browser bearbeiten – die Steuerdaten müssen dafür nicht mehr zurück auf den PC."),
                null),
            new("0.9.7", tr.T("Pico über WLAN"),
                server ? tr.T("Lüftersteuerung und E-Paper brauchen kein USB-Kabel zum Server mehr: Pico 2 WH einmal unter Lüfter & Anzeige „für WLAN einrichten“, dann an ein eigenes Netzteil. Jede Zeile ist signiert, der Ausfallschutz bleibt, Updates kommen über WLAN.")
                       : tr.T("Im Server-Betrieb: Lüftersteuerung und E-Paper können über WLAN angebunden werden (Pico 2 WH) – kein USB-Kabel zum Server mehr nötig."),
                null),
            new("0.9.11", tr.T("Temperatur jedes Chips"),
                tr.T("Bei Boards mit mehreren Chips (z. B. NerdQAxe, Gamma Hex) zeigt BitaxeTuner jeden Chip einzeln, den heißesten hervorgehoben, mit Verlauf je Chip. Lüfter, Temperaturschutz und Meldungen richten sich nach dem heißesten Chip; Home Assistant und Prometheus bekommen jeden Chip als eigenen Wert."),
                null),
            new("0.9.11", tr.T("Pico-Programm 8 mit Hardware-Watchdog"),
                tr.T("Hängt das Programm auf dem Pico, startet er nach 8 Sekunden von selbst neu – die Lüfter laufen dabei mit 100 %. Der Lüfter-Notlauf endet erst wieder mit einem gültigen Befehl vom Server. Der Server spielt das Programm beim nächsten Verbinden automatisch auf (USB und WLAN)."),
                null),
            new("0.9.11", tr.T("Übersicht selbst gestalten"),
                server ? tr.T("Einstellungen → „Übersicht gestalten“: Panels wie beim Kiosk anordnen (Kennzahlen, Verlauf, Miner, Smart Plugs, Neuigkeiten, eigener Text …), jedes mit eigener Breite. Dazu die Reihenfolge der Miner – z. B. Gamma 1, 2, 3 –, die dann überall gilt.")
                       : tr.T("Die Reihenfolge der Miner lässt sich im Browser unter Einstellungen → „Übersicht gestalten“ festlegen und gilt dann auch hier in der App."),
                null),
            new("0.9.11", tr.T("Mehrere E-Paper-Anzeigen"),
                tr.T("Unter Lüfter & Anzeige → „Weitere Anzeigen“ bis zu acht zusätzliche E-Paper anlegen: jede mit eigenem Display-Pico (am besten per WLAN), eigenen Seiten, eigenem Intervall und auf Wunsch nur für eine Miner-Gruppe. Taste 1 blättert auf der eigenen Anzeige, Blockfunde und Warnungen erscheinen überall."),
                null),
            new("0.9.11", tr.T("Neuigkeiten aus der Solo-Mining-Welt"),
                tr.T("Neue E-Paper-Seite „Neuigkeiten“ (Lüfter & Anzeige → Seiten) und eine Liste auf der Hilfe-Seite: Solo-Blockfunde (BTC einzeln, BCH als Tagessumme), neue Firmware für Bitaxe und NerdQAxe, neue Miner-Modelle, Difficulty-Anpassungen und neue BitaxeTuner-Versionen. Gesammelt alle 6 Stunden vom BitaxeTuner-Projekt – der Server holt nur diese eine öffentliche Datei, ohne Daten von dir."),
                null),
            new("0.9.11", tr.T("Wartungsmodus je Miner"),
                server ? tr.T("Haken „Wartungsmodus“ oben auf der Geräteseite: Während du am Miner arbeitest, pausiert seine Überwachung – keine Meldungen (außer Blockfunden), kein Watchdog-Neustart, keine Automatik, und die Zeit zählt nicht als Ausfall. Endet auf Wunsch nach 1–24 Stunden von selbst.")
                       : tr.T("Haken „Wartungsmodus“ oben neben dem Profil: Während du am Miner arbeitest, pausiert seine Überwachung – keine Meldungen (außer Blockfunden), kein Watchdog-Neustart, keine Automatik, und die Zeit zählt nicht als Ausfall. Endet auf Wunsch nach 1–24 Stunden von selbst."),
                null),
            new("0.9.11", tr.T("Eine Währung für alles"),
                tr.T("Unter Einstellungen → Allgemein wählst du die Währung (Euro, US-Dollar, Pfund, Franken, Kronen, Złoty …): Kurse, Erträge, Stromkosten, Berichte, E-Paper, Kiosk und Steuer-Übersicht erscheinen darin, Strompreise in der passenden Untereinheit (ct, ¢, p, Rp.). Euro-Kurse werden weiter mit erfasst – vorhandene Daten bleiben unverändert, fehlende Kurse der neuen Währung werden für das letzte Jahr nachgeholt."),
                null),
            new("0.9.11", tr.T("Pool umschalten"),
                server ? tr.T("Je Miner (Gerät → Automatik → Pool-Umschaltung) oder für eine ganze Gruppe mit Vorschau alt → neu. Dazu die Pool-Automatik mit Freigabe: Ersatz-Pool nach Zeitplan, zurück zum Haupt-Pool, sobald er wieder erreichbar ist, und Wechsel bei vielen abgelehnten Shares.")
                       : tr.T("Neuer Knopf „Pool …“ oben: Pool je Miner oder Gruppe umschalten (Vorschau alt → neu) und Pool-Automatik mit Freigabe – Ersatz-Pool nach Zeitplan, zurück zum Haupt-Pool, Wechsel bei vielen abgelehnten Shares."),
                null),
            new("0.9.11", tr.T("Miner-Logs speichern"),
                server ? tr.T("Je Miner einschaltbar (Einstellungen → Geräte → Details): Die Logs werden 48 Stunden gespeichert (einstellbar) und lassen sich unter Gerät → Protokolle filtern und herunterladen.")
                       : tr.T("Je Miner einschaltbar (Einstellungen → Geräte): Die Logs werden 48 Stunden gespeichert (einstellbar) – ansehen und exportieren über „Gespeicherte Logs …“ im Tab Miner-Logs."),
                null),
            new("0.9.10", tr.T("Sicherheits-Update nach dem zweiten Audit"),
                tr.T("Lüfter gehen auf 100 %, wenn die Lüftersteuerung ausgeschaltet wird; Lüfter-Sicherheit auch im manuellen Modus aller Fühler und höchstens bis zur VR-Grenze des Profils. Profil-Kopien und von Hand bearbeitete Profile werden gegen feste Obergrenzen je Chip geprüft. Updates sind an ihre Version gebunden; Releases werden erst nach Freigabe signiert. Steuer: „65.000“ wird als 65 000 € gelesen, Zuflüsse am Verkaufstag zählen mit."),
                null),
            new("0.9.10", tr.T("Gruppen-Automatik einmal neu freigeben"),
                tr.T("Die Freigabe einer Gruppen-Automatik gilt jetzt nur für genau die freigegebenen Werte der Voreinstellungen und die Profilgrenzen. Bestehende Gruppen-Automatiken bitte einmal neu freigeben (Übersicht → Gruppe → Gruppen-Automatik)."),
                null),
            new("0.9.9", tr.T("Hilfe im Programm"),
                server ? tr.T("Neuer Menüpunkt „Hilfe“: Kurzanleitung, jede Art von Protokolleintrag mit Bedeutung und „Was tun“, die Miner-Logs (AxeOS/ESP-Miner) erklärt und häufige Fragen – mit Suche.")
                       : tr.T("Neuer Knopf „Hilfe“ oben: Kurzanleitung, jede Art von Protokolleintrag mit Bedeutung und „Was tun“, die Miner-Logs (AxeOS/ESP-Miner) erklärt und häufige Fragen – mit Suche."),
                null),
            new("0.9.9", tr.T("Geräteprofile bearbeiten"),
                server ? tr.T("Einstellungen → Geräteprofile: Grenzen je Modell anpassen, eigene Profile als Kopie anlegen oder auf den eingebauten Stand zurücksetzen. Grenzen über dem eingebauten Profil nur nach Rückfrage; jede Änderung steht im Protokoll.")
                       : tr.T("„Profile bearbeiten“ öffnet jetzt ein Formular statt der JSON-Datei: Grenzen je Modell anpassen, eigene Profile als Kopie anlegen oder zurücksetzen – wirkt sofort, ohne Neustart."),
                null),
            new("0.9.9", tr.T("Blockfund-Anzeige bis zur Taste"),
                tr.T("Das Blockfund-Vollbild auf dem E-Paper endet wahlweise nach einstellbaren Stunden oder bleibt – wie die Warnungen – stehen, bis Taste 1 gedrückt wird (Lüfter & Anzeige → Sonderanzeigen)."),
                null),
            new("0.9.8", tr.T("Kiosk / Wand-Tablet"),
                server ? tr.T("Vollbild-Anzeige ohne Menü für ein Tablet an der Wand – frei gestaltbar im Kiosk-Designer: Farben, Animationen, Panels per Ziehen anordnen, je Kiosk-Link ein eigenes Design. Anmeldung per Kiosk-Link (meldet sich selbst wieder an, widerrufbar) oder Ansicht-PIN – unter Einstellungen → Kiosk / Wand-Tablet.")
                       : tr.T("Handy-Ansicht mit „?kiosk=1“ öffnen: große Anzeige für ein Tablet an der Wand. Im Server-Betrieb gibt es zusätzlich Kiosk-Links."),
                null),
            new("0.9.8", tr.T("Sicherer und robuster"),
                tr.T("Restliche Punkte aus dem Sicherheits-Audit: Steuer rechnet in fester Zeitzone (Europe/Berlin), Gewinn ohne bekannte Anschaffungskosten wird getrennt ausgewiesen, Tibber-Viertelstundenpreise, Drosselung bei mempool.space, Speicherplatz-Prüfung vor Datenübernahmen, Reverse-Proxy-Option und schreibgeschützter Programmordner auf dem Pi."),
                null),
            new("0.9.8", tr.T("Mindestdrehzahl der Lüfter-Automatik"),
                tr.T("Bei „Lüfter des Miners“ lässt sich in der Automatik jetzt wie in AxeOS die Mindestdrehzahl einstellen (Firmware mit „minFanSpeed“); sie wird auch gesichert und wiederhergestellt."),
                null),
            new("0.9.7", tr.T("Gruppen-Automatik"),
                server ? tr.T("Ein Zeitplan oder eine Strompreis-Regel für eine ganze Miner-Gruppe – jeder Miner nutzt dabei seine eigene Voreinstellung gleichen Namens. Dazu „jetzt umschalten“ mit Vorschau alt → neu. In der Übersicht eine Gruppe wählen → Gruppen-Automatik.")
                       : tr.T("Im Server-Betrieb: Zeitplan oder Strompreis-Regel für eine ganze Miner-Gruppe, jeder Miner mit seiner eigenen Voreinstellung gleichen Namens."),
                null),
            new("0.9.7", tr.T("Neue Seiten auf dem E-Paper"),
                tr.T("Kurs von BTC, BCH oder beiden mit 24-h-Verlauf und Countdown bis zur nächsten Difficulty-Anpassung, Monatsbilanz mit Balken je Tag, Tagesbilanz wahlweise mit Graph, je Miner-Gruppe eine Seite, Strompreis-Ampel, Temperaturfühler und QR-Code zur Oberfläche – einzeln unter Lüfter & Anzeige einschaltbar."),
                null),
            new("0.9.7", tr.T("Eigener Display-Pico"),
                tr.T("Das E-Paper kann einen eigenen Pico bekommen, der einfach auf das Waveshare-Modul gesteckt wird – ohne Kabel und ohne Platine. Neu außerdem: Farben umkehren (helle Schrift auf Schwarz)."),
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
