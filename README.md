# BitaxeTuner

**Automatisches Übertakten und Benchmarken für Bitaxe- und NerdAxe-Miner – als Windows-Programm (WPF).**
*Automatic overclocking & benchmarking for Bitaxe and NerdAxe miners – English summary below.*

BitaxeTuner erhöht Frequenz und Kernspannung deines Miners Schritt für Schritt, misst jede Kombination
(Hashrate, Leistung, Effizienz, Temperaturen, Fehlerrate) und zeigt dir am Ende die beste Einstellung –
wahlweise für **maximale Hashrate**, **beste Effizienz (J/TH)** oder einen **Kompromiss** aus beidem.
Mehrere Geräte lassen sich **parallel** testen.

> ⚠️ **Übertakten auf eigenes Risiko.** Höhere Frequenz und Spannung erhöhen Leistungsaufnahme und Temperatur
> und können die Hardware beschädigen. Prüfe, ob Netzteil und Kühlung ausreichen.

## Funktionen

- **Automatischer Benchmark** pro Gerät:
  stabil → Frequenz erhöhen · instabil → Spannung erhöhen · Grenze erreicht → sofort abbrechen.
  Optional wird pro Frequenz auch die niedrigste stabile Spannung gesucht (effizienter).
- **Sicherheits-Watchdog** bei jedem Messwert: max. Chip-Temperatur, VR-Temperatur, Leistung,
  Eingangsspannung, Überhitzungs- und Spannungsfehler der Firmware. Bei Abbruch, Fehler oder Programmende
  werden die ursprünglichen (oder die beste gefundene) Einstellungen wiederhergestellt.
- **Parallelbetrieb** mehrerer Miner, **Pause/Fortsetzen** und Fortsetzen abgebrochener Läufe.
- **Live-Ansicht** mit Hashrate-/Temperaturverlauf, **Heatmap** Frequenz × Spannung, Ergebnistabelle, CSV-Export.
- **Automatische Geräteerkennung** (Netzwerkscan) und passende **Geräteprofile** mit sinnvollen Grenzen.
- **Simulationsmodus**: Adresse `sim` oder `sim:<profil-id>` eingeben (z. B. `sim:nerdqaxe-plusplus`) –
  zum Ausprobieren ohne echte Hardware (läuft 30× schneller).

### Überwachung (aus BitaxeMonitor übernommen)

- **Gesamt- und Einzelansicht** mit 14 Kacheln, Wallet-Guthaben (mempool.space / Blockchair), Verlauf 1 h / 24 h / 7 / 30 Tage
  aus `history.db`, jetzt mit viertem Diagramm **Effizienz (J/TH)**.
- **Netzwerk**: zuletzt gefundene Blöcke, Pool-Ranking, Solo-Chancen BTC/BCH.
- **Steuer**: Zuflüsse mit EUR-Kurs, Verkäufe/Haltefrist (FIFO), CSV-Export – Dateiformat unverändert.
- **Push** (ntfy/Telegram), **Watchdog**, **Firmware-Check**, **Best-Diff-Rekorde**, **Tray**, Autostart.

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

### Miner-Logs, Alarme, Sicherungen, Tagesbericht

- **Miner-Logs** je Gerät live (`ws://<host>/api/ws`) und als Puffer (`/api/system/logs`), mit Filter, Stufen, Speichern.
- **Log-Alarme** (Einstellungen → Log-Alarme, Haken je Miner): Push bei Fehlerzeilen und frei definierbaren Mustern
  (Stratum getrennt, Overheat, Spannungsfehler, Fallback …), Sperrzeit je Regel, still während Tuning/Neustart.
  Log-Tab und Alarme teilen sich eine WebSocket-Verbindung; aktivierte Alarme belegen dauerhaft einen Platz am Miner.
- **Pool-Überwachung**: Push bei Wechsel auf den Fallback-Pool, hoher Ablehnungsquote im Zeitfenster und langsamer
  Pool-Antwort; Pool-Zeile in der Überwachungsansicht.
