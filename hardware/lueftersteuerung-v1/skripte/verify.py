# Unabhängige Prüfung der fertigen Platine gegen die Firmware btfan.py (Pins) und die Sicherheitsregeln.
import os
import re
import sys
import pcbnew

board = pcbnew.LoadBoard(sys.argv[1])
fw = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "src", "BitaxeTuner.Core", "Fans", "Firmware", "btfan.py"), encoding="utf-8").read()
PWM = [int(x) for x in re.search(r"PWM_PINS = \(([^)]*)\)", fw).group(1).split(",")]
TACH = [int(x) for x in re.search(r"TACH_PINS = \(([^)]*)\)", fw).group(1).split(",")]
BTN = [int(x) for x in re.search(r"BUTTON_PINS = \(([^)]*)\)", fw).group(1).split(",")]
EPD = {"CLK": 14, "DIN": 15, "CS": 13, "DC": 12, "RST": 11, "BUSY": 22}
ONEWIRE = 26

# Pico-Pin-Nummer → GPIO (offizielle Pinbelegung Pico / Pico 2)
GP = {1: 0, 2: 1, 4: 2, 5: 3, 6: 4, 7: 5, 9: 6, 10: 7, 11: 8, 12: 9, 14: 10, 15: 11, 16: 12, 17: 13, 19: 14, 20: 15,
      21: 16, 22: 17, 24: 18, 25: 19, 26: 20, 27: 21, 29: 22, 31: 26, 32: 27, 34: 28}
GND_PINS = {3, 8, 13, 18, 23, 28, 33, 38}

pico = {}
for fp in board.GetFootprints():
    if fp.GetReference() == "J1":
        for p in fp.Pads():
            pico[int(p.GetNumber())] = p.GetNetname()
    if fp.GetReference() == "J2":
        for p in fp.Pads():
            pico[41 - int(p.GetNumber())] = p.GetNetname()
gpio_net = {GP[pin]: net for pin, net in pico.items() if pin in GP}
errors = []


def check(cond, msg):
    if not cond:
        errors.append(msg)


for k, gp in enumerate(PWM, 1):
    check(gpio_net.get(gp) == f"PWM{k}", f"K{k} PWM: GP{gp} hat Netz {gpio_net.get(gp)}")
for k, gp in enumerate(TACH, 1):
    check(gpio_net.get(gp) == f"TACH{k}", f"K{k} Tacho: GP{gp} hat Netz {gpio_net.get(gp)}")
for k, gp in enumerate(BTN, 1):
    check(gpio_net.get(gp) == f"BTN{k}", f"Taster {k}: GP{gp} hat Netz {gpio_net.get(gp)}")
for name, gp in EPD.items():
    check(gpio_net.get(gp) == f"EPD_{name}", f"E-Paper {name}: GP{gp} hat Netz {gpio_net.get(gp)}")
check(gpio_net.get(ONEWIRE) == "ONEWIRE", "DS18B20 nicht an GP26")
for pin in GND_PINS:
    check(pico[pin] == "GND", f"Pico-Pin {pin} nicht GND")
check(pico[36] == "+3V3", "Pin 36 nicht 3V3")
check(pico[39] == "VSYS", "Pin 39 nicht VSYS")
check(pico[40] == "", "Pin 40 (VBUS) darf nicht verbunden sein")
check("+5V_FAN" not in pico.values() and "VIN" not in pico.values() and "VIN_F" not in pico.values(),
      "Lüfter-5-V oder Eingang an einem Pico-Pin!")

# Netze der Bauteile
pads = {}
for fp in board.GetFootprints():
    for p in fp.Pads():
        pads[(fp.GetReference(), p.GetNumber())] = p.GetNetname()
for k in range(1, 7):
    j = f"J{10 + k}"
    check(pads[(j, "1")] == "GND" and pads[(j, "2")] == "+5V_FAN" and pads[(j, "3")] == f"TACH{k}" and pads[(j, "4")] == f"FANPWM{k}",
          f"Lüfterstecker K{k} falsch belegt")
    check(pads[(f"Q{k}", "1")] == f"BASE{k}" and pads[(f"Q{k}", "2")] == "GND" and pads[(f"Q{k}", "3")] == f"FANPWM{k}", f"Q{k} falsch")
    check({pads[(f"R{10 + k}", "1")], pads[(f"R{10 + k}", "2")]} == {f"PWM{k}", f"BASE{k}"}, f"R1 von K{k} falsch")
    check({pads[(f"R{20 + k}", "1")], pads[(f"R{20 + k}", "2")]} == {f"BASE{k}", "GND"}, f"R2 (Ausfallschutz) von K{k} fehlt/falsch")
    check({pads[(f"R{30 + k}", "1")], pads[(f"R{30 + k}", "2")]} == {f"TACH{k}", "+3V3"}, f"R3 von K{k} falsch (Tacho muss an 3,3 V)")
# Schutzschaltung
check(pads[("Q7", "2")] == "VIN_F" and pads[("Q7", "3")] == "+5V_FAN" and pads[("Q8", "2")] == "VIN_F" and pads[("Q8", "3")] == "+5V_FAN",
      "MOSFET-Richtung falsch (Source muss am Eingang liegen)")
check(pads[("D1", "1")] == "VIN_F" and pads[("D1", "2")] == "GATE", "Gate-Klemmdiode falsch gepolt")
check(pads[("Q9", "2")] == "VIN_F" and pads[("Q9", "3")] == "GATE" and pads[("Q9", "1")] == "OV_B", "PNP falsch")
check(pads[("D2", "1")] == "OV_B" and pads[("D2", "2")] == "OV_Z", "Z-Diode 5,6 V falsch gepolt")
check(pads[("D3", "1")] == "+5V_FAN" and pads[("D3", "2")] == "GND", "TVS nicht hinter dem MOSFET oder falsch gepolt")
check(pads[("C1", "1")] == "+5V_FAN", "Elko falsch gepolt")
check(pads[("F1", "1")] == "VIN" and pads[("F1", "2")] == "VIN_F", "Sicherung falsch")
check(pads[("J4", "1")] == "VSYS" and pads[("J4", "2")] == "GND", "E-Paper-Versorgung falsch")
for i in range(3):
    j = f"J{6 + i}"
    check((pads[(j, "1")], pads[(j, "2")], pads[(j, "3")]) == ("GND", "ONEWIRE", "+3V3"), f"Fühlerklemme {j} falsch")

# Leiterbahnbreite der Stromnetze
narrow = [t for t in board.GetTracks() if t.GetNetname() in ("+5V_FAN", "VIN", "VIN_F") and t.GetClass() == "PCB_TRACK"
          and pcbnew.ToMM(t.GetWidth()) < 0.99
          # erlaubt: kurze Anschlussstücke an schmalen Pads (z. B. VBUS der USB-C-Buchse, 0,76 mm), höchstens 2 mm lang
          and not (pcbnew.ToMM(t.GetWidth()) >= 0.7 and pcbnew.ToMM(t.GetLength()) <= 2.0)]
check(not narrow, f"{len(narrow)} Strom-Leiterbahnen schmaler als 1 mm")

print("Pico-Pins:", ", ".join(f"{p}={pico[p] or '-'}" for p in sorted(pico)))
print("FEHLER:" if errors else "Alle Prüfungen bestanden.")
for e in errors:
    print("  -", e)
sys.exit(1 if errors else 0)
