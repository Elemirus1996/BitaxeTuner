#!/bin/sh
# BitaxeTuner: USB-Stick nach /media/bitaxetuner-usb einbinden (root, von bitaxetuner-usb@.service aufgerufen).
# Der Dienstbenutzer darf nur in den Ordner BitaxeTuner-Sicherungen schreiben; keine Programme vom Stick.
set -u
MP=/media/bitaxetuner-usb
DEV="/dev/${2:-}"
case "${1:-}" in
add)
    mountpoint -q "$MP" && exit 0            # nur ein Stick gleichzeitig
    mkdir -p "$MP"
    FS=$(blkid -o value -s TYPE "$DEV" 2>/dev/null)
    U=$(id -u bitaxetuner) || exit 1
    G=$(id -g bitaxetuner) || exit 1
    case "$FS" in
        vfat|exfat) mount -t "$FS" -o "uid=$U,gid=$G,umask=027,flush,noexec,nosuid,nodev" "$DEV" "$MP" || exit 1 ;;
        ext4) mount -t ext4 -o noexec,nosuid,nodev "$DEV" "$MP" || exit 1 ;;
        *) exit 0 ;;
    esac
    mkdir -p "$MP/BitaxeTuner-Sicherungen"
    [ "$FS" = ext4 ] && chown bitaxetuner:bitaxetuner "$MP/BitaxeTuner-Sicherungen" && chmod 750 "$MP/BitaxeTuner-Sicherungen"
    logger -t bitaxetuner "USB-Stick $DEV ($FS) eingebunden: $MP"
    ;;
remove)
    if mountpoint -q "$MP" && [ "$(findmnt -n -o SOURCE "$MP")" = "$DEV" ]; then
        umount -l "$MP" && logger -t bitaxetuner "USB-Stick $DEV entfernt"
    fi
    ;;
esac
exit 0