- **Einstellungen sichern/wiederherstellen** (Gerätekopf): vollständige Sicherung unter `snapshots\`, automatisch vor
  jedem Benchmark und jeder manuellen Änderung. Wiederherstellen feldweise mit Vorschau alt → neu, Profilgrenzen
  werden geprüft, Frequenz/Spannung werden protokolliert. Pool-Passwörter liefert AxeOS nicht aus und bleiben unverändert.
- **Tagesbericht** per Push: Ø Hashrate, J/TH, Temperatur, Verfügbarkeit je Miner, Stromkosten, Best-Diff-Rekord,
  Tuning-Änderungen der letzten 24 h.

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

Werte laut ESP-Miner `main/device_config.h` und NerdQAxePlus `main/boards/*.cpp` (Stand 09/2026); Quelle je Profil
steht in dessen Notiz. Meldet das Gerät Chipanzahl oder Small-Cores selbst, haben diese Angaben Vorrang.
Unbekannte Geräte mit AxeOS-kompatibler API laufen mit einem konservativen generischen Profil.
Alle Profile lassen sich über **„Profile bearbeiten“** (`profiles.json` im Datenordner) anpassen oder ergänzen.

## Installation

1. Unter [Releases](https://github.com/Elemirus1996/BitaxeTuner/releases) die Datei `BitaxeTuner-Setup-x.y.z.exe` herunterladen.
2. Setup starten – Zielordner frei wählbar (z. B. `F:\Programme\BitaxeTuner`), Installation ohne Adminrechte möglich.
3. Alternativ: `BitaxeTuner-x.y.z-portable-win-x64.zip` entpacken und `BitaxeTuner.exe` starten.

Es wird **keine** separate .NET-Installation benötigt.

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
- **Vergleich**: alle Miner nebeneinander – aktuelle Einstellung, 24-h-Mittel, Verfügbarkeit, beste Benchmark-Ergebnisse,
  höchste stabile Frequenz (Chip-Güte).
- **Handy-Ansicht** (Einstellungen → Handy-Ansicht): Nur-Lese-Webseite im Heimnetz, Anmeldung per PIN, nur private
  Adressbereiche, Sperre nach Fehlversuchen, keine Wallet-Adressen. Windows fragt beim ersten Start nach der
  Firewall-Freigabe – nur „Private Netzwerke“ erlauben.

### Daten

Alle Daten liegen in **einem** Ordner, Standard `%AppData%\BitaxeMonitor\` (bisheriger BitaxeMonitor-Ordner):
`config.json` (Geräte und alle Einstellungen), `history.db`, `tax\*.json`, `tuning\results\*.json`, `tuning\profiles.json`.

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
.\build.ps1                 # Tests + Single-File-Exe + portable ZIP + Setup.exe in .\artifacts
```

Ein Release entsteht automatisch per GitHub Actions, sobald ein Tag `v*` gepusht wird (`git tag v0.1.0 && git push --tags`).

## Projektstruktur

```
src/BitaxeTuner.Core   API-Client, Geräteprofile, Benchmark-Engine, Simulator, Speicherung
src/BitaxeTuner.App    WPF-Oberfläche (MVVM)
tests/                 xUnit-Tests (Engine gegen simulierten Miner)
installer/             Inno-Setup-Skript
```

---

## English summary

BitaxeTuner is a Windows desktop app that automatically overclocks and benchmarks Bitaxe (Max, Ultra, Supra,
Gamma, Duo, GT, Hex, SupraHex) and NerdAxe-family miners (NerdAxe, NerdAxe Gamma, NerdQAxe+/++, NerdOctaxe).
It steps through frequency/core-voltage combinations, measures hashrate, power, efficiency and temperatures,
enforces safety limits on every sample, and recommends the best setting by max hashrate, efficiency or a
weighted balance. Multiple miners can be tuned in parallel. Download the installer from Releases.
Use at your own risk.

## Lizenz

GPL-3.0 – siehe [LICENSE](LICENSE).
