"""Run the real Pico program (btfan.py) under normal Python (CPython 3.9+) with simulated hardware.

Used by the tests (PicoNetworkTests) to check WLAN, signing and program updates without a Pico:
    python run_btfan.py <work folder> <path to btfan.py>
The work folder contains btcfg.json and becomes the Pico's file system (main.py, upd.tmp).
USB output goes to stdout. machine.reset() ends the process with exit code 3.
Only for tests and development - not part of the installed program.
"""
import binascii
import os
import select as _select
import socket as _socket
import sys
import time
import types

work, source = sys.argv[1], sys.argv[2]
os.chdir(work)
sys.stdout.reconfigure(line_buffering=True)

# ---------- time (MicroPython extensions) ----------
_t0 = time.monotonic()
time.ticks_ms = lambda: int((time.monotonic() - _t0) * 1000)
time.ticks_us = lambda: int((time.monotonic() - _t0) * 1000000)
time.ticks_diff = lambda a, b: a - b
time.ticks_add = lambda a, b: a + b
time.sleep_ms = lambda ms: time.sleep(ms / 1000)

# ---------- machine ----------
machine = types.ModuleType("machine")


class Pin:
    IN, OUT, PULL_UP, IRQ_FALLING = 0, 1, 2, 4

    def __init__(self, pin, mode=None, pull=None, value=None):
        self.pin = pin
        self._v = 1 if pull == Pin.PULL_UP else (value or 0)

    def value(self, v=None):
        if v is None:
            return self._v
        self._v = v

    def irq(self, trigger=None, handler=None):
        pass


class PWM:
    def __init__(self, pin):
        self.pin = pin
        self.duty = 0

    def freq(self, f):
        pass

    def duty_u16(self, d):
        self.duty = d


class SPI:
    def __init__(self, *a, **k):
        pass

    def write(self, data):
        pass


def reset():
    sys.stdout.flush()
    os._exit(3)


machine.Pin, machine.PWM, machine.SPI, machine.reset = Pin, PWM, SPI, reset
machine.disable_irq = lambda: 0
machine.enable_irq = lambda s: None
sys.modules["machine"] = machine

# ---------- ubinascii ----------
sys.modules["ubinascii"] = binascii

# ---------- select: stdin is never ready (no USB host), sockets via select.select ----------
select = types.ModuleType("select")
select.POLLIN = 1


class Poll:
    def __init__(self):
        self.objs = []

    def register(self, obj, mask=1):
        self.objs.append(obj)

    def poll(self, timeout=0):
        socks = [o for o in self.objs if isinstance(o, _socket.socket) and o.fileno() >= 0]
        if not socks:
            if timeout:
                time.sleep(timeout / 1000)
            return []
        ready, _, _ = _select.select(socks, [], [], (timeout or 0) / 1000)
        return [(s, 1) for s in ready]


select.poll = Poll
sys.modules["select"] = select

# ---------- network ----------
network = types.ModuleType("network")
network.STA_IF = 0


class WLAN:
    PM_NONE = 0xA11140

    def __init__(self, i):
        self.up = False

    def active(self, on=None):
        pass

    def config(self, **k):
        pass

    def connect(self, ssid, psk):
        self.up = True

    def isconnected(self):
        return self.up

    def ifconfig(self):
        return ("127.0.0.1", "255.0.0.0", "127.0.0.1", "127.0.0.1")


network.WLAN = WLAN
network.hostname = lambda name=None: None
sys.modules["network"] = network

code = compile(open(source, encoding="utf-8").read(), "btfan.py", "exec")
exec(code, {"__name__": "__main__"})
