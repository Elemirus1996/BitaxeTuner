#!/bin/sh
# BitaxeTuner-Server installieren oder aktualisieren
# Raspberry Pi OS (64/32 bit), Debian, Ubuntu – mit systemd.
#
#   tar xzf BitaxeTuner-Server-<version>-linux-arm64.tar.gz
#   cd bitaxetuner-server && sudo ./install.sh
#
# Programm:  /opt/bitaxetuner/versions/<version>  (Symlink /opt/bitaxetuner/current → versions/active → <version>)
#            Ab 0.9.8 (Audit S9): /opt/bitaxetuner und „current“ gehören root, der Dienst darf nur versions/ beschreiben.
# Daten:     /var/lib/bitaxetuner                 (bleiben bei Update und Deinstallation erhalten)
# Dienst:    bitaxetuner.service                  (startet automatisch, Neustart bei Absturz)
# Entfernen: sudo ./install.sh --uninstall        (Daten bleiben; löschen mit --purge)
# System:    sudo sh /opt/bitaxetuner/current/install.sh --system
#            (nur Dienste, Neustart-Funktion und USB-Sicherung neu einrichten, z. B. nach einem Update per Oberfläche)
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Bitte mit sudo ausführen:  sudo ./install.sh" >&2
    exit 1
fi

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=/opt/bitaxetuner
DATA=/var/lib/bitaxetuner
UNIT=/etc/systemd/system/bitaxetuner.service
USB_RULE=/etc/udev/rules.d/99-bitaxetuner-usb.rules
USB_HELPER=/usr/local/lib/bitaxetuner/usb-mount

# Dienste, Neustart per Taste/Browser und USB-Stick für Sicherungen (alles root-eigene Dateien)
# Audit S9: Wurzel und „current“ root-eigen, nur versions/ (neue Versionen, Link „active“) gehört dem Dienst.
# Stellt auch ältere Installationen um (dort war „current“ der vom Dienst umgehängte Link).
secure_layout() {
    [ -d "$ROOT/versions" ] || return 0
    if [ ! -L "$ROOT/versions/active" ]; then
        CUR=$(readlink -f "$ROOT/current" 2>/dev/null || true)
        [ -n "$CUR" ] && [ -d "$CUR" ] || return 0
        ln -sfn "$(basename "$CUR")" "$ROOT/versions/active.tmp"
        mv -T "$ROOT/versions/active.tmp" "$ROOT/versions/active"
    fi
    ln -sfn versions/active "$ROOT/current.tmp"
    mv -T "$ROOT/current.tmp" "$ROOT/current"
    chown root:root "$ROOT"
    chown -h root:root "$ROOT/current"
    chmod 755 "$ROOT"
    chown -R bitaxetuner:bitaxetuner "$ROOT/versions"
}

install_system() {
    secure_layout
    install -m 644 "$HERE/bitaxetuner.service" "$UNIT"
    # Neustart per Taste 4 / Browser: der Dienst legt eine Anforderungsdatei ab, diese Pfad-Unit (root) startet neu
    install -m 644 "$HERE/bitaxetuner-reboot.path" /etc/systemd/system/bitaxetuner-reboot.path
    install -m 644 "$HERE/bitaxetuner-reboot.service" /etc/systemd/system/bitaxetuner-reboot.service
    # USB-Stick: udev-Regel startet bitaxetuner-usb@<gerät>, das Skript liegt root-eigen außerhalb von /opt
    install -d -m 755 /usr/local/lib/bitaxetuner /media/bitaxetuner-usb
    install -m 755 "$HERE/bitaxetuner-usb-mount.sh" "$USB_HELPER"
    install -m 644 "$HERE/bitaxetuner-usb@.service" /etc/systemd/system/bitaxetuner-usb@.service
    install -m 644 "$HERE/99-bitaxetuner-usb.rules" "$USB_RULE"
    rm -f "$DATA/reboot-request"
    systemctl daemon-reload
    udevadm control --reload-rules 2>/dev/null || true
    systemctl enable bitaxetuner bitaxetuner-reboot.path >/dev/null
    systemctl restart bitaxetuner-reboot.path
}

if [ "${1:-}" = "--system" ]; then
    install_system
    systemctl restart bitaxetuner
    echo "Systemdateien eingerichtet (Dienst, Neustart-Funktion, USB-Sicherung)."
    exit 0
fi

if [ "${1:-}" = "--uninstall" ] || [ "${1:-}" = "--purge" ]; then
    systemctl disable --now bitaxetuner bitaxetuner-reboot.path 2>/dev/null || true
    umount -l /media/bitaxetuner-usb 2>/dev/null || true
    rm -f "$UNIT" /etc/systemd/system/bitaxetuner-reboot.path /etc/systemd/system/bitaxetuner-reboot.service \
          /etc/systemd/system/bitaxetuner-usb@.service "$USB_RULE" "$USB_HELPER"
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
# Zusatzlüfter über einen Raspberry Pi Pico (USB-Seriell) – Zugriff über die Gruppe dialout
getent group dialout >/dev/null 2>&1 && usermod -aG dialout bitaxetuner
mkdir -p "$ROOT/versions" "$DATA"

TARGET="$ROOT/versions/$VERSION"
rm -rf "$TARGET.new"
mkdir -p "$TARGET.new"
for f in "$HERE"/*; do
    # alles, auch install.sh und die Systemdateien (für „install.sh --system“ nach einem Update)
    case "$(basename "$f")" in LIESMICH.txt) ;; *) cp -a "$f" "$TARGET.new/" ;; esac
done
chmod 755 "$TARGET.new/BitaxeTuner.Server"
rm -rf "$TARGET"
mv "$TARGET.new" "$TARGET"

# Atomar umschalten: alte Version bleibt als Rückfall in versions/ liegen
ln -sfn "$VERSION" "$ROOT/versions/active.tmp"
mv -T "$ROOT/versions/active.tmp" "$ROOT/versions/active"

chown -R bitaxetuner:bitaxetuner "$DATA"
chmod 750 "$DATA"
secure_layout

install_system
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
