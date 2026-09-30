<p align="center"><a href="https://elemirus1996.github.io/BitaxeTuner/"><img src="docs/assets/banner.png" alt="BitaxeTuner – Bitaxe & NerdAxe automatisch optimieren und rund um die Uhr überwachen" width="100%"></a></p>

<p align="center">
  <a href="https://github.com/Elemirus1996/BitaxeTuner/releases/latest"><b>⬇ Herunterladen</b></a> ·
  <a href="https://elemirus1996.github.io/BitaxeTuner/"><b>Webseite</b></a> ·
  <a href="https://elemirus1996.github.io/BitaxeTuner/pico-luefter/">Pico-Bauanleitung</a> ·
  <a href="#247-betrieb">24/7-Server</a> ·
  <a href="README.en.md"><b>English</b></a>
</p>

# BitaxeTuner

**Automatisches Übertakten, Benchmarken und Überwachen für Bitaxe- und NerdAxe-Miner – als Windows-Programm (WPF)
und als [24/7-Server](#247-betrieb) für Raspberry Pi, Windows oder Docker mit Browser-Oberfläche.**
*Automatic overclocking & benchmarking for Bitaxe and NerdAxe miners – [English README](README.en.md).*

BitaxeTuner erhöht Frequenz und Kernspannung deines Miners Schritt für Schritt, misst jede Kombination
(Hashrate, Leistung, Effizienz, Temperaturen, Fehlerrate) und zeigt dir am Ende die beste Einstellung –
wahlweise für **maximale Hashrate**, **beste Effizienz (J/TH)** oder einen **Kompromiss** aus beidem.
Mehrere Geräte lassen sich **parallel** testen.

> ⚠️ **Übertakten auf eigenes Risiko.** Höhere Frequenz und Spannung erhöhen Leistungsaufnahme und Temperatur
> und können die Hardware beschädigen. Prüfe, ob Netzteil und Kühlung ausreichen.

## Sprache

Desktop-App, Browser-Oberfläche, Push-Meldungen, Tagesbericht, E-Paper-Anzeige und Home Assistant gibt es auf
**Deutsch und Englisch**:

- **Desktop-App**: *Einstellungen → Sprache* (Automatisch = Windows-Sprache), wirkt nach einem Neustart der App.
- **Browser**: Knopf **DE/EN** im Kopf – jeder Browser merkt sich seine Wahl (Standard: Browser-Sprache).
- **Server** (Push, Tagesbericht, E-Paper, Statustexte): *Einstellungen → Sprache des Servers* in der Browser-Oberfläche.
  Ein Raspberry Pi steht ab Werk auf Englisch – für deutsche Meldungen dort „Deutsch“ wählen.
- Zahlen und Datum folgen dem Format des Systems, solange dessen Sprache passt.

## Funktionen

### Tuning

- **Automatischer Benchmark** pro Gerät:
  stabil → Frequenz erhöhen · instabil → Spannung erhöhen · Grenze erreicht → sofort abbrechen.
  Optional wird pro Frequenz auch die niedrigste stabile Spannung gesucht (effizienter).
- **Sicherheits-Watchdog** bei jedem Messwert: max. Chip-Temperatur, VR-Temperatur, Leistung,
  Eingangsspannung, Überhitzungs- und Spannungsfehler der Firmware. Bei Abbruch, Fehler oder Programmende
  werden die ursprünglichen (oder die beste gefundene) Einstellungen wiederhergestellt.
- **Parallelbetrieb** mehrerer Miner, **Pause/Fortsetzen** und Fortsetzen abgebrochener Läufe.
- **Live-Ansicht** mit Hashrate-/Temperaturverlauf, **Heatmap** Frequenz × Spannung, Ergebnistabelle, CSV-Export.
- **Dauertest** und **Effizienz-Ratgeber** (siehe [Bedienung](#automatik-dauertest-vergleich-handy-ansicht)).
- **Simulationsmodus**: Adresse `sim` oder `sim:<profil-id>` eingeben (z. B. `sim:nerdqaxe-plusplus`) –
  zum Ausprobieren ohne echte Hardware (läuft 30× schneller).

### Überwachung

- **Gesamt- und Einzelansicht** mit 14 Kacheln, Wallet-Guthaben (mempool.space / Blockchair), Verlauf 1 h / 24 h /
  7 / 30 Tage aus `history.db` (Hashrate, Temperatur, Leistung, Effizienz J/TH).
- **Netzwerk**: zuletzt gefundene Blöcke, Pool-Ranking, Solo-Chancen BTC/BCH.
- **Gesundheits-Frühwarnung**: täglicher Vergleich der letzten 7 Tage mit den 4 Wochen davor – Kühlung (Temperatur je
  Watt), Effizienz ohne Tuning-Änderung, Lüfter-Drehzahl, abgelehnte Shares, Verfügbarkeit.
- **Watchdog** (Neustart bei 0 Hashrate), **Firmware-Check**, **Best-Diff-Rekorde**, **Tray**, Autostart.
- **Steuer** (nach deutschem Steuerrecht, § 23 EStG): Zuflüsse mit EUR-Kurs, Verkäufe/Haltefrist (FIFO), CSV-Export,
  Stromkosten je Monat neben den Zuflüssen.

### Strom und Kosten

- **Stromtarif** brutto oder netto (mit MwSt.-Satz); optional **Stundenpreise** von aWATTar (Börsenpreis + Aufschlag)
  oder Tibber (Endpreis).
- **Smart Plugs** (Shelly Gen1, Plus/Pro/Gen3 mit Leistungsmessung): echter Verbrauch an der Steckdose inklusive
  Netzteil und Zusatzlüftern, Verlauf gegen AxeOS, Meldung bei Ausfall oder steigendem Mehrverbrauch. Es wird nur
  gemessen, **nie geschaltet**.
- **Monats- und Jahresbericht**: Verfügbarkeit, Ø Hashrate/Temperatur/Leistung, J/TH, kWh, Stromkosten und Zuflüsse –
  als CSV oder druckbare Seite (PDF), optional Monats-Push.

### Mehrere Miner

- **Automatische Geräteerkennung** (Netzwerkscan in Desktop und Browser) und passende **Geräteprofile** mit sinnvollen Grenzen.
- **Einstellungen übertragen**: Pool/Fallback und Lüfter eines Miners auf andere übernehmen – Vorschau alt → neu,
  vorher Sicherung je Miner, Worker-Name bleibt, Frequenz und Spannung werden nie übertragen.
- **Dauertest für mehrere Miner** auf einmal, **Vergleich** aller Miner nebeneinander.

### Zusammenspiel Tuning ↔ Überwachung

- **Ein zentraler Abruf** je Miner (max. eine Anfrage gleichzeitig) – Überwachung und Benchmark pollen nie doppelt.
- Während einer Tuning-Änderung, eines Benchmarks und des folgenden Neustarts **pausiert der Watchdog**
  für diesen Miner, **Offline-Meldungen** für den gewollten Neustart entfallen.
- Jede Frequenz-/Spannungsänderung wird mit Zeit, altem und neuem Wert in `history.db` (Tabelle `tuning_events`)
  protokolliert, als **Linie in allen Verlaufsdiagrammen** markiert und im Tab **Vorher/Nachher**
  (Ø 60 min davor/danach) verglichen.
- Änderungen nur nach **Bestätigung** mit aktuellem und neuem Wert; Werte außerhalb der Profilgrenzen werden abgelehnt.
  `overclockEnabled` wird nur gesetzt, wenn der Wert außerhalb der AxeOS-Auswahlliste liegt – mit Hinweis im Dialog.
- **Design** umschaltbar: dunkel oder hell.

### Miner-Logs, Alarme, Sicherungen

- **Miner-Logs** je Gerät live (`ws://<host>/api/ws`) und als Puffer (`/api/system/logs`), mit Filter, Stufen, Speichern.
- **Log-Alarme** (Einstellungen → Log-Alarme, Haken je Miner): Push bei Fehlerzeilen und frei definierbaren Mustern
  (Stratum getrennt, Overheat, Spannungsfehler, Fallback …), Sperrzeit je Regel, still während Tuning/Neustart.
  Log-Tab und Alarme teilen sich eine WebSocket-Verbindung; aktivierte Alarme belegen dauerhaft einen Platz am Miner.
- **Pool-Überwachung**: Push bei Wechsel auf den Fallback-Pool, hoher Ablehnungsquote im Zeitfenster und langsamer
  Pool-Antwort; Pool-Zeile in der Überwachungsansicht.
- **Einstellungen sichern/wiederherstellen** (Gerätekopf): vollständige Sicherung unter `snapshots\`, automatisch vor
  jedem Benchmark und jeder manuellen Änderung. Wiederherstellen feldweise mit Vorschau alt → neu, Profilgrenzen
  werden geprüft, Frequenz/Spannung werden protokolliert. Pool-Passwörter liefert AxeOS nicht aus und bleiben unverändert.

## Strom, Kosten, Berichte und Meldungen

### Push-Benachrichtigungen

*Einstellungen → Push-Benachrichtigungen* (Desktop und Browser). **Mehrere Dienste gleichzeitig**
sind möglich – z. B. ntfy für dich und Discord für eine Community-Gruppe. Je Dienst wählst du, **welche Meldungen**
(offline, Blockfund, Tagesbericht …) und **für welche Miner** (alle oder einzelne) er bekommt; „Test“ prüft jeden Dienst
einzeln. Ein bisher eingerichteter Dienst wird automatisch als erster übernommen.

| Dienst | Einzutragen |
|---|---|
| **ntfy** | Server (Standard `https://ntfy.sh`) und ein schwer zu erratendes Topic; App „ntfy“ auf dem Handy |
| **Telegram** | Bot-Token (von @BotFather) und Chat-ID |
| **Discord** | Webhook-URL eines Kanals (*Kanal-Einstellungen → Integrationen → Webhooks*) |
| **Pushover** | User-Key und App-Token (pushover.net) |
| **Eigener Webhook** | URL; BitaxeTuner sendet `POST` mit JSON `{"source":"BitaxeTuner","title":…,"message":…,"priority":"high","priorityLevel":4,"time":…}` – z. B. an Home Assistant oder n8n |

Meldungsarten je Dienst: offline, Überhitzung, Blockfund/Zufluss, Watchdog/Automatik/Firmware, Rekorde,
Log-Alarme, Pool, Smart Plugs, Gesundheit. Dazu **Tagesbericht** (Ø Hashrate, J/TH, Temperatur, Verfügbarkeit je
Miner, Stromkosten, Best-Diff-Rekord, Tuning-Änderungen, lohnende Ratgeber-Vorschläge) und **Monatsbericht** am
Monatsersten. Jede Meldung hat eine Sperrzeit, damit ein wackelnder Miner das Handy nicht flutet.

### Stromkosten

*Einstellungen → Strom und Abfrage / Strompreis* (Desktop) bzw. *Einstellungen → Allgemein / Strompreis-Quelle* (Browser):

- **Strompreis** (ct/kWh) deines Vertrags, wahlweise **brutto (inkl. MwSt.)** oder **netto (zzgl. MwSt.)** mit MwSt.-Satz
  (Standard 19 %). Gerechnet wird immer mit dem Bruttopreis.
- **Stromkosten mit Stundenpreisen** (optional): Kosten Stunde für Stunde aus Energie × Preis der Stunde.
  aWATTar liefert den Börsenpreis **netto ohne Netzentgelte und Steuern** – dafür den **Aufschlag** (typisch 15–25 ct/kWh)
  eintragen; die MwSt. kommt automatisch dazu. Tibber liefert den Endpreis, Aufschlag 0. Stunden ohne Preis rechnen
  mit dem festen Strompreis.
- Mit **Smart Plugs** zählt der Verbrauch an der Steckdose statt der AxeOS-Leistung (abschaltbar).

### Smart Plugs (Shelly)

*Einstellungen → Smart Plugs* (Desktop und Browser; in der Desktop-App führt auch *Smart Plugs …* direkt dorthin):

1. **Plug hinzufügen**, IP-Adresse (oder Name) eintragen – nur Adressen im Heimnetz. Kanal bei Steckdosen 0.
   Geschützte Shellys: Benutzer (Gen2+ immer `admin`) und Passwort; das Passwort liegt in `secrets.json`.
2. **Testen** zeigt Modell, Generation und aktuelle Leistung – auch vor dem Speichern.
3. **Rolle** wählen:
   - *speist Miner* – die angehakten Miner hängen an diesem Plug; bei mehreren wird nach AxeOS-Anteil aufgeteilt,
   - *Nebenverbraucher* – Zusatzlüfter, Pi, Router … (kommt zu den Kosten dazu),
   - *Gesamtmessung* – alles hängt dahinter; ersetzt die Summe (höchstens ein Plug).
4. **Speichern**. Die Übersicht zeigt „Leistung (Steckdose)“, den Anteil Netzteil/Nebenverbrauch und je Plug den Wert;
   ein Klick auf einen Plug öffnet den Verlauf Steckdose gegen AxeOS.

Unterstützt: Shelly Gen1 (`/status`, z. B. Plug S) und Gen2+/Gen3 (RPC `Shelly.GetStatus`, z. B. Plus Plug S, PM Mini,
Pro EM-50). Push, wenn ein Plug 5 min nicht antwortet oder der Mehrverbrauch gegenüber der Vorwoche deutlich steigt
(z. B. alterndes Netzteil). Home Assistant bekommt je Plug Leistung und Energie (Energie-Dashboard).
**BitaxeTuner schaltet die Plugs nie** – auch nicht, wenn der Server selbst am Plug hängt.

### Berichte

Browser *Berichte* bzw. Desktop *Bericht …*: Monat oder Jahr wählen. Je Miner Verfügbarkeit, Ø Hashrate, Temperatur,
Leistung, J/TH, kWh und Tuning-Änderungen; dazu Smart-Plug-Energie, Stromkosten (mit Stundenpreisen, wenn eingerichtet)
und Zuflüsse aus dem Steuer-Bereich. **Ansehen / Drucken** öffnet eine druckbare Seite (im Browser „Als PDF speichern“),
**CSV** für Excel. Abgeschlossene Monate werden in `history.db` abgelegt – Jahresberichte funktionieren auch, wenn
ältere Minutenwerte bereinigt sind. Im **Steuer-Bereich** stehen die Stromkosten je Monat neben den Zuflüssen
(keine Steuerberatung).

### Gesundheits-Frühwarnung

Einmal am Tag vergleicht BitaxeTuner je Miner die letzten 7 Tage mit den 4 Wochen davor und meldet nur deutliche
Veränderungen (je Hinweis höchstens einmal pro Woche):

| Hinweis | Bedingung |
|---|---|
| Kühlung prüfen | Temperatur je Watt +10 % und mindestens +3 °C |
| Effizienz lässt nach | J/TH +5 % ohne Tuning-Änderung im Vergleichszeitraum |
| Lüfter verliert Drehzahl | Drehzahl je % Ansteuerung −15 % |
| Mehr abgelehnte Shares | mindestens +1 Prozentpunkt und doppelt so viele |
| Häufiger offline | unter 97 % statt vorher ab 99 % |

Die Werte stehen im Browser je Miner im Tab **Gesundheit**, in der Desktop-App in der Einzelansicht. Lüfter und
abgelehnte Shares werden seit 0.7.0 aufgezeichnet – der Vergleich braucht einige Wochen Daten.

## Unterstützte Geräte

| Gerät | ASIC | Board / deviceModel | Firmware |
|---|---|---|---|
| Bitaxe Max | BM1397 | 2.2, 102 | AxeOS |
| Bitaxe Ultra | BM1366 | 0.11, 201–205, 207 | AxeOS |
| Bitaxe Supra | BM1368 | 400–403 | AxeOS |
| Bitaxe Gamma | BM1370 | 600–603 | AxeOS |
| Bitaxe Gamma Duo | 2× BM1370 | 650 | AxeOS |
| Bitaxe GT / Gamma Turbo | 2× BM1370 | 801 | AxeOS |
| Bitaxe Gamma Hex | 6× BM1370 | 1300 | AxeOS (Grenzen vorläufig) |
| Bitaxe Naja Duo | 2× BM1373 | 1201 | AxeOS (Grenzen vorläufig) |
| Bitaxe Hex / SupraHex | 6× BM1366 / BM1368 | 302–303 / 701–702 | AxeOS |
| NerdAxe / NerdAxe Gamma | BM1366 / BM1370 | NerdAxe / NerdAxeGamma | NerdQAxe-Firmware |
| NerdAxe Gaia | BM1373 | NerdAxeGaia | NerdQAxe-Firmware ≥ 1.1.0 |
| NerdQAxe+ / NerdQAxe++ | 4× BM1368 / BM1370 | NerdQAxe+ / NerdQAxe++ | NerdQAxe-Firmware |
| NerdHaxe-γ | 6× BM1370 | NerdHaxe-γ | NerdQAxe-Firmware |
| NerdOctaxe-γ / NerdOctaxe+ | 8× BM1370 / BM1368 | NerdOCTAXE-γ / NerdOCTAXE+ | NerdQAxe-Firmware |
| NerdEKO | 12× BM1370 | NerdEKO | NerdQAxe-Firmware |
| NerdQX | BM1370 | NerdQX | NerdQAxe-Firmware |
| Q1370 / Q1373 | 4× BM1370 / 4× BM1373 | Q1370 / Q1373 | NerdQAxe-Firmware (Grenzen vorläufig) |

Werte laut ESP-Miner `main/device_config.h` und NerdQAxePlus `main/boards/*.cpp` (Stand 09/2026); Quelle je Profil
steht in dessen Notiz. Meldet das Gerät Chipanzahl oder Small-Cores selbst, haben diese Angaben Vorrang.
Unbekannte Geräte mit AxeOS-kompatibler API laufen mit einem konservativen generischen Profil.
Alle Profile lassen sich über **„Profile bearbeiten“** (`profiles.json` im Datenordner) anpassen oder ergänzen.

## Installation

Beim ersten Start führt **„Erste Schritte“** durch das Wichtigste (Miner, Strompreis, Push-Dienst, Sicherung bzw.
24/7-Server) – jeder Schritt hakt sich selbst ab und springt zur passenden Einstellung. Nach einem Update fragt
BitaxeTuner einmal, ob du eine kurze **Einführung nur in die neuen Funktionen** möchtest.

1. Unter [Releases](https://github.com/Elemirus1996/BitaxeTuner/releases) die Datei `BitaxeTuner-Setup-x.y.z.exe` herunterladen.
2. Setup starten – Zielordner frei wählbar (z. B. `F:\Programme\BitaxeTuner`), Installation ohne Adminrechte möglich.
3. Alternativ: `BitaxeTuner-x.y.z-portable-win-x64.zip` entpacken und `BitaxeTuner.exe` starten.

Es wird **keine** separate .NET-Installation benötigt.

## 24/7-Betrieb

Die Miner laufen ohne PC weiter – Verlauf, Push-Meldungen, Watchdog, Automatik-Regeln, Dauertests, Benchmarks,
Tagesbericht und Steuer-Erfassung aber nur, solange BitaxeTuner läuft. Soll das rund um die Uhr passieren, ohne dass
dein PC an ist, installierst du den **BitaxeTuner-Server** auf einem Gerät, das ohnehin durchläuft:

| Gerät | Paket | Aufwand |
|---|---|---|
| Raspberry Pi 3/4/5, Zero 2 W – **fertiges SD-Image** | `BitaxeTuner-Server-x.y.z-pi-arm64.img.xz` | Raspberry Pi Imager |
| Raspberry Pi 3/4/5 (Pi OS 64-bit) | `BitaxeTuner-Server-x.y.z-linux-arm64.tar.gz` | 2 Befehle |
| Raspberry Pi mit 32-bit-System | `…-linux-arm.tar.gz` | 2 Befehle |
| Linux-PC / Mini-PC (x64) | `…-linux-x64.tar.gz` | 2 Befehle |
| Zweiter Windows-PC / Mini-PC | `BitaxeTuner-Server-Setup-x.y.z.exe` (Windows-Dienst) | Setup |
| NAS / Home-Server mit Docker | `ghcr.io/elemirus1996/bitaxetuner-server` | `docker compose up -d` |

Der Server braucht wenig: ca. 100–150 MB RAM, kaum CPU; ein Pi 3B+ reicht. history.db schreibt höchstens einen
Datensatz pro Minute und Miner (schont die SD-Karte).

**Raspberry Pi – fertiges Image (am einfachsten)**

1. `…-pi-arm64.img.xz` mit dem **Raspberry Pi Imager** schreiben („Eigenes Image“). Das Image basiert auf
   Raspberry Pi OS Lite (64-bit) und enthält den vorinstallierten Server (kein offizielles Raspberry-Pi-Produkt). Der Imager bietet für eigene Images keine Einstellungen an –
   Benutzer, WLAN und SSH trägt die Desktop-App ein (Schritt 2).
2. SD-Karte neu einstecken, in der Desktop-App *Betriebsart … → Raspberry Pi vorbereiten*: Laufwerk „bootfs“ wählen,
   Admin-Passwort festlegen, Benutzer/Passwort für den Pi und WLAN eintragen, optional *Meine Daten mitgeben*.
   Land, Zeitzone und Tastatur übernimmt die App aus Windows (änderbar); optional *SSH-Schlüssel hinterlegen* für
   Anmeldung ohne Passwort. Danach öffnet **„SSH-Terminal“** oben in der App eine Konsole auf dem Server; für einen
   schon laufenden Pi oder Linux-Server einmal *Betriebsart … → SSH-Terminal zum Server → Schlüssel übertragen …*.
   Die App schreibt die cloud-init-Dateien (`user-data`, `network-config`, `ssh`) und ein Einrichtungspaket auf die
   Karte (alle Passwörter nur als Hash) und merkt sich Adresse und Token.
3. Karte in den Pi, starten (erster Start 3–5 Minuten). Der Pi übernimmt das Paket, löscht es von der Karte und
   startet **pausiert**. In der App *Verbindung testen* → *Nur umschalten* – erst dann fragt der Pi die Miner ab.

Selbst bauen: `sudo deploy/pi-image/build-image.sh BitaxeTuner-Server-x.y.z-linux-arm64.tar.gz` (Linux/WSL; lädt das
offizielle Image und prüft dessen SHA-256). Ersteinrichtungs-Protokoll auf dem Pi: `/var/log/bitaxetuner-firstboot.log`.

**Raspberry Pi / Linux (Paket)**

```sh
tar xzf BitaxeTuner-Server-x.y.z-linux-arm64.tar.gz
cd bitaxetuner-server && sudo ./install.sh
```

`install.sh` legt einen Systembenutzer an, installiert nach `/opt/bitaxetuner`, Daten nach `/var/lib/bitaxetuner`
und richtet den systemd-Dienst `bitaxetuner` ein (Autostart, Neustart bei Absturz). Am Ende zeigt es die Adresse
und den **Einrichtungs-Code**. Protokoll: `journalctl -u bitaxetuner -f`. Entfernen: `sudo ./install.sh --uninstall`
(Daten bleiben) bzw. `--purge`. Eigene Einstellungen (Port, HTTPS) in `/etc/default/bitaxetuner`, z. B. `BITAXETUNER_PORT=8484`.

**Windows (zweiter PC)**: `BitaxeTuner-Server-Setup-x.y.z.exe` ausführen. Es richtet den Dienst „BitaxeTuner“
(Autostart, Neustart bei Fehler) und eine Firewall-Regel **nur für private Netzwerke** ein. Daten:
`C:\ProgramData\BitaxeTuner`, Einrichtungs-Code in `SETUP-CODE.txt` dort.

**Docker**: [`deploy/docker/docker-compose.yml`](deploy/docker/docker-compose.yml) herunterladen, `docker compose up -d`,
Einrichtungs-Code mit `docker compose logs bitaxetuner`. Daten im Volume `/data`.

**Einrichten**: Im Browser `http://<IP>:8484/` öffnen, Einrichtungs-Code eingeben und ein Admin-Passwort festlegen.
Danach unter *Einstellungen* Geräte, Push-Dienst usw. einrichten – oder die Daten vom PC übertragen (siehe unten).

### Zusatzlüfter, E-Paper-Anzeige und Taster (Raspberry Pi Pico)

Ein Raspberry Pi Pico (2) per USB am Server regelt bis zu sechs 4-Pin-PWM-Lüfter (5 V oder 12 V): je Miner einen
VR-Lüfter (manuell oder automatisch nach VR-Temperatur) und eine Gehäuse-Gruppe (nach VR-, ASIC- oder
Temperaturfühlern). Mehrere DS18B20 (z. B. Netzteil, Miner-Raum) werden automatisch erkannt und bekommen
je einen Namen und eine eigene Warnschwelle. Optional zeigt ein 7,5"-E-Paper (rot/schwarz/weiß) die wichtigsten Werte,
vier Taster: *Anzeige weiter/quittieren* · *Lüfter Automatik* · *100 %* (5 s halten: *Lüfter aus*) ·
*Neustart Pi + Pico* (3 s halten).
Das Pico-Programm spielt der Server selbst auf. Sicherheit: Miner offline oder Daten älter als 30 s → 100 %;
Pico ohne Befehl für 5 s → 100 %; ohne Pico läuft jeder Lüfter über die Schaltung mit voller Drehzahl.
Das E-Paper zeigt Seiten im Wechsel (Übersicht, Tagesbilanz, Verlauf 24 h, Dauertest, Pool & Netzwerk) und
Sonderanzeigen als Vollbild: **Blockfund** (bis Taste 1 oder 24 h), **Warnungen** (bis quittiert, neue Warnung zeigt
wieder), **Best-Diff-Rekord** (einmal) – alles einzeln schaltbar. Einstellungen und Vorschau jeder Seite:
Browser → *Lüfter & Anzeige*. Schaltplan, Steckbrett-Aufbau ohne Löten und Einkaufsliste
stehen in der Bauanleitung
[docs/pico-luefter](https://elemirus1996.github.io/BitaxeTuner/pico-luefter/) (Quelle: [`docs/pico-luefter/index.html`](docs/pico-luefter/index.html)).

### Home Assistant / MQTT

*Einstellungen → Home Assistant / MQTT* (Browser): Broker-Adresse (z. B. das Mosquitto-Add-on von Home Assistant),
Benutzer, Passwort. BitaxeTuner sendet Hashrate, Leistung, Effizienz, Temperaturen, Frequenz/Spannung (nur lesend),
Best Diff, Dauertest, Zusatzlüfter, Temperaturfühler und Smart Plugs (Leistung, Energie fürs Energie-Dashboard,
Leistung an der Steckdose gesamt); Home Assistant legt je Miner und für den Server automatisch
Geräte an (MQTT-Discovery, Verfügbarkeit per Last Will). Aus Home Assistant schaltbar: „Anzeige aktualisieren“ und –
nur wenn freigegeben – der Zusatzlüfter-Modus (Automatik / 100 % / Aus). **Frequenz und Spannung lassen sich über
MQTT nicht ändern.** Das Passwort liegt getrennt in `secrets.json` und geht nie in Sicherungen oder Übertragungen.

### Sicherung

Einmal täglich (Standard ab 3 Uhr) sichert BitaxeTuner Einstellungen, Verlauf (history.db), Steuerdaten,
Benchmark-Ergebnisse und Miner-Sicherungen als geprüftes Archiv (SHA-256 je Datei, integrity_check, vor dem Ablegen
einmal vollständig entpackt und geprüft). Ziele unter *Einstellungen → Sicherung* (Browser):

- **Datenordner** (`auto-backups`, immer; Standard: die letzten 7),
- **Ordner oder USB-Stick** – am Pi wird ein eingesteckter Stick (FAT32, exFAT, ext4) automatisch unter
  `/media/bitaxetuner-usb` eingebunden; auf einem bereits eingerichteten Pi einmalig
  `sudo sh /opt/bitaxetuner/current/install.sh --system` ausführen,
- **Netzlaufwerk/NAS** (SMB, ohne Einbinden ins System; Passwort getrennt in `secrets.json`, nie in Sicherungen),
- **PC holt ab**: Die Desktop-App im Modus „Server“ holt täglich eine geprüfte Sicherung in einen Ordner auf dem PC
  (*Betriebsart …*).

Alte Sicherungen werden je Ziel aufgeräumt (nur eigene Dateien). Fehler kommen als Push-Meldung.

### Bedienung: Browser oder Desktop-App

- **Browser** (PC, Handy, Tablet): Übersicht, Vergleich, je Miner Live-Werte und Verlauf mit Tuning-Markierungen,
  Benchmark, Ergebnisse, Vorher/Nachher, Gesundheit, Automatik-Regeln mit Freigabe, Dauertest, Sicherungen, Miner-Logs
  live, Smart Plugs mit Verlauf, Berichte, Steuer (Zuflüsse, Stromkosten je Monat, CSV), Einstellungen. Hell/Dunkel, handytauglich, als App zum Startbildschirm hinzufügbar.
  Frequenz/Spannung ändern sich – wie am Desktop – nur nach einem Dialog mit altem und neuem Wert und den Profilgrenzen.
- **Rollen**: *Admin* (Passwort, alles) und *Nur ansehen* (PIN, ohne IP- und Wallet-Adressen, ohne Protokolle).
- **Desktop-App** im Modus „Server“: *Betriebsart …* → Server-Adresse (oder *Im Netz suchen*) und ein **API-Token**
  (Server-Oberfläche → Einstellungen → *Desktop-App verbinden*) eintragen, *Verbindung testen*. Die App zeigt dann
  die Oberfläche des Servers an und fragt selbst **keine** Miner ab. Das Token lässt sich jederzeit widerrufen.

### Umstieg und Rückweg (Datenübertragung)

*Betriebsart …* in der Desktop-App:

- **Lokal → Server**: *Daten übertragen und umschalten* schickt `config.json`, `history.db`, Steuerdaten, Benchmark-
  Ergebnisse und Sicherungen einmalig an den Server. Geprüft wird alles (SHA-256 je Datei, `integrity_check`,
  Zeilenzahlen). Hat der Server schon Daten, wird nachgefragt und er sichert seinen Stand vorher (`backup-…`).
  Deine lokalen Daten bleiben unverändert.
- **Server → Lokal**: *Daten vom Server holen und umschalten* pausiert den Server, lädt seinen Stand, prüft ihn,
  sichert den lokalen Stand und übernimmt beim Neustart der App.
- **Nie doppelt**: Es fragt immer nur eine Seite die Miner ab. Startet die App im Modus „Lokal“, während ein bekannter
  Server dieselben Miner abfragt, fragt sie nach (umschalten oder Server pausieren). Ein pausierter Server zeigt das
  in seiner Oberfläche und lässt sich dort fortsetzen.

### Sicherheit

- Erreichbar nur aus privaten Netzen (Heimnetz, Docker-Netz, VPN wie **Tailscale**/WireGuard). Den Port **nicht** im
  Router freigeben – für unterwegs ein VPN verwenden.
- Admin-Passwort als PBKDF2-Hash, Sperre nach 5 Fehlversuchen, Sitzungs-Cookies HttpOnly/SameSite=Strict,
  CSRF-Schutz für alle Änderungen, API-Token nur als Hash gespeichert.
- Optional HTTPS mit selbst signiertem Zertifikat (`BITAXETUNER_HTTPS=1`); die Desktop-App lässt den Fingerabdruck
  beim ersten Verbinden bestätigen.

### Updates

- Server: *Einstellungen → Server-Update*. Raspberry Pi/Linux: neue Version wird neben die alte gelegt und atomar
  umgeschaltet (die alte bleibt als Rückfall in `/opt/bitaxetuner/versions`). Windows: stilles Setup, der Dienst startet neu.
  Docker: `docker compose pull && docker compose up -d`. Jede Datei wird gegen die veröffentlichte SHA-256-Prüfsumme geprüft.
- Desktop-App und Server prüfen die Versionen beim Verbinden; passen sie nicht zusammen, gibt es eine klare Meldung.

## Bedienung

1. Links die IP-Adresse des Miners eingeben (oder **„Netzwerk durchsuchen“**).
2. Profil prüfen (wird automatisch erkannt) und im Tab **Benchmark** Bereich, Messdauer und Grenzen anpassen.
3. **Benchmark starten**. Standard: 90 s Aufwärmen + 10 min Messung pro Kombination.
4. Im Tab **Ergebnisse** das Ranking wählen und die beste Einstellung anwenden.

### Automatik, Dauertest, Vergleich, Handy-Ansicht

- **Voreinstellungen** je Miner (z. B. „Hashrate“, „Effizienz“ – auch direkt aus dem letzten Benchmark).
- **Temperaturschutz**: bei anhaltender Überschreitung Frequenz stufenweise senken (Spannung bleibt), nie unter ein Minimum,
  optional schrittweise zurück, wenn der Miner wieder kühl ist.
- **Zeitplan oder Strompreis**: Voreinstellung nach Wochentag/Uhrzeit oder nach Preisschwelle.
  Preisquellen: aWATTar DE/AT (Börsenpreis, ohne Konto) oder Tibber (Endpreis, API-Token).
- Automatik-Regeln handeln **nur nach ausdrücklicher Freigabe** je Regel; jede Änderung an der Regel hebt die Freigabe auf.
  Mindestens 10 min zwischen zwei automatischen Änderungen, Pause während Benchmark/Dauertest/Wartung.
  Jede Änderung: Quelle „Automatik“ in history.db, Markierung im Verlauf, Push.
- **Dauertest** (6–48 h) der aktuellen Einstellung: Hashrate-Anteil, Fehlerrate, Temperaturen, Erreichbarkeit; übersteht
  App-Neustarts. Bei Fehler Vorschlag der nächstniedrigeren stabilen Einstellung (nur nach Bestätigung).
  **Für mehrere Miner auf einmal**: Übersicht (Browser) bzw. *Dauertest …* (Desktop) – Auswahl, eine Dauer,
  eine Bestätigung mit aktueller Einstellung je Miner; „Alle abbrechen“.
- **Effizienz-Ratgeber** (Browser → *Vergleich* → *Empfehlungen*): je Miner die beste geprüfte Einstellung für
  Effizienz, ausgewogen oder Hashrate – aus stabilen Benchmark-Ergebnissen innerhalb der Profilgrenzen, bestandene
  Dauertests zählen mehr, durchgefallene werden nie vorgeschlagen. Mit Änderung von Hashrate, Leistung und Stromkosten
  pro Monat; „Anwenden …“ bzw. „Anwenden + Dauertest 24 h …“ nur über den Bestätigungsdialog (alt → neu).
  Lohnende Vorschläge (ab 1 €/Monat) stehen auch im Tagesbericht.
- **Vergleich**: alle Miner nebeneinander – aktuelle Einstellung, 24-h-Mittel, Verfügbarkeit, beste Benchmark-Ergebnisse,
  höchste stabile Frequenz (Chip-Güte).
- **Handy-Ansicht** (Einstellungen → Handy-Ansicht): Nur-Lese-Webseite im Heimnetz, Anmeldung per PIN, nur private
  Adressbereiche, Sperre nach Fehlversuchen, keine Wallet-Adressen. Windows fragt beim ersten Start nach der
  Firewall-Freigabe – nur „Private Netzwerke“ erlauben.

### Daten

Alle Daten liegen in **einem** Ordner, Standard `%AppData%\BitaxeMonitor\` (bisheriger BitaxeMonitor-Ordner):
`config.json` (Geräte und alle Einstellungen), `history.db` (Verlauf, Tuning-Ereignisse, Dauertests, Smart-Plug-Werte,
Strompreise, Monatsberichte, Frühwarnung), `tax\*.json`, `tuning\results\*.json`, `tuning\profiles.json` und
`secrets.json` (Passwörter für NAS, MQTT und Smart Plugs – unter Windows verschlüsselt, unter Linux nur für den
Dienst lesbar, nie in Sicherungen oder Übertragungen).

- Beim ersten Start wird der Ordner vollständig nach `backup-<datum>\` gesichert (history.db über die SQLite-Backup-API).
- Geräte und Ergebnisse des früheren eigenständigen BitaxeTuner (`%LocalAppData%\BitaxeTuner`) werden einmalig
  **kopiert** – nie verschoben, nie überschrieben.
- **Einstellungen → Datenordner → Umziehen …**: kopiert alles, prüft (integrity_check, Zeilenzahlen, SHA-256) und schaltet
  erst dann um; der alte Ordner bleibt erhalten. Im Ziel wird nichts überschrieben.
- Absturzprotokoll: `crash.log` im Datenordner.

Läuft der alte BitaxeMonitor noch, warnt das Programm beim Start (sonst doppelte Abfragen und Schreibzugriffe).

## Wie wird gemessen?

- API: `GET /api/system/info`, `GET /api/system/asic`, `PATCH /api/system` (`frequency`, `coreVoltage`),
  `POST /api/system/restart` – siehe [ESP-Miner](https://github.com/bitaxeorg/ESP-Miner) und
  [ESP-Miner-NerdQAxePlus](https://github.com/shufps/ESP-Miner-NerdQAxePlus).
- Pro Kombination wird der getrimmte Mittelwert der Hashrate gebildet (Ausreißer verworfen) und mit der
  theoretischen Hashrate (`expectedHashrate` bzw. Frequenz × Small-Cores × Chips) verglichen.
  Stabil = mindestens 94 % der Soll-Hashrate und Fehlerrate unter dem Limit.
- Der Ansatz orientiert sich an [mrv777/Bitaxe-Hashrate-Benchmark](https://github.com/mrv777/Bitaxe-Hashrate-Benchmark).

## Selbst bauen

Voraussetzungen: .NET 8 SDK, optional [Inno Setup 6](https://jrsoftware.org/isinfo.php) (wird von `build.ps1` bei Bedarf per winget installiert).

```powershell
dotnet test                 # Tests
dotnet run --project src/BitaxeTuner.App
.\build.ps1                 # Tests + Desktop (Exe, ZIP, Setup) + Server (Linux-Pakete, Windows-Setup) in .\artifacts
dotnet run --project src/BitaxeTuner.Server -- --data .\serverdaten --port 8484
docker build -f deploy/docker/Dockerfile -t bitaxetuner-server .
```

Ein Release entsteht automatisch per GitHub Actions, sobald ein Tag `v*` gepusht wird (`git tag v0.1.0 && git push --tags`).

## Projektstruktur

```
src/BitaxeTuner.Core     API-Client, Geräteprofile, Benchmark-Engine, Simulator, Speicherung, I18n (Texte DE/EN),
                         Host/MinerHub (der Motor: Abfrage, Verlauf, Meldungen, Watchdog, Automatik, Benchmarks),
                         Transfer (Datenübertragung, Server-Client)
src/BitaxeTuner.App      WPF-Oberfläche (MVVM) – Betriebsart „Lokal“ (Motor im Prozess) oder „Server“
src/BitaxeTuner.Server   ASP.NET-Core-Dienst: Motor + REST-API /api/v1 + Live-Ereignisse + Browser-Oberfläche (wwwroot)
deploy/                  install.sh + systemd-Unit (Linux/Pi), Dockerfile + docker-compose.yml
tests/                   xUnit-Tests (Engine, Hub, Server-API, Datenübertragung, Übersetzungen)
installer/               Inno-Setup-Skripte (Desktop, Server-Dienst)
```

---

## English

The full English documentation is in **[README.en.md](README.en.md)**; the program itself is available in English
(desktop: *Settings → Language*, browser: **EN** button). Website: [elemirus1996.github.io/BitaxeTuner/en/](https://elemirus1996.github.io/BitaxeTuner/en/).

## Hinweise / Disclaimer

- **Keine Gewähr.** BitaxeTuner wird ohne jede Garantie bereitgestellt (GPL-3.0, Abschnitte 15–16). Übertakten,
  höhere Spannungen und eigene Lüfterschaltungen geschehen auf eigenes Risiko.
- **Unabhängiges Projekt.** BitaxeTuner ist nicht mit dem Bitaxe-Projekt (bitaxe.org), NerdAxe, Raspberry Pi Ltd,
  Home Assistant, Waveshare, Shelly, Tibber, aWATTar, Discord, Pushover oder Telegram verbunden und wird von ihnen weder unterstützt noch geprüft. Alle genannten Namen und
  Marken gehören ihren jeweiligen Inhabern und werden nur zur Beschreibung der Kompatibilität verwendet.
- **Keine Steuerberatung.** Steuer-Bereich, Zuflüsse und Stromkosten sind eine Hilfe zur Dokumentation; ob und wie
  etwas steuerlich zählt, klärt deine Steuerberatung.
- Das fertige Pi-Image basiert auf Raspberry Pi OS und ist **kein offizielles Raspberry-Pi-Produkt**; Lizenzen und
  Quelltext-Hinweise dazu stehen in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
- *No warranty. Independent project, not affiliated with or endorsed by the Bitaxe project, NerdAxe, Raspberry Pi Ltd,
  Home Assistant, Waveshare, Shelly, Tibber, aWATTar, Discord, Pushover or Telegram. All trademarks belong to their respective owners.*

## Datenschutz und Code-Signatur

- BitaxeTuner sammelt keine Daten für die Entwickler (keine Telemetrie, kein Konto, keine Cloud). Welche Dienste das
  Programm wann kontaktiert: [Datenschutz / Privacy Policy](https://elemirus1996.github.io/BitaxeTuner/privacy.html).
- Code-Signatur: Die Windows-Setups sind **noch nicht digital signiert** (daher die Windows-Warnung beim Installieren:
  „Weitere Informationen“ → „Trotzdem ausführen“). Wir arbeiten daran, das Programm signieren zu lassen. Bis dahin lässt
  sich die Echtheit über die SHA-256-Prüfsummen (`SHA256SUMS.txt` im Release) prüfen.

## Mitmachen, Fehler melden, Sicherheit

- Fehler und Wünsche: [Issues](../../issues) (Vorlagen vorhanden). Bitte **keine** IP-Adressen, Wallet-Adressen,
  Tokens oder Passwörter in Issues posten.
- Beiträge: siehe [CONTRIBUTING.md](CONTRIBUTING.md). Beiträge stehen unter derselben Lizenz (GPL-3.0).
- Sicherheitslücken bitte **nicht öffentlich**, sondern wie in [SECURITY.md](SECURITY.md) beschrieben melden.

## Lizenz

Copyright © 2026 BitaxeTuner contributors. Lizenz: **GNU GPL v3.0** – siehe [LICENSE](LICENSE).
Den Quelltext zu jeder veröffentlichten Version gibt es in diesem Repository (Tag `vX.Y.Z` bzw. „Source code“ auf der
Release-Seite). Enthaltene Komponenten anderer Urheber (u. a. .NET, SQLite, ImageSharp, SMBLibrary, MQTTnet,
DejaVu-Schriften, WebView2-SDK, Inno Setup) und Hinweise zum Raspberry-Pi- und Docker-Image: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
`LICENSE` und `THIRD-PARTY-NOTICES.txt` liegen jedem Paket bei.
