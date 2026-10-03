# Sicherheit / Security

BitaxeTuner steuert Miner (Frequenz, Spannung, Neustarts) und kann als Server dauerhaft im Heimnetz laufen.
Sicherheitsprobleme nehmen wir deshalb ernst.

## Lücke melden

Bitte **nicht** als öffentliches Issue. Stattdessen über GitHub:
**Security → „Report a vulnerability“** (private Meldung an die Maintainer).

Hilfreich sind: betroffene Version, Betriebsart (Desktop, Server auf Pi/Windows/Docker), Schritte zum Nachstellen
und die mögliche Auswirkung. Bitte keine echten Wallet-Adressen, Tokens oder Passwörter mitschicken.

Wir melden uns in der Regel innerhalb einer Woche und veröffentlichen eine Korrektur mit Hinweis im Release.

## Unterstützte Versionen

Sicherheitskorrekturen gibt es für die jeweils neueste Version. Bitte vor einer Meldung auf die aktuelle Version
aktualisieren (Desktop: *Nach Updates suchen*, Server: *Einstellungen → Server-Update*).

## Hinweise zum Betrieb

- Den Server **nicht** per Portfreigabe ins Internet stellen; für den Zugriff von unterwegs ein VPN verwenden
  (z. B. Tailscale oder WireGuard). Der Server nimmt nur Anfragen aus privaten Netzen an.
- API-Tokens nur an vertrauenswürdige Geräte geben; nicht mehr benötigte Tokens widerrufen.
- Passwörter für Netzlaufwerk, MQTT und Smart Plugs liegen getrennt in `secrets.json` (Windows: verschlüsselt,
  Linux: nur für den Dienst lesbar) und gehen nie in Sicherungen, Übertragungen oder die API-Antworten.
- Smart Plugs und Push-Webhooks: BitaxeTuner fragt nur Adressen im Heimnetz ab bzw. sendet nur an die eingetragene
  Adresse; Plugs werden nie geschaltet. Shelly Gen1 überträgt ein Plug-Passwort unverschlüsselt (Basic-Auth) – für
  geschützte Plugs Shelly Plus/Gen2+ (Digest) verwenden.
- **Reverse-Proxy / Docker:** Der Heimnetz-Filter prüft die Adresse, von der die Verbindung kommt. Hinter einem Proxy auf
  demselben Rechner oder mit Dockers Userland-Proxy sieht er nur den Proxy. Dann den Proxy mit `--trusted-proxy <IP>`
  bzw. `BITAXETUNER_TRUSTED_PROXIES` eintragen – nur von dort wird `X-Forwarded-For` übernommen. Adressen aus 100.64.0.0/10
  (z. B. Tailscale) gelten als privat.
- **Programmordner (Linux):** `/opt/bitaxetuner` und der Link `current` gehören root; der Dienst darf nur
  `/opt/bitaxetuner/versions` beschreiben (für das Ein-Klick-Update, jedes Update ist signiert geprüft). Bewusster
  Kompromiss: Wer Code im Dienst ausführen kann, kann dort eine Version ablegen. `sudo sh …/install.sh --system` nur nach
  einem geprüften Update ausführen.
- **Kiosk-Links** sind lange gültige Schlüssel für die Ansicht (nur Ansehen): nur auf Geräten im eigenen Haushalt
  öffnen und nicht mehr benötigte Links widerrufen.

---

*English:* Please report vulnerabilities privately via **Security → Report a vulnerability** on GitHub, not as a
public issue. Fixes are provided for the latest release. Do not expose the server to the internet; use a VPN.
