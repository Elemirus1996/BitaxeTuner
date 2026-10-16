#!/bin/sh
# BitaxeTuner (0.9.20): Lüfter des Servers (Raspberry Pi 5) – läuft als root, aber nur auf Anforderung.
#   pifan-set apply : liest <Datenordner>/pifan-request („auto“ oder „level N“) und setzt die Lüfterstufe
#                     (Thermal-Regler user_space + cur_state) bzw. gibt an die Firmware zurück (step_wise).
#   pifan-set guard : Wächter (Timer, jede Minute) – regelt BitaxeTuner, meldet sich aber länger nicht als im
#                     Lebenszeichen pifan-heartbeat erlaubt (mind. 3 min), zurück an die Firmware.
# Fehler stehen in <Datenordner>/pifan-error (für die Oberfläche), sonst wird die Datei entfernt.
set -u
DATA=${BITAXETUNER_PIFAN_TEST_DATA:-/var/lib/bitaxetuner}   # Umgebungsvariablen nur für Tests
SYS=${BITAXETUNER_PIFAN_TEST_SYS:-}
ERR="$DATA/pifan-error"

cooling=""
for d in "$SYS"/sys/class/thermal/cooling_device*; do
    [ "$(cat "$d/type" 2>/dev/null)" = "pwm-fan" ] && cooling="$d" && break
done
zone=""
for d in "$SYS"/sys/class/thermal/thermal_zone*; do
    case "$(cat "$d/type" 2>/dev/null)" in cpu-thermal|cpu_thermal) zone="$d"; break ;; esac
done
[ -n "$zone" ] || zone="$SYS/sys/class/thermal/thermal_zone0"
[ -n "$cooling" ] || exit 0          # kein Lüfter am Board: nichts zu tun

firmware() {
    echo step_wise > "$zone/policy" 2>/dev/null || true
}

fail() {
    printf '%s\n' "$1" > "$ERR"; chown bitaxetuner:bitaxetuner "$ERR" 2>/dev/null || true
    firmware
    exit 0
}

case "${1:-apply}" in
    guard)
        [ "$(cat "$zone/policy" 2>/dev/null)" = "user_space" ] || exit 0
        HB="$DATA/pifan-heartbeat"
        max=$(head -c 12 "$HB" 2>/dev/null | tr -cd '0-9')
        [ -n "$max" ] || max=180
        [ "$max" -ge 180 ] || max=180
        now=$(date +%s)
        mod=$(stat -c %Y "$HB" 2>/dev/null || echo 0)
        if [ $((now - mod)) -gt "$max" ]; then
            firmware
            printf '%s\n' "guard" > "$ERR"; chown bitaxetuner:bitaxetuner "$ERR" 2>/dev/null || true
        fi
        ;;
    *)
        req=$(head -c 20 "$DATA/pifan-request" 2>/dev/null | tr -d '\r\n')
        case "$req" in
            auto)
                firmware
                rm -f "$ERR"
                ;;
            "level "[0-9])
                n=${req#level }
                max=$(cat "$cooling/max_state" 2>/dev/null || echo 4)
                [ "$n" -le "$max" ] || n=$max
                grep -qw user_space "$zone/available_policies" 2>/dev/null || fail "nouserspace"
                echo user_space > "$zone/policy" 2>/dev/null || fail "policy"
                echo "$n" > "$cooling/cur_state" 2>/dev/null || fail "state"
                rm -f "$ERR"
                ;;
        esac
        ;;
esac
