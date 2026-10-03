# BitaxeTuner accessory controller for Raspberry Pi Pico / Pico 2 (+ W variants, MicroPython).
# Installed and updated automatically by the BitaxeTuner server as main.py - do not edit on the device.
#
# Optional btcfg.json (written by the server when setting up WLAN):
#   {"role": "fans" | "display", "ssid": "...", "psk": "...", "key": "<64 hex>", "host": "bitaxetuner-fans", "port": 8490}
# Without btcfg.json: role "fans", USB only (as up to version 5).
#
# Role "fans" (fan board v1 / breadboard):
#   Fans: PWM GP0,2,4,6,8,10 -> 1k -> BC547 base (10k to GND), collector -> fan pin 4.
#   The transistor inverts: GPIO low = fan PWM high = 100 %. Tacho GP16..21 with internal pull-up.
#   e-Paper 7.5" B (800x480, black/white/red, UC8179): DIN GP15, CLK GP14, CS GP13, DC GP12, RST GP11, BUSY GP22.
# Role "display" (Pico plugged directly onto the Waveshare Pico-ePaper-7.5-B, no fans):
#   e-Paper: DIN GP11, CLK GP10, CS GP9, DC GP8, RST GP12, BUSY GP13 (Waveshare layout).
# Both roles: buttons GP1,3,5,7 to GND (internal pull-up), onboard LED blinks on every press;
#   DS18B20 on GP26 (1-Wire, one 4.7k pull-up to 3V3), several sensors in parallel, each reported with its ROM id.
#
# Protocol (one command per line, every valid line resets the watchdog):
#   HELLO                -> OK BTFAN <version> <channels> <role>
#   SET p1 .. p6         -> RPM r1 .. r6 [T id=t id=t ..]   (fan percent 0..100; T = DS18B20 ROM id (hex) = deg C)
#   GET                  -> RPM r1 .. r6 [T id=t id=t ..]   (role display: "RPM" without fan values)
#   IMG <bytes>          -> OK IMG             (then D-lines with base64 data, 1st half black plane, 2nd half red plane)
#   D <base64>           -> (no reply)
#   SHOW                 -> OK SHOW | ERR ...  (refresh runs in the background, ~16 s)
#   PUT main.py <bytes> <sha256 hex> -> OK PUT (then F-lines with base64 data)
#   F <base64>           -> (no reply)
#   COMMIT               -> OK COMMIT | ERR ... (checks size and SHA-256, then replaces main.py; RESET starts it)
#   NET                  -> OK NET <1|0> <ip|->   (WLAN connected and IP address)
#   RESET                -> OK RESET, then the Pico restarts
# Unsolicited: BTN <n> (short press 1-3), BTN 3 LONG (held 5 s), BTN 4 LONG (held 3 s), EPD DONE (refresh finished).
# Button 4 has no short press. Without any command for WATCHDOG_MS all fans go to 100 %.
# Button 3 (short) sets 100 % locally even without server.
#
# WLAN (TCP port 8490, one server at a time): on connect the Pico sends "BTFAN <version> <role> <nonce_p>".
# The server answers "AUTH <nonce_c> <hmac(key, 'C|'+nonce_p+'|'+nonce_c)>", the Pico replies
# "AUTHOK <hmac(key, 'P|'+nonce_c+'|'+nonce_p)>" (both sides prove the key). Session key
# sk = hmac(key, 'S|'+nonce_p+'|'+nonce_c). From then on every line in both directions is
# "<payload> ~<counter> <tag>", tag = first 8 bytes (hex) of hmac(sk, dir + counter + '|' + payload),
# dir 'c' (server -> Pico) or 'p' (Pico -> server), counters start at 1 and must increase by exactly 1.
# A wrong tag or counter closes the connection. Lines without valid tag are never executed.

import sys
import time
import select
import machine
import ubinascii
import hashlib
import os
from machine import Pin, PWM

VERSION = "7"
FREQ = 25000
WATCHDOG_MS = 5000
PULSES_PER_REV = 2
DEBOUNCE_US = 1500
BUTTON_PINS = (1, 3, 5, 7)
# Hold time for a long press per button in ms (0 = no long press): button 3 = fans off, button 4 = reboot
LONG_MS = (0, 0, 5000, 3000)
EPD_W = 800
EPD_H = 480
PLANE = EPD_W * EPD_H // 8
SERVER_LOST_MS = 10 * 60 * 1000
NET_PORT = 8490
AUTH_TIMEOUT_MS = 5000

