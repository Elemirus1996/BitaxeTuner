# BitaxeTuner accessory controller for Raspberry Pi Pico / Pico 2 (MicroPython).
# Installed and updated automatically by the BitaxeTuner server as main.py - do not edit on the device.
#
# Fans (see "Pico-Lueftersteuerung" page): PWM GP0,2,4,6,8,10 -> 1k -> BC547 base (10k to GND),
# collector -> fan pin 4. The transistor inverts: GPIO low = fan PWM high = 100 %.
# Tacho GP16..21 with internal pull-up.
# Buttons GP1,3,5,7 to GND (internal pull-up). Onboard LED blinks on every press.
# e-Paper 7.5" B (800x480, black/white/red, UC8179): DIN GP15, CLK GP14, CS GP13, DC GP12, RST GP11, BUSY GP22.
# Temperatures: DS18B20 on GP26 (1-Wire, one 4.7k pull-up to 3V3); several sensors in parallel on one wire,
# each reported with its unique 64-bit ROM id (so names like "PSU" / "miner room" never mix up).
#
# Protocol (USB serial, one command per line). Fan watchdog: only SET/GET/HELLO count as a sign of life of the fan
# control; other lines (e.g. image data) keep only the "server lost" display timer alive.
# Hardware watchdog (machine.WDT, 8 s): if this program hangs, the Pico restarts; on start all fans run at 100 %.
#   HELLO                -> OK BTFAN <version> <channels>
#   SET p1 .. p6         -> RPM r1 .. r6 [T id=t id=t ..]   (fan percent 0..100; T = DS18B20 ROM id (hex) = deg C)
#   GET                  -> RPM r1 .. r6 [T id=t id=t ..]
#   IMG <bytes>          -> OK IMG             (then D-lines with base64 data, 1st half black plane, 2nd half red plane)
#   D <base64>           -> (no reply)
#   SHOW                 -> OK SHOW | ERR ...  (refresh runs in the background, ~16 s)
#   RESET                -> OK RESET, then the Pico restarts
# Unsolicited: BTN <n> (short press 1-3), BTN 3 LONG (held 5 s), BTN 4 LONG (held 3 s), EPD DONE (refresh finished).
# Button 4 has no short press. Without any command for WATCHDOG_MS all fans go to 100 %.
# Button 3 (short) sets 100 % locally even without server.

import sys
import time
import select
import machine
import ubinascii
from machine import Pin, PWM

VERSION = "6"
PWM_PINS = (0, 2, 4, 6, 8, 10)
TACH_PINS = (16, 17, 18, 19, 20, 21)
BUTTON_PINS = (1, 3, 5, 7)
FREQ = 25000
WATCHDOG_MS = 5000
PULSES_PER_REV = 2
DEBOUNCE_US = 1500
# Hold time for a long press per button in ms (0 = no long press): button 3 = fans off, button 4 = reboot
LONG_MS = (0, 0, 5000, 3000)
EPD_W = 800
EPD_H = 480
PLANE = EPD_W * EPD_H // 8
SERVER_LOST_MS = 10 * 60 * 1000

N = len(PWM_PINS)

