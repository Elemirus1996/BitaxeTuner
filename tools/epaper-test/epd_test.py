# BitaxeTuner - test picture for the Waveshare Pico-ePaper-7.5-B (800x480, black/white/red)
#
# Run in Thonny on a Pico (2) W plugged onto the Waveshare driver board ("Run current script", do NOT save as main.py).
# The BitaxeTuner program on the Pico (main.py) is not changed; after a reset it starts again as usual.
#
# What you should see after about 20-30 seconds: white background, black frame, the text "BitaxeTuner E-Paper Test",
# a black and a red bar. If the screen stays unchanged, check the board seating and the console output below.
#
# Pins as on the Waveshare board: DIN GP11, CLK GP10, CS GP9, DC GP8, RST GP12, BUSY GP13.
# Important (same fix as in BitaxeTuner program 7.1): SPI with an explicit unused MISO pin (GP28) - otherwise
# MicroPython takes GP8 as SPI1 RX, which is the DC line of the display.
import time
import framebuf
from machine import Pin, SPI

W, H = 800, 480

cs = Pin(9, Pin.OUT, value=1)
dc = Pin(8, Pin.OUT, value=0)
rst = Pin(12, Pin.OUT, value=1)
busy = Pin(13, Pin.IN, Pin.PULL_UP)
spi = SPI(1, baudrate=4000000, polarity=0, phase=0, sck=Pin(10), mosi=Pin(11), miso=Pin(28))
dc.init(Pin.OUT, value=0)
rst.init(Pin.OUT, value=1)


def cmd(c):
    dc.value(0)
    cs.value(0)
    spi.write(bytes([c]))
    cs.value(1)


def data(d):
    dc.value(1)
    cs.value(0)
    spi.write(d)
    cs.value(1)


def wait(ms, what):
    t0 = time.ticks_ms()
    while busy.value() == 0:          # BUSY low = busy
        if time.ticks_diff(time.ticks_ms(), t0) > ms:
            print("TIMEOUT waiting for", what, "- BUSY stays low (display not responding)")
            return False
        time.sleep_ms(20)
    print("ok:", what, time.ticks_diff(time.ticks_ms(), t0), "ms")
    return True


print("BUSY at start:", busy.value())
rst.value(1); time.sleep_ms(200)
rst.value(0); time.sleep_ms(2)
rst.value(1); time.sleep_ms(200)
cmd(0x06); data(b"\x17\x17\x28\x17")      # booster
cmd(0x04); time.sleep_ms(100)              # power on
if wait(5000, "power on"):
    cmd(0x00); data(b"\x0f")
    cmd(0x61); data(b"\x03\x20\x01\xe0")
    cmd(0x15); data(b"\x00")
    cmd(0x50); data(b"\x11\x07")
    cmd(0x60); data(b"\x22")
    cmd(0x65); data(b"\x00\x00\x00\x00")

    black = bytearray(W * H // 8)
    red = bytearray(W * H // 8)
    fb = framebuf.FrameBuffer(black, W, H, framebuf.MONO_HLSB)
    fr = framebuf.FrameBuffer(red, W, H, framebuf.MONO_HLSB)
    fb.fill(1)                             # black plane: 1 = white
    fr.fill(0)                             # red plane: 0 = no red
    fb.rect(10, 10, W - 20, H - 20, 0)
    fb.rect(12, 12, W - 24, H - 24, 0)
    fb.text("BitaxeTuner E-Paper Test", 40, 60, 0)
    fb.text("Wenn du das liest, funktioniert das Display.", 40, 90, 0)
    fb.fill_rect(40, 150, 720, 60, 0)      # black bar
    fr.fill_rect(40, 250, 720, 60, 1)      # red bar

    cmd(0x10); data(black)
    cmd(0x13); data(red)
    cmd(0x12); time.sleep_ms(100)          # refresh (~16-25 s)
    # Did BUSY go low ("busy") at all? If not, the BUSY line does not work - then wait a fixed time instead,
    # otherwise switching the panel off right away would cut the refresh short (picture does not change).
    t0 = time.ticks_ms()
    seen = False
    while time.ticks_diff(time.ticks_ms(), t0) < 3000:
        if busy.value() == 0:
            seen = True
            break
        time.sleep_ms(10)
    if seen:
        wait(40000, "refresh")
    else:
        print("BUSY never went low - BUSY line (GP13) not working? Waiting 25 s instead ...")
        time.sleep_ms(25000)
    cmd(0x02); wait(5000, "power off")
    cmd(0x07); data(b"\xa5")               # deep sleep
    print("Done - the picture should be visible now.", "(BUSY ok)" if seen else "(BUSY NOT working)")
