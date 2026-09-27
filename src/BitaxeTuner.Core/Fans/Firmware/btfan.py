# BitaxeTuner fan controller for Raspberry Pi Pico (MicroPython).
# Installed and updated automatically by the BitaxeTuner server as main.py - do not edit on the device.
#
# Wiring (see "Pico-Lueftersteuerung" page): PWM GP0,2,4,6,8,10 -> 1k -> BC547 base (10k to GND),
# collector -> fan pin 4. The transistor inverts: GPIO low = fan PWM high = 100 %.
# Tacho GP16..21 with internal pull-up (optional 10k to 3V3).
#
# Protocol (USB serial, one command per line):
#   HELLO            -> OK BTFAN <version> <channels>
#   SET p1 .. p6     -> RPM r1 .. r6      (percent 0..100, also resets the watchdog)
#   GET              -> RPM r1 .. r6
# Without SET for WATCHDOG_MS all fans go to 100 %.

import sys
import time
import select
import machine
from machine import Pin, PWM

VERSION = "1"
PWM_PINS = (0, 2, 4, 6, 8, 10)
TACH_PINS = (16, 17, 18, 19, 20, 21)
FREQ = 25000
WATCHDOG_MS = 5000
PULSES_PER_REV = 2
DEBOUNCE_US = 1500

N = len(PWM_PINS)
pwms = []
for p in PWM_PINS:
    w = PWM(Pin(p))
    w.freq(FREQ)
    w.duty_u16(0)  # transistor off -> fan runs at 100 % (fail-safe)
    pwms.append(w)

duty = [100] * N
counts = [0] * N
last_edge = [0] * N
rpm = [0] * N


def make_handler(i):
    def handler(pin):
        now = time.ticks_us()
        if time.ticks_diff(now, last_edge[i]) > DEBOUNCE_US:
            counts[i] += 1
            last_edge[i] = now
    return handler


tachs = []
for i, p in enumerate(TACH_PINS):
    t = Pin(p, Pin.IN, Pin.PULL_UP)
    t.irq(trigger=Pin.IRQ_FALLING, handler=make_handler(i))
    tachs.append(t)


def apply(i, pct):
    if pct < 0:
        pct = 0
    if pct > 100:
        pct = 100
    duty[i] = pct
    # inverted: GPIO high share = 100 - fan percent
    pwms[i].duty_u16((100 - pct) * 65535 // 100)


def all_full():
    for i in range(N):
        apply(i, 100)


def rpm_line():
    return "RPM " + " ".join(str(r) for r in rpm)


def handle(line):
    global last_cmd, failsafe
    parts = line.strip().split()
    if not parts:
        return
    cmd = parts[0].upper()
    if cmd == "HELLO":
        print("OK BTFAN", VERSION, N)
    elif cmd == "SET":
        vals = parts[1:]
        if len(vals) != N:
            print("ERR expected", N, "values")
            return
        try:
            nums = [int(v) for v in vals]
        except ValueError:
            print("ERR not a number")
            return
        for i in range(N):
            apply(i, nums[i])
        last_cmd = time.ticks_ms()
        failsafe = False
        print(rpm_line())
    elif cmd == "GET":
        print(rpm_line())
    else:
        print("ERR unknown command")


poll = select.poll()
poll.register(sys.stdin, select.POLLIN)
buf = ""
last_cmd = time.ticks_ms()
failsafe = True
last_rpm = time.ticks_ms()

try:
    while True:
        if poll.poll(20):
            c = sys.stdin.read(1)
            if c in ("\n", "\r"):
                if buf:
                    handle(buf)
                buf = ""
            elif len(buf) < 120:
                buf += c
        now = time.ticks_ms()
        dt = time.ticks_diff(now, last_rpm)
        if dt >= 1000:
            state = machine.disable_irq()
            snap = counts[:]
            for i in range(N):
                counts[i] = 0
            machine.enable_irq(state)
            for i in range(N):
                rpm[i] = snap[i] * 60000 // (PULSES_PER_REV * dt)
            last_rpm = now
        if not failsafe and time.ticks_diff(now, last_cmd) > WATCHDOG_MS:
            all_full()
            failsafe = True
finally:
    # Ctrl-C (firmware update) or crash: fans to 100 %
    all_full()
