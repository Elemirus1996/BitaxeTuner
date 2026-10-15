#!/bin/sh
# BitaxeTuner: WLAN-Name und -Passwort des Servers für die Pico-Einrichtung lesen (0.9.17).
# Läuft als root, aber nur auf Anforderung des Dienstes (Pfad-Unit bitaxetuner-wifi.path, Datei wifi-request) und
# liest ausschließlich das gerade aktive WLAN. Die Antwort ist nur für den Dienst lesbar; der Dienst liest und löscht
# sie sofort und schickt das Passwort nur an den Pico (nie angezeigt, nie gespeichert).
set -u
DATA=${BITAXETUNER_WIFI_TEST_DIR:-/var/lib/bitaxetuner}   # Umgebungsvariable nur für Tests
OUT="$DATA/wifi-answer.json"
rm -f "$DATA/wifi-request" "$OUT"
umask 077

esc() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

ssid=""
psk=""
err=""
if command -v nmcli >/dev/null 2>&1; then
    # NetworkManager (Raspberry Pi OS ab Bookworm): verbundenes WLAN-Gerät → Verbindung → SSID und Schlüssel
    dev=$(nmcli -t -f DEVICE,TYPE,STATE device 2>/dev/null | awk -F: '$2=="wifi" && $3=="connected" {print $1; exit}')
    if [ -n "$dev" ]; then
        con=$(nmcli -g GENERAL.CONNECTION device show "$dev" 2>/dev/null)
        if [ -n "$con" ]; then
            ssid=$(nmcli -s -g 802-11-wireless.ssid connection show "$con" 2>/dev/null)
            psk=$(nmcli -s -g 802-11-wireless-security.psk connection show "$con" 2>/dev/null)
        fi
    fi
fi
if [ -z "$ssid" ] && command -v iwgetid >/dev/null 2>&1 && [ -r /etc/wpa_supplicant/wpa_supplicant.conf ]; then
    # Ältere Systeme mit wpa_supplicant: Block des verbundenen Netzes
    ssid=$(iwgetid -r 2>/dev/null || true)
    if [ -n "$ssid" ]; then
        psk=$(awk -v want="$ssid" '
            /network=\{/ { s=""; p=""; inblock=1; next }
            inblock && /^[ \t]*ssid=/ { v=$0; sub(/^[ \t]*ssid=/, "", v); gsub(/^"|"$/, "", v); s=v }
            inblock && /^[ \t]*psk=/ { v=$0; sub(/^[ \t]*psk=/, "", v); gsub(/^"|"$/, "", v); p=v }
            inblock && /\}/ { if (s == want) { print p; exit } inblock=0 }
        ' /etc/wpa_supplicant/wpa_supplicant.conf)
    fi
fi
[ -n "$ssid" ] || err="nowifi"     # kein WLAN verbunden (Server per Kabel im Netz?)
[ -z "$err" ] && [ -z "$psk" ] && err="nopsk"   # offenes Netz, Enterprise-WLAN oder Schlüssel nicht lesbar

TMP="$OUT.tmp"
printf '{"ssid":"%s","psk":"%s","error":"%s"}\n' "$(esc "$ssid")" "$(esc "$psk")" "$(esc "$err")" > "$TMP"
chown bitaxetuner:bitaxetuner "$TMP" 2>/dev/null || true
chmod 600 "$TMP"
mv -f "$TMP" "$OUT"