# ---------- Configuration ----------

cfg = {}
try:
    import json
    with open("btcfg.json") as f:
        cfg = json.load(f)
except Exception:
    cfg = {}

ROLE = "display" if cfg.get("role") == "display" else "fans"
if ROLE == "fans":
    PWM_PINS = (0, 2, 4, 6, 8, 10)
    TACH_PINS = (16, 17, 18, 19, 20, 21)
    EPD_PINS = (15, 14, 13, 12, 11, 22)    # DIN, CLK, CS, DC, RST, BUSY
    EPD_SPI = 1
else:
    PWM_PINS = ()
    TACH_PINS = ()
    EPD_PINS = (11, 10, 9, 8, 12, 13)      # Waveshare Pico-ePaper layout
    EPD_SPI = 1

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
    line = "RPM " + " ".join(str(r) for r in rpm) if N else "RPM"
    if temps:
        line += " T " + " ".join(i + "=" + str(t) for i, t in temps)
    return line

# ---------- DS18B20 temperatures ----------

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

# ---------- Output channels (USB and WLAN) ----------


def usb_send(line):
    print(line)


reply = usb_send      # where the answer to the current command goes


def event(line):
    # unsolicited messages go to USB and to the authenticated WLAN server
    usb_send(line)
    if net is not None:
        net.send(line)

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
            event("BTN %d LONG" % (i + 1))
        elif not pressed and btn_down[i] != 0:
            held = time.ticks_diff(now, btn_down[i])
            btn_down[i] = 0
            if not btn_long_sent[i] and 30 <= held and i != 3 and (not LONG_MS[i] or held < LONG_MS[i]):
                blink()
                if i == 2:
                    all_full()  # 100 % sofort, auch ohne Server
                event("BTN %d" % (i + 1))

# ---------- e-Paper 7.5" B V2 ----------

epd_spi = None
epd_cs = Pin(EPD_PINS[2], Pin.OUT, value=1)
epd_dc = Pin(EPD_PINS[3], Pin.OUT, value=0)
epd_rst = Pin(EPD_PINS[4], Pin.OUT, value=1)
epd_busy = Pin(EPD_PINS[5], Pin.IN, Pin.PULL_UP)
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
        epd_spi = machine.SPI(EPD_SPI, baudrate=4000000, polarity=0, phase=0, sck=Pin(EPD_PINS[1]), mosi=Pin(EPD_PINS[0]))
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
        event("EPD DONE")


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

# ---------- Program update (PUT / F / COMMIT) ----------

upd = None           # [file, expected size, expected sha256 hex, written, hash object]


def hexs(b):
    return ubinascii.hexlify(b).decode()


def upd_start(parts):
    global upd
    if len(parts) != 4 or parts[1] != "main.py":
        return "ERR PUT"
    try:
        size = int(parts[2])
    except ValueError:
        return "ERR PUT"
    if size <= 0 or size > 200000 or len(parts[3]) != 64:
        return "ERR PUT"
    upd_abort()
    upd = [open("upd.tmp", "wb"), size, parts[3].lower(), 0, hashlib.sha256()]
    return "OK PUT"


def upd_chunk(data):
    global upd
    if upd is None:
        return
    try:
        chunk = ubinascii.a2b_base64(data)
    except Exception:
        upd_abort()
        return
    if upd[3] + len(chunk) > upd[1]:
        upd_abort()
        return
    upd[0].write(chunk)
    upd[4].update(chunk)
    upd[3] += len(chunk)


def upd_abort():
    global upd
    if upd is not None:
        try:
            upd[0].close()
        except Exception:
            pass
        upd = None
    try:
        os.remove("upd.tmp")
    except OSError:
        pass


def upd_commit():
    global upd
    if upd is None:
        return "ERR no update"
    u = upd
    u[0].close()
    upd = None
    if u[3] != u[1] or hexs(u[4].digest()) != u[2]:
        upd_abort()
        return "ERR checksum"
    try:
        os.remove("main.py")
    except OSError:
        pass
    os.rename("upd.tmp", "main.py")
    return "OK COMMIT"

# ---------- Commands ----------


