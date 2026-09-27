#!/bin/sh
# BitaxeTuner – Einrichtung beim ersten Start eines vorbereiteten Pi-Images (läuft als root, einmalig).
#  - Dienstbenutzer, Rechte (dialout für den Pico), Datenordner
#  - ICU nachinstallieren, falls es im System fehlt
#  - Einrichtungspaket der Desktop-App übernehmen (/boot/firmware/bitaxetuner): Zugangsdaten, Daten
set -u
DATA=/var/lib/bitaxetuner
ROOT=/opt/bitaxetuner
LOG=/var/log/bitaxetuner-firstboot.log
exec >>"$LOG" 2>&1
echo "== $(date) BitaxeTuner Ersteinrichtung =="

id bitaxetuner >/dev/null 2>&1 || useradd --system --home-dir "$DATA" --shell /usr/sbin/nologin bitaxetuner
getent group dialout >/dev/null 2>&1 && usermod -aG dialout bitaxetuner
mkdir -p "$DATA"

if ! ldconfig -p 2>/dev/null | grep -q libicuuc; then
    echo "ICU fehlt – installiere …"
    for i in 1 2 3 4 5 6; do
        apt-get update -qq && PKG=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -1) \
            && apt-get install -y -qq "$PKG" && break
        echo "Netz noch nicht bereit, neuer Versuch in 20 s …"
        sleep 20
    done
fi

for BOOT in /boot/firmware /boot; do
    if [ -f "$BOOT/bitaxetuner/zugang.json" ]; then
        echo "Einrichtungspaket gefunden in $BOOT/bitaxetuner"
        "$ROOT/current/BitaxeTuner.Server" --provision "$BOOT/bitaxetuner" --data "$DATA"
        break
    fi
done

chown -R bitaxetuner:bitaxetuner "$DATA" "$ROOT"
chmod 750 "$DATA"
systemctl disable bitaxetuner-firstboot.service
echo "== fertig =="
