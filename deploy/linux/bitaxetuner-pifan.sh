#!/bin/sh
# BitaxeTuner (0.9.20): Lüfter des Servers (Raspberry Pi 5) – läuft als root, aber nur auf Anforderung.
#   pifan-set apply : liest <Datenordner>/pifan-request („auto“ oder „level N“) und setzt die Lüfterstufe bzw. gibt an
#                     die Firmware zurück (step_wise, Werks-Schaltpunkte).
#                     0.9.24: Raspberry Pi OS hat keinen Regler „user_space“. Stattdessen werden die aktiven Schaltpunkte
#                     der CPU-Thermalzone verschoben: für Stufe N die Schaltpunkte 1…N auf 1…N °C (immer überschritten),
#                     die übrigen auf 96…99 °C – die Firmware (step_wise) fährt dann genau Stufe N an. Der kritische
#                     Schaltpunkt (110 °C) bleibt unberührt. Werkswerte liegen solange in <Datenordner>/pifan-trips.
#                     Nur wenn das nicht geht: Regler user_space + cur_state (andere Kernel).
#   pifan-set guard : Wächter (Timer, jede Minute) – regelt BitaxeTuner, meldet sich aber länger nicht als im
#                     Lebenszeichen pifan-heartbeat erlaubt (mind. 3 min), zurück an die Firmware.
# Fehler stehen in <Datenordner>/pifan-error (für die Oberfläche), sonst wird die Datei entfernt.
set -u
DATA=${BITAXETUNER_PIFAN_TEST_DATA:-/var/lib/bitaxetuner}   # Umgebungsvariablen nur für Tests
SYS=${BITAXETUNER_PIFAN_TEST_SYS:-}
ERR="$DATA/pifan-error"
TRIPS="$DATA/pifan-trips"

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

# Aktive Schaltpunkte (Nummern) der CPU-Zone, in Reihenfolge
active_trips() {
    for i in 0 1 2 3 4 5 6 7 8 9; do
        [ "$(cat "$zone/trip_point_${i}_type" 2>/dev/null)" = "active" ] && echo "$i"
    done
}

# Werkswerte einmal sichern (bleiben bis zur Rückgabe an die Firmware)
save_trips() {
    [ -f "$TRIPS" ] && return 0
    : > "$TRIPS.tmp" || return 1
    for i in $(active_trips); do
        printf '%s %s\n' "$i" "$(cat "$zone/trip_point_${i}_temp")" >> "$TRIPS.tmp" || return 1
    done
    [ -s "$TRIPS.tmp" ] || { rm -f "$TRIPS.tmp"; return 1; }
    mv "$TRIPS.tmp" "$TRIPS" && chmod 644 "$TRIPS"
}

restore_trips() {
    [ -f "$TRIPS" ] || return 0
    while read -r i t; do
        echo "$t" > "$zone/trip_point_${i}_temp" 2>/dev/null || true
    done < "$TRIPS"
    rm -f "$TRIPS"
}

# Stufe N über die Schaltpunkte; Fehler (nur lesbar) → 1
level_by_trips() {
    n=$1
    [ -n "$(active_trips)" ] || return 1
    save_trips || return 1
    k=0
    for i in $(active_trips); do
        k=$((k + 1))
        if [ "$k" -le "$n" ]; then t=$((k * 1000)); else t=$(((95 + k) * 1000)); fi
        echo "$t" > "$zone/trip_point_${i}_temp" 2>/dev/null || { restore_trips; return 1; }
        [ "$(cat "$zone/trip_point_${i}_temp" 2>/dev/null)" = "$t" ] || { restore_trips; return 1; }
    done
    echo step_wise > "$zone/policy" 2>/dev/null || true
    return 0
}

firmware() {
    restore_trips
    echo step_wise > "$zone/policy" 2>/dev/null || true
}

fail() {
    printf '%s\n' "$1" > "$ERR"; chown bitaxetuner:bitaxetuner "$ERR" 2>/dev/null || true
    firmware
    exit 0
}

case "${1:-apply}" in
    guard)
        [ "$(cat "$zone/policy" 2>/dev/null)" = "user_space" ] || [ -f "$TRIPS" ] || exit 0
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
                if level_by_trips "$n"; then
                    rm -f "$ERR"
                    exit 0
                fi
                grep -qw user_space "$zone/available_policies" 2>/dev/null || fail "nouserspace"
                echo user_space > "$zone/policy" 2>/dev/null || fail "policy"
                echo "$n" > "$cooling/cur_state" 2>/dev/null || fail "state"
                rm -f "$ERR"
                ;;
        esac
        ;;
esac