def handle(line):
    global last_cmd, failsafe, img, img_pos, img_len
    parts = line.strip().split()
    if not parts:
        return
    cmd = parts[0].upper()
    last_cmd = time.ticks_ms()
    if cmd == "D":
        if img is not None and len(parts) > 1:
            try:
                chunk = ubinascii.a2b_base64(parts[1])
                n = min(len(chunk), len(img) - img_pos)
                img[img_pos:img_pos + n] = chunk[:n]
                img_pos += n
            except Exception:
                pass
    elif cmd == "F":
        if len(parts) > 1:
            upd_chunk(parts[1])
    elif cmd == "HELLO":
        reply("OK BTFAN %s %d %s" % (VERSION, N, ROLE))
    elif cmd == "SET":
        vals = parts[1:]
        if len(vals) != N:
            reply("ERR expected %d values" % N)
            return
        try:
            nums = [int(v) for v in vals]
        except ValueError:
            reply("ERR not a number")
            return
        for i in range(N):
            apply(i, nums[i])
        failsafe = False
        reply(rpm_line())
    elif cmd == "GET":
        reply(rpm_line())
    elif cmd == "IMG":
        try:
            size = int(parts[1])
            if size != 2 * PLANE:
                reply("ERR size")
                return
            if img is None:
                img = bytearray(2 * PLANE)
            img_pos = 0
            img_len = size
            reply("OK IMG")
        except MemoryError:
            reply("ERR memory")
        except Exception:
            reply("ERR IMG")
    elif cmd == "SHOW":
        reply(epd_show())
    elif cmd == "NET":
        if wlan is not None and wlan.isconnected():
            reply("OK NET 1 " + wlan.ifconfig()[0])
        else:
            reply("OK NET 0 -")
    elif cmd == "PUT":
        reply(upd_start(parts))
    elif cmd == "COMMIT":
        reply(upd_commit())
    elif cmd == "RESET":
        reply("OK RESET")
        all_full()
        time.sleep_ms(200)
        machine.reset()
    else:
        reply("ERR unknown command")

# ---------- WLAN ----------


_pads = {}


def hmac(key, msg):
    # HMAC-SHA256; the two padded keys are computed once per key
    pads = _pads.get(key)
    if pads is None:
        k = key if len(key) <= 64 else hashlib.sha256(key).digest()
        k = k + b"\x00" * (64 - len(k))
        ipad = bytearray(64)
        opad = bytearray(64)
        for i in range(64):
            ipad[i] = k[i] ^ 0x36
            opad[i] = k[i] ^ 0x5C
        pads = (bytes(ipad), bytes(opad))
        if len(_pads) > 8:
            _pads.clear()
        _pads[key] = pads
    inner = hashlib.sha256(pads[0] + msg).digest()
    return hashlib.sha256(pads[1] + inner).digest()


def same(a, b):
    # comparison without early exit
    if len(a) != len(b):
        return False
    r = 0
    for x, y in zip(a, b):
        r |= ord(x) ^ ord(y)
    return r == 0


class Net:
    """One authenticated server connection over TCP; further connections replace it only after their AUTH."""

    def __init__(self, sock, key):
        self.sock = sock
        self.key = key
        self.buf = b""
        self.nonce = hexs(os.urandom(16))
        self.started = time.ticks_ms()
        self.sk = None
        self.rx = 0
        self.tx = 0
        self.raw("BTFAN %s %s %s" % (VERSION, ROLE, self.nonce))

    def raw(self, line):
        try:
            self.sock.send((line + "\n").encode())
        except Exception:
            self.close()

    def tag(self, d, ctr, payload):
        return hexs(hmac(self.sk, ("%s%d|%s" % (d, ctr, payload)).encode())[:8])

    def send(self, line):
        if self.sk is None or self.sock is None:
            return
        self.tx += 1
        self.raw("%s ~%d %s" % (line, self.tx, self.tag("p", self.tx, line)))

    def close(self):
        if self.sock is not None:
            try:
                self.sock.close()
            except Exception:
                pass
        self.sock = None

    def read(self):
        """New complete lines (bytes) or None if the connection is gone."""
        if self.sock is None:
            return None
        try:
            data = self.sock.recv(2048)
        except OSError:
            return []
        if not data:
            self.close()
            return None
        self.buf += data
        if len(self.buf) > 4096 and b"\n" not in self.buf:
            self.close()
            return None
        lines = self.buf.split(b"\n")
        self.buf = lines.pop()
        return lines

    def auth(self, line):
        parts = line.split()
        if len(parts) != 3 or parts[0] != "AUTH" or len(parts[1]) != 32:
            return False
        expect = hexs(hmac(self.key, ("C|%s|%s" % (self.nonce, parts[1])).encode()))
        if not same(expect, parts[2]):
            return False
        self.sk = hmac(self.key, ("S|%s|%s" % (self.nonce, parts[1])).encode())
        self.raw("AUTHOK " + hexs(hmac(self.key, ("P|%s|%s" % (parts[1], self.nonce)).encode())))
        return True

    def unwrap(self, line):
        """Payload of a correctly signed line, otherwise None (connection is closed)."""
        i = line.rfind(" ~")
        if i < 0:
            self.close()
            return None
        payload = line[:i]
        rest = line[i + 2:].split()
        if len(rest) != 2 or not rest[0].isdigit() or int(rest[0]) != self.rx + 1 or not same(self.tag("c", self.rx + 1, payload), rest[1]):
            self.close()
            return None
        self.rx += 1
        return payload


