# Mitmachen / Contributing

Danke für dein Interesse an BitaxeTuner! Fehlerberichte, Geräteprofile, Übersetzungen und Code sind willkommen.

## Grundregeln

- **Lizenz:** Beiträge werden unter der **GNU GPL v3.0** veröffentlicht (wie das Projekt). Mit einem Pull Request
  bestätigst du, dass du den Beitrag unter dieser Lizenz einbringen darfst (eigener Code oder GPL-kompatible Quelle,
  Herkunft im PR angeben).
- **Sicherheit der Hardware:** Änderungen an Frequenz/Spannung dürfen nur nach ausdrücklicher Bestätigung mit altem
  und neuem Wert passieren und müssen die Profilgrenzen prüfen. Automatiken brauchen eine eigene Freigabe.
- **Keine Daten verlieren:** Formate von `config.json`, `history.db` und den Steuerdaten nur additiv erweitern
  bzw. mit Migration ändern.
- **Keine persönlichen Daten** in Issues, Logs, Tests oder Screenshots (IP-/Wallet-Adressen, Tokens, Passwörter).

## Entwickeln

Voraussetzungen: .NET 8 SDK (Windows für die Desktop-App; Server und Tests laufen auch unter Linux).

```powershell
dotnet build BitaxeTuner.sln
dotnet test  BitaxeTuner.sln
.\build.ps1 -SkipInstaller   # Pakete wie im Release (ohne Setups)
```

Simulierte Miner: Adresse `sim` oder `sim:<profil-id>`; simulierter Pico: Port `sim`; simulierter Smart Plug: Adresse `sim`.

## Pull Requests

- Klein und fokussiert, mit Tests für neues Verhalten; `dotnet test` muss grün sein.
- Stil wie im umgebenden Code.
- **Texte zweisprachig:** Oberfläche und Meldungen werden auf Deutsch geschrieben und übersetzt – C#: `L.T("…")`,
  XAML: `{l:T '…'}`, Browser: `t('…')`; der deutsche Text ist der Schlüssel, die englische Fassung steht in
  `src/BitaxeTuner.Core/I18n/Strings.en.json`. `I18nTests` schlägt fehl, wenn eine Übersetzung fehlt oder übrig ist.
  Übersetzungen weiterer Sprachen: siehe Issue #4.
- Neue Abhängigkeiten nur mit GPL-3.0-kompatibler Lizenz und Eintrag in `THIRD-PARTY-NOTICES.txt`.

---

*English:* Contributions are welcome and are licensed under GPL-3.0. Keep changes focused, add tests, never commit
personal data, and only add dependencies with GPL-compatible licenses (listed in `THIRD-PARTY-NOTICES.txt`).
User-facing texts are written in German and translated (`L.T`, `{l:T}`, `t()`; English in `Strings.en.json`);
the tests fail when a translation is missing. Corrections to the English texts are very welcome.
