#!/bin/sh
# BitaxeTuner-Server installieren oder aktualisieren
# Raspberry Pi OS (64/32 bit), Debian, Ubuntu – mit systemd.
#
#   tar xzf BitaxeTuner-Server-<version>-linux-arm64.tar.gz
#   cd bitaxetuner-server && sudo ./install.sh
#
# Programm:  /opt/bitaxetuner/versions/<version>  (Symlink /opt/bitaxetuner/current)
# Daten:     /var/lib/bitaxetuner                 (bleiben bei Update und Deinstallation erhalten)
# Dienst:    bitaxetuner.service                  (startet automatisch, Neustart bei Absturz)
# Entfernen: sudo ./install.sh --uninstall        (Daten bleiben; löschen mit --purge)
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Bitte mit sudo ausführen:  sudo ./install.sh" >&2
    exit 1
fi

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=/opt/bitaxetuner
DATA=/var/lib/bitaxetuner
UNIT=/etc/systemd/system/bitaxetuner.service

if [ "${1:-}" = "--uninstall" ] || [ "${1:-}" = "--purge" ]; then
    systemctl disable --now bitaxetuner 2>/dev/null || true
    rm -f "$UNIT"
    systemctl daemon-reload
    rm -rf "$ROOT"
    if [ "$1" = "--purge" ]; then
        rm -rf "$DATA"
        userdel bitaxetuner 2>/dev/null || true
        echo "BitaxeTuner-Server und alle Daten entfernt."
    else
        echo "BitaxeTuner-Server entfernt. Daten liegen weiter in $DATA."
    fi
    exit 0
fi

VERSION=$(cat "$HERE/VERSION")
echo "== BitaxeTuner-Server $VERSION installieren =="

# .NET braucht ICU (Länderformate, z. B. Zahlen mit Komma)
if ! ldconfig -p 2>/dev/null | grep -q libicuuc; then
    echo "Installiere ICU (libicu) …"
    apt-get update -qq
    PKG=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -1)
    apt-get install -y -qq "${PKG:-libicu-dev}"
fi

id bitaxetuner >/dev/null 2>&1 || useradd --system --home-dir "$DATA" --shell /usr/sbin/nologin bitaxetuner
mkdir -p "$ROOT/versions" "$DATA"

TARGET="$ROOT/versions/$VERSION"
rm -rf "$TARGET.new"
mkdir -p "$TARGET.new"
for f in "$HERE"/*; do
    case "$(basename "$f")" in install.sh|bitaxetuner.service|LIESMICH.txt) ;; *) cp -a "$f" "$TARGET.new/" ;; esac
done
chmod 755 "$TARGET.new/BitaxeTuner.Server"
rm -rf "$TARGET"
mv "$TARGET.new" "$TARGET"

# Atomar umschalten: alte Version bleibt als Rückfall in versions/ liegen
ln -sfn "$TARGET" "$ROOT/current.tmp"
mv -T "$ROOT/current.tmp" "$ROOT/current"

chown -R bitaxetuner:bitaxetuner "$ROOT" "$DATA"
chmod 750 "$DATA"

install -m 644 "$HERE/bitaxetuner.service" "$UNIT"
systemctl daemon-reload
systemctl enable bitaxetuner >/dev/null
systemctl restart bitaxetuner

PORT=8484
if [ -f /etc/default/bitaxetuner ]; then
    P=$(sed -n 's/^BITAXETUNER_PORT=//p' /etc/default/bitaxetuner | tail -1)
    [ -n "$P" ] && PORT=$P
fi
IP=$(hostname -I 2>/dev/null | awk '{print $1}')

i=0
while [ $i -lt 20 ] && ! systemctl is-active --quiet bitaxetuner; do sleep 1; i=$((i + 1)); done
sleep 2

echo
if systemctl is-active --quiet bitaxetuner; then
    echo "Der Dienst läuft.  Oberfläche:  http://${IP:-<IP-des-Pi>}:$PORT/"
else
    echo "Der Dienst ist nicht gestartet. Protokoll:  journalctl -u bitaxetuner -n 50" >&2
fi
if [ -f "$DATA/SETUP-CODE.txt" ]; then
    echo
    cat "$DATA/SETUP-CODE.txt"
fi
echo
echo "Protokoll:   journalctl -u bitaxetuner -f"
echo "Neustart:    sudo systemctl restart bitaxetuner"
echo "Updates:     in der Oberfläche unter Einstellungen → Server-Update"
