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

## Unterstützte Geräte

| Gerät | ASIC | Firmware |
|---|---|---|
| Bitaxe Max | BM1397 | AxeOS |
| Bitaxe Ultra | BM1366 | AxeOS |
| Bitaxe Supra | BM1368 | AxeOS |
| Bitaxe Gamma | BM1370 | AxeOS |
| Bitaxe Gamma Duo | 2× BM1370 | AxeOS (vorläufige Grenzen) |
| Bitaxe GT / Gamma Turbo | 2× BM1370 | AxeOS |
| Bitaxe Hex / SupraHex | 6× BM1366 / BM1368 | AxeOS |
| NerdAxe / NerdAxe Gamma | BM1366 / BM1370 | NerdQAxe-Firmware |
| NerdQAxe+ / NerdQAxe++ | 4× BM1368 / BM1370 | NerdQAxe-Firmware |
| NerdOctaxe | 8× BM1370 | NerdQAxe-Firmware |

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

Ergebnisse werden nach jedem Schritt als JSON gespeichert (Standard: `%LocalAppData%\BitaxeTuner\results`,
über **„Datenordner …“** änderbar).

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