wlan = None
srv = None
net = None           # authenticated connection
pending = None       # connection waiting for AUTH
wlan_tried = -60000
NET_KEY = None


def net_setup():
    global wlan, srv, NET_KEY
    if not cfg.get("ssid") or len(cfg.get("key", "")) != 64:
        return
    try:
        import network
        import socket
        NET_KEY = ubinascii.unhexlify(cfg["key"])
        try:
            network.hostname(cfg.get("host") or ("bitaxetuner-" + ROLE))
        except Exception:
            pass
        wlan = network.WLAN(network.STA_IF)
        wlan.active(True)
        try:
            wlan.config(pm=getattr(network.WLAN, "PM_NONE", 0xa11140))   # no power saving: quick answers
        except Exception:
            pass
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind((cfg.get("bind", "0.0.0.0"), int(cfg.get("port", NET_PORT))))
        srv.listen(2)
        srv.setblocking(False)
    except Exception as e:
        usb_send("ERR WLAN " + str(e))
        wlan = None
        srv = None


def net_poll(now):
    """Keep WLAN up, accept connections, run received lines."""
    global wlan_tried, net, pending, reply
    if wlan is None:
        return
    if not wlan.isconnected() and time.ticks_diff(now, wlan_tried) > 15000:
        wlan_tried = now
        try:
            wlan.connect(cfg["ssid"], cfg.get("psk", ""))
        except Exception:
            pass
    if srv is not None:
        try:
            s, _ = srv.accept()
            if pending is not None:
                pending.close()
            s.settimeout(2)
            pending = Net(s, NET_KEY)
        except OSError:
            pass
    if pending is not None:
        if pending.sock is None or time.ticks_diff(now, pending.started) > AUTH_TIMEOUT_MS:
            pending.close()
            pending = None
        elif poll_ready(pending.sock):
            lines = pending.read()
            if lines is None:
                pending = None
            elif lines:
                if pending.auth(lines[0].decode().strip()):
                    if net is not None:
                        net.close()
                    net = pending
                    pending = None
                    for l in lines[1:]:
                        net_line(l)
                else:
                    pending.close()
                    pending = None
    if net is not None and net.sock is not None and poll_ready(net.sock):
        lines = net.read()
        if lines is None:
            net = None
        else:
            for l in lines:
                net_line(l)
                if net is None or net.sock is None:
                    break
    if net is not None and net.sock is None:
        net = None


def net_line(raw):
    global reply
    try:
        line = raw.decode().strip()
    except Exception:
        line = ""
    if not line or net is None:
        return
    payload = net.unwrap(line)
    if payload is None:
        return
    reply = net.send
    try:
        handle(payload)
    finally:
        reply = usb_send


def poll_ready(sock):
    p = select.poll()
    p.register(sock, select.POLLIN)
    return bool(p.poll(0))

# ---------- Main loop ----------


usb = select.poll()
usb.register(sys.stdin, select.POLLIN)
buf = ""
last_cmd = time.ticks_ms()
failsafe = True
last_rpm = time.ticks_ms()
net_setup()

try:
    while True:
        # read everything that is waiting (image transfers are large)
        budget = 600
        while budget > 0 and usb.poll(0 if budget < 600 else (5 if wlan is not None else 20)):
            budget -= 1
            c = sys.stdin.read(1)
            if c in ("\n", "\r"):
                if buf:
                    reply = usb_send
                    handle(buf)
                buf = ""
            elif len(buf) < 400:
                buf += c
        now = time.ticks_ms()
        for _ in range(40):
            net_poll(now)
            if net is None or net.sock is None or not poll_ready(net.sock):
                break
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
