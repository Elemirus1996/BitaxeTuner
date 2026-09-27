#!/bin/bash
# Baut ein fertiges SD-Karten-Image: Raspberry Pi OS Lite 64-bit + BitaxeTuner-Server (Pi 3/4/5, Zero 2 W).
#
#   sudo deploy/pi-image/build-image.sh artifacts/BitaxeTuner-Server-<v>-linux-arm64.tar.gz [ausgabe.img.xz]
#
# Ablauf: offizielles Image laden und per SHA-256 prüfen → Root-Partition um 1 GiB vergrößern → Server nach
# /opt/bitaxetuner/versions/<v> (Symlink current) → Dienst, Neustart-Pfad-Unit und Ersteinrichtung aktivieren →
# Hostname "bitaxetuner" → komprimieren. Benutzer, WLAN und SSH stellt man beim Schreiben im Raspberry Pi Imager ein.
# Benötigt: curl, xz, parted, e2fsprogs, losetup (util-linux), sha256sum.
set -euo pipefail

PKG=${1:?Aufruf: build-image.sh <BitaxeTuner-Server-...-linux-arm64.tar.gz> [ausgabe.img.xz]}
[ "$(id -u)" -eq 0 ] || { echo "Bitte als root ausführen (sudo)." >&2; exit 1; }
HERE=$(cd "$(dirname "$0")" && pwd)
WORK=${WORK:-$(mktemp -d /tmp/bt-image.XXXX)}
BASE_URL=${BASE_URL:-https://downloads.raspberrypi.com/raspios_lite_arm64_latest}
HOSTNAME_NEW=${HOSTNAME_NEW:-bitaxetuner}

cleanup() {
    set +e
    umount "$WORK/root/boot/firmware" 2>/dev/null
    umount "$WORK/root" 2>/dev/null
    [ -n "${LOOP_BOOT:-}" ] && losetup -d "$LOOP_BOOT" 2>/dev/null
    [ -n "${LOOP_ROOT:-}" ] && losetup -d "$LOOP_ROOT" 2>/dev/null
}
trap cleanup EXIT

echo "== Paket prüfen =="
mkdir -p "$WORK/pkg"
tar -xzf "$PKG" -C "$WORK/pkg"
SRC="$WORK/pkg/bitaxetuner-server"
[ -x "$SRC/BitaxeTuner.Server" ] || chmod 755 "$SRC/BitaxeTuner.Server"
VERSION=$(tr -d '\r\n' < "$SRC/VERSION")
OUT=${2:-$(pwd)/BitaxeTuner-Server-$VERSION-raspios-arm64.img.xz}
echo "BitaxeTuner-Server $VERSION"

echo "== Raspberry Pi OS Lite (64-bit) laden =="
URL=$(curl -sIL -o /dev/null -w '%{url_effective}' "$BASE_URL")
echo "$URL"
curl -fL --retry 3 -o "$WORK/os.img.xz" "$URL"
EXPECTED=$(curl -fsL "$URL.sha256" | awk '{print $1}')
ACTUAL=$(sha256sum "$WORK/os.img.xz" | awk '{print $1}')
[ "$EXPECTED" = "$ACTUAL" ] || { echo "Prüfsumme des Raspberry-Pi-OS-Images stimmt nicht!" >&2; exit 1; }
echo "Prüfsumme in Ordnung"
xz -d -T0 "$WORK/os.img.xz"
IMG="$WORK/os.img"

echo "== Root-Partition vergrößern =="
truncate -s +1G "$IMG"
parted -s "$IMG" resizepart 2 100%
# Partitionen über ihre Lage im Image einbinden (ohne Partitions-Scan: auf CI-Servern erscheinen
# /dev/loopXpN oft nicht oder zu spät)
part_loop() {
    set -- $(partx -g -o START,SECTORS -n "$1" "$IMG")
    losetup --show -f -o $(( $1 * 512 )) --sizelimit $(( $2 * 512 )) "$IMG"
}
LOOP_BOOT=$(part_loop 1)
LOOP_ROOT=$(part_loop 2)
# e2fsck: 0 = sauber, 1/2 = repariert, ab 4 = Fehler
rc=0; e2fsck -fy "$LOOP_ROOT" || rc=$?
[ "$rc" -lt 4 ] || { echo "Dateisystem des Images defekt (e2fsck $rc)" >&2; exit 1; }
resize2fs "$LOOP_ROOT"
mkdir -p "$WORK/root"
mount "$LOOP_ROOT" "$WORK/root"
mount "$LOOP_BOOT" "$WORK/root/boot/firmware"

echo "== BitaxeTuner-Server installieren =="
R="$WORK/root"
install -d "$R/opt/bitaxetuner/versions/$VERSION" "$R/var/lib/bitaxetuner"
for f in "$SRC"/*; do
    case "$(basename "$f")" in LIESMICH.txt) ;; *) cp -a "$f" "$R/opt/bitaxetuner/versions/$VERSION/" ;; esac
done
chmod 755 "$R/opt/bitaxetuner/versions/$VERSION/BitaxeTuner.Server"
ln -sfn "/opt/bitaxetuner/versions/$VERSION" "$R/opt/bitaxetuner/current"
install -m 755 "$SRC/bitaxetuner-firstboot.sh" "$R/opt/bitaxetuner/bitaxetuner-firstboot.sh"
for u in bitaxetuner.service bitaxetuner-reboot.path bitaxetuner-reboot.service bitaxetuner-firstboot.service bitaxetuner-usb@.service; do
    install -m 644 "$SRC/$u" "$R/etc/systemd/system/$u"
done
# USB-Stick für Sicherungen
install -d -m 755 "$R/usr/local/lib/bitaxetuner" "$R/media/bitaxetuner-usb"
install -m 755 "$SRC/bitaxetuner-usb-mount.sh" "$R/usr/local/lib/bitaxetuner/usb-mount"
install -m 644 "$SRC/99-bitaxetuner-usb.rules" "$R/etc/udev/rules.d/99-bitaxetuner-usb.rules"
# Dienste aktivieren (wie "systemctl enable", ohne das Zielsystem zu starten)
install -d "$R/etc/systemd/system/multi-user.target.wants"
for u in bitaxetuner.service bitaxetuner-reboot.path bitaxetuner-firstboot.service; do
    ln -sfn "/etc/systemd/system/$u" "$R/etc/systemd/system/multi-user.target.wants/$u"
done

echo "== Hostname $HOSTNAME_NEW =="
echo "$HOSTNAME_NEW" > "$R/etc/hostname"
sed -i "s/127\.0\.1\.1.*/127.0.1.1\t$HOSTNAME_NEW/" "$R/etc/hosts"

install -d "$R/boot/firmware/bitaxetuner"
# Lizenzhinweise (GPL-3.0, Drittanbieter, Quelltexte von Raspberry Pi OS) auch auf der Boot-Partition lesbar
for f in LICENSE THIRD-PARTY-NOTICES.txt; do [ -f "$SRC/$f" ] && install -m 644 "$SRC/$f" "$R/boot/firmware/bitaxetuner/$f"; done
cat > "$R/boot/firmware/bitaxetuner/LIESMICH.txt" <<'TXT'
BitaxeTuner-Server – vorbereitetes Raspberry-Pi-OS-Image

Optional vor dem ersten Start: In der BitaxeTuner-Desktop-App unter "Betriebsart" -> "Raspberry Pi vorbereiten"
diesen Ordner auswählen. Die App legt hier Zugangsdaten (nur Hashes) und auf Wunsch deine Daten ab.
Der Pi übernimmt sie beim ersten Start und löscht sie danach.

Ohne Einrichtungspaket: http://bitaxetuner.local:8484/ im Browser öffnen, Einrichtungs-Code:
  ssh <benutzer>@bitaxetuner.local  und  sudo cat /var/lib/bitaxetuner/SETUP-CODE.txt
TXT

sync
umount "$R/boot/firmware"
umount "$R"
losetup -d "$LOOP_BOOT" "$LOOP_ROOT"
LOOP_BOOT= LOOP_ROOT=

echo "== Komprimieren =="
xz -T0 -6 -c "$IMG" > "$OUT"
sha256sum "$OUT" | awk '{print $1}' > "$OUT.sha256"
ls -la "$OUT"
echo "Fertig: $OUT"