# ---------- Fans ----------

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
    pwms[i].duty_u16((100 - pct) * 65535 // 100)


def all_full():
    for i in range(N):
        apply(i, 100)


def rpm_line():
    line = "RPM " + " ".join(str(r) for r in rpm)
    if temps:
        line += " T " + " ".join(i + "=" + str(t) for i, t in temps)
    return line

# ---------- DS18B20 case temperature ----------

temps = []
ds = None
roms = []
ds_state = 0          # 0 idle, 1 converting
ds_started = 0
ds_scanned = -60000
try:
    import onewire
    import ds18x20
    ds = ds18x20.DS18X20(onewire.OneWire(Pin(26)))
except Exception:
    ds = None


def poll_temps(now):
    global roms, ds_state, ds_started, ds_scanned, temps
    if ds is None:
        return
    try:
        if ds_state == 0:
            if time.ticks_diff(now, ds_scanned) >= 60000:
                roms = ds.scan()
                ds_scanned = now
                if not roms:
                    temps = []
            if roms and time.ticks_diff(now, ds_started) >= 2000:
                ds.convert_temp()
                ds_started = now
                ds_state = 1
        elif time.ticks_diff(now, ds_started) >= 800:
            vals = []
            for r in roms:
                try:
                    t = ds.read_temp(r)
                except Exception:
                    continue  # one faulty sensor must not hide the others
                # 85.0 is the power-on value of a sensor that did not convert
                if t is not None and -40 < t < 120 and t != 85.0:
                    vals.append(("".join("%02x" % b for b in r), round(t, 1)))
            temps = vals
            ds_state = 0
    except Exception:
        temps = []
        roms = []
        ds_state = 0

# ---------- LED and buttons ----------

try:
    led = Pin("LED", Pin.OUT)
except Exception:
    led = Pin(25, Pin.OUT)
led_off_at = 0

buttons = [Pin(p, Pin.IN, Pin.PULL_UP) for p in BUTTON_PINS]
btn_down = [0] * len(buttons)      # ticks when pressed, 0 = released
btn_long_sent = [False] * len(buttons)


def blink(ms=150):
    global led_off_at
    led.value(1)
    led_off_at = time.ticks_add(time.ticks_ms(), ms)


def poll_buttons(now):
    for i, b in enumerate(buttons):
        pressed = b.value() == 0
        if pressed and btn_down[i] == 0:
            btn_down[i] = now
            btn_long_sent[i] = False
        elif pressed and LONG_MS[i] and not btn_long_sent[i] and time.ticks_diff(now, btn_down[i]) >= LONG_MS[i]:
            btn_long_sent[i] = True
            blink(1000)
            print("BTN", i + 1, "LONG")
        elif not pressed and btn_down[i] != 0:
            held = time.ticks_diff(now, btn_down[i])
            btn_down[i] = 0
            if not btn_long_sent[i] and 30 <= held and i != 3 and (not LONG_MS[i] or held < LONG_MS[i]):
                blink()
                if i == 2:
                    all_full()  # 100 % sofort, auch ohne Server
                print("BTN", i + 1)

# ---------- e-Paper 7.5" B V2 ----------

epd_spi = None
epd_cs = Pin(13, Pin.OUT, value=1)
epd_dc = Pin(12, Pin.OUT, value=0)
epd_rst = Pin(11, Pin.OUT, value=1)
epd_busy = Pin(22, Pin.IN, Pin.PULL_UP)
img = None           # bytearray(2 * PLANE): black plane (1 = white), red plane (1 = red)
img_pos = 0
img_len = 0
epd_state = 0        # 0 idle, 1 refreshing
epd_started = 0
last_show = 0
lost_drawn = False


def epd_cmd(c):
    epd_dc.value(0)
    epd_cs.value(0)
    epd_spi.write(bytes([c]))
    epd_cs.value(1)


def epd_data(d):
    epd_dc.value(1)
    epd_cs.value(0)
    epd_spi.write(d)
    epd_cs.value(1)


def epd_idle():
    # UC8179: BUSY high = idle (status read after command 0x71)
    epd_cmd(0x71)
    return epd_busy.value() == 1


def epd_wait(ms):
    t0 = time.ticks_ms()
    while not epd_idle():
        if time.ticks_diff(time.ticks_ms(), t0) > ms:
            return False
        time.sleep_ms(10)
    return True


def epd_init():
    global epd_spi
    if epd_spi is None:
        epd_spi = machine.SPI(1, baudrate=4000000, polarity=0, phase=0, sck=Pin(14), mosi=Pin(15))
    epd_rst.value(1)
    time.sleep_ms(20)
    epd_rst.value(0)
    time.sleep_ms(4)
    epd_rst.value(1)
    time.sleep_ms(20)
    epd_cmd(0x01)            # power setting
    epd_data(b"\x07\x07\x3f\x3f")
    epd_cmd(0x06)            # booster soft start
    epd_data(b"\x17\x17\x28\x17")
    epd_cmd(0x04)            # power on
    time.sleep_ms(100)
    if not epd_wait(3000):
        return False
    epd_cmd(0x00)            # panel setting: BWR, LUT from OTP
    epd_data(b"\x0f")
    epd_cmd(0x61)            # resolution 800 x 480
    epd_data(b"\x03\x20\x01\xe0")
    epd_cmd(0x15)
    epd_data(b"\x00")
    epd_cmd(0x50)            # VCOM and data interval
    epd_data(b"\x11\x07")
    epd_cmd(0x60)            # TCON
    epd_data(b"\x22")
    return True


def epd_show():
    global epd_state, epd_started, last_show
    if img is None or img_pos < len(img):
        return "ERR no image"
    if epd_state != 0:
        return "ERR busy"
    try:
        if not epd_init():
            return "ERR display not responding"
        epd_cmd(0x10)
        epd_data(memoryview(img)[0:PLANE])
        epd_cmd(0x13)
        epd_data(memoryview(img)[PLANE:2 * PLANE])
        epd_cmd(0x12)        # refresh, finishes in the background
        time.sleep_ms(100)
    except Exception as e:
        return "ERR " + str(e)
    epd_state = 1
    epd_started = time.ticks_ms()
    last_show = epd_started
    return "OK SHOW"


def epd_poll():
    global epd_state
    if epd_state != 1:
        return
    if epd_idle() or time.ticks_diff(time.ticks_ms(), epd_started) > 40000:
        epd_cmd(0x02)        # power off
        epd_wait(3000)
        epd_cmd(0x07)        # deep sleep
        epd_data(b"\xa5")
        epd_state = 0
        print("EPD DONE")


def draw_server_lost():
    # No server for a long time: write a red note into the last picture (built-in 8x8 font, scaled x3)
    import framebuf
    text = "KEINE VERBINDUNG ZUM SERVER"
    small = bytearray(len(text) * 8 * 8 // 8)
    fb = framebuf.FrameBuffer(small, len(text) * 8, 8, framebuf.MONO_HLSB)
    fb.fill(0)
    fb.text(text, 0, 0, 1)
    red = framebuf.FrameBuffer(memoryview(img)[PLANE:2 * PLANE], EPD_W, EPD_H, framebuf.MONO_HLSB)
    black = framebuf.FrameBuffer(memoryview(img)[0:PLANE], EPD_W, EPD_H, framebuf.MONO_HLSB)
    scale = 3
    x0 = (EPD_W - len(text) * 8 * scale) // 2
    y0 = EPD_H // 2 - 12
    red.fill_rect(0, y0 - 12, EPD_W, 8 * scale + 24, 0)
    black.fill_rect(0, y0 - 12, EPD_W, 8 * scale + 24, 1)
    for y in range(8):
        for x in range(len(text) * 8):
            if fb.pixel(x, y):
                red.fill_rect(x0 + x * scale, y0 + y * scale, scale, scale, 1)

# ---------- Commands ----------


def handle(line):
    global last_cmd, last_fan, failsafe, img, img_pos, img_len
    parts = line.strip().split()
    if not parts:
        return
    cmd = parts[0].upper()
    last_cmd = time.ticks_ms()
    if cmd in ("SET", "GET", "HELLO"):
        last_fan = last_cmd
    if cmd == "D":
        if img is not None and len(parts) > 1:
            try:
                chunk = ubinascii.a2b_base64(parts[1])
                n = min(len(chunk), len(img) - img_pos)
                img[img_pos:img_pos + n] = chunk[:n]
                img_pos += n
            except Exception:
                pass
    elif cmd == "HELLO":
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
        failsafe = False
        print(rpm_line())
    elif cmd == "GET":
        print(rpm_line())
    elif cmd == "IMG":
        try:
            size = int(parts[1])
            if size != 2 * PLANE:
                print("ERR size")
                return
            if img is None:
                img = bytearray(2 * PLANE)
            img_pos = 0
            img_len = size
            print("OK IMG")
        except MemoryError:
            print("ERR memory")
        except Exception:
            print("ERR IMG")
    elif cmd == "SHOW":
        print(epd_show())
    elif cmd == "RESET":
        print("OK RESET")
        all_full()
        time.sleep_ms(200)
        machine.reset()
    else:
        print("ERR unknown command")


poll = select.poll()
poll.register(sys.stdin, select.POLLIN)
buf = ""
last_cmd = time.ticks_ms()
last_fan = last_cmd
failsafe = True
last_rpm = time.ticks_ms()
# Hardware watchdog: cannot be stopped once started (also not by Ctrl-C); the server feeds it while installing.
wdt = machine.WDT(timeout=8000)

try:
    while True:
        wdt.feed()
        # read everything that is waiting (image transfers are large)
        budget = 600
        while budget > 0 and poll.poll(0 if budget < 600 else 20):
            budget -= 1
            c = sys.stdin.read(1)
            if c in ("\n", "\r"):
                if buf:
                    handle(buf)
                buf = ""
            elif len(buf) < 400:
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
        if not failsafe and time.ticks_diff(now, last_fan) > WATCHDOG_MS:
            all_full()
            failsafe = True
        poll_buttons(now)
        poll_temps(now)
        if led_off_at and time.ticks_diff(now, led_off_at) >= 0:
            led.value(0)
            led_off_at = 0
        epd_poll()
        if (img is not None and img_pos == len(img) and not lost_drawn and epd_state == 0
                and time.ticks_diff(now, last_cmd) > SERVER_LOST_MS and time.ticks_diff(now, last_show) > 180000):
            lost_drawn = True
            draw_server_lost()
            epd_show()
        if time.ticks_diff(now, last_cmd) < 1000:
            lost_drawn = False
finally:
    # Ctrl-C (update) or crash: fans to 100 %
    all_full()
