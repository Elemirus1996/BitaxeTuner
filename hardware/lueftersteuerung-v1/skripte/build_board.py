# BitaxeTuner Lüfterplatine v1 – erzeugt die Platine (Bauteile, Netze, Platzierung, Umriss, Beschriftung) mit pcbnew.
# Aufruf mit dem Python von KiCad: python build_board.py <ausgabe.kicad_pcb>
# Schaltung = Steckbrett-Aufbau der Pico-Anleitung (Firmware btfan.py v6), 5-V-Lüfter, USB-C-Stromeingang mit Überspannungsschutz.
import sys
import pcbnew

import os
FP = os.environ.get("KICAD10_FOOTPRINT_DIR", os.path.join(os.environ.get("KICAD_DIR", ""), "share", "kicad", "footprints"))
OUT = sys.argv[1]
W, H = 115.0, 85.0


def mm(v):
    return pcbnew.FromMM(v)


board = pcbnew.BOARD()
nets = {}


def net(name):
    if name not in nets:
        n = pcbnew.NETINFO_ITEM(board, name)
        board.Add(n)
        nets[name] = n
    return nets[name]


BOM = []  # (ref, value, footprint, lcsc)


def place(lib, name, ref, value, x, y, rot=0, pads=None, lcsc="", dnp=False):
    fp = pcbnew.FootprintLoad(f"{FP}/{lib}.pretty", name)
    if fp is None:
        raise SystemExit(f"Footprint fehlt: {lib}:{name}")
    fp.SetReference(ref)
    fp.SetValue(value)
    board.Add(fp)
    fp.SetPosition(pcbnew.VECTOR2I(mm(x), mm(y)))
    fp.SetOrientationDegrees(rot)
    for pad in fp.Pads():
        num = pad.GetNumber()
        if pads and num in pads and pads[num]:
            pad.SetNet(net(pads[num]))
    fp.Reference().SetTextSize(pcbnew.VECTOR2I(mm(0.8), mm(0.8)))
    fp.Reference().SetTextThickness(mm(0.12))
    fp.Value().SetVisible(False)
    if not dnp:
        BOM.append((ref, value, f"{lib}:{name}", lcsc))
    return fp


# ---------- Pico 2 H auf zwei Buchsenleisten (USB des Pico zeigt nach oben) ----------
PICO = {
    1: "PWM1", 2: "BTN1", 3: "GND", 4: "PWM2", 5: "BTN2", 6: "PWM3", 7: "BTN3", 8: "GND", 9: "PWM4", 10: "BTN4",
    11: "PWM5", 12: "", 13: "GND", 14: "PWM6", 15: "EPD_RST", 16: "EPD_DC", 17: "EPD_CS", 18: "GND", 19: "EPD_CLK", 20: "EPD_DIN",
    21: "TACH1", 22: "TACH2", 23: "GND", 24: "TACH3", 25: "TACH4", 26: "TACH5", 27: "TACH6", 28: "GND", 29: "EPD_BUSY", 30: "",
    31: "ONEWIRE", 32: "", 33: "GND", 34: "", 35: "", 36: "+3V3", 37: "", 38: "GND", 39: "VSYS", 40: "",
}
PX, PY = 52.0, 10.0
place("Connector_PinSocket_2.54mm", "PinSocket_1x20_P2.54mm_Vertical", "J1", "Pico 2 H Pins 1-20", PX, PY,
      pads={str(i): PICO[i] for i in range(1, 21)}, lcsc="C2905423")
# rechte Leiste: oben Pin 40, unten Pin 21
place("Connector_PinSocket_2.54mm", "PinSocket_1x20_P2.54mm_Vertical", "J2", "Pico 2 H Pins 40-21", PX + 17.78, PY,
      pads={str(k): PICO[41 - k] for k in range(1, 21)}, lcsc="C2905423")

# Masse-Pins der Buchsenleisten vollflächig anbinden (zwischen den Signalbahnen bleibt sonst zu wenig Platz für Stege)
for ref in ("J1", "J2"):
    for pad in board.FindFootprintByReference(ref).Pads():
        if pad.GetNetname() == "GND":
            pad.SetLocalZoneConnection(pcbnew.ZONE_CONNECTION_FULL)

# ---------- Lüfterkanäle K1–K6 ----------
for n in range(6):
    k = n + 1
    hx = 10.0 + 15.0 * n
    place("Connector", "FanPinHeader_1x04_P2.54mm_Vertical", f"J{10 + k}", f"Lüfter K{k}", hx, 79.0,
          pads={"1": "GND", "2": "+5V_FAN", "3": f"TACH{k}", "4": f"FANPWM{k}"}, lcsc="C240840")
    place("Resistor_SMD", "R_0603_1608Metric", f"R{10 + k}", "1k", hx + 0.6, 68.0, 90,
          pads={"1": f"PWM{k}", "2": f"BASE{k}"}, lcsc="C21190")
    place("Resistor_SMD", "R_0603_1608Metric", f"R{20 + k}", "10k", hx + 3.0, 71.2, 0,
          pads={"1": f"BASE{k}", "2": "GND"}, lcsc="C25804")
    # BC847B: 1 = Basis, 2 = Emitter, 3 = Kollektor
    place("Package_TO_SOT_SMD", "SOT-23", f"Q{k}", "BC847B", hx + 4.2, 67.6, 0,
          pads={"1": f"BASE{k}", "2": "GND", "3": f"FANPWM{k}"}, lcsc="C20069135")
    place("Resistor_SMD", "R_0603_1608Metric", f"R{30 + k}", "10k", hx + 9.4, 68.0, 90,
          pads={"1": f"TACH{k}", "2": "+3V3"}, lcsc="C25804")

# ---------- Stromeingang 5 V für die Lüfter: USB-C (nur Strom) + Sicherung + Überspannungsabschaltung ----------
place("Connector_USB", "USB_C_Receptacle_GCT_USB4125-xx-x_6P_TopMnt_Horizontal", "J3", "USB-C 5V FAN POWER", 3.4, 22.0, 270,
      pads={"A9": "VIN", "B9": "VIN", "A12": "GND", "B12": "GND", "SH": "GND", "A5": "CC1", "B5": "CC2"}, lcsc="C3151650")
place("Resistor_SMD", "R_0603_1608Metric", "R1", "5.1k", 12.0, 15.0, 90, pads={"1": "CC1", "2": "GND"}, lcsc="C23186")
place("Resistor_SMD", "R_0603_1608Metric", "R2", "5.1k", 15.0, 15.0, 90, pads={"1": "CC2", "2": "GND"}, lcsc="C23186")
place("Fuse", "Fuse_1812_4532Metric", "F1", "PTC 2A hold", 15.0, 22.0, 0, pads={"1": "VIN", "2": "VIN_F"}, lcsc="C960026")
# zwei AO3401A parallel: 1 = Gate, 2 = Source (Eingang), 3 = Drain (+5V_FAN)
place("Package_TO_SOT_SMD", "SOT-23", "Q7", "AO3401A", 23.0, 19.0, 0, pads={"1": "GATE", "2": "VIN_F", "3": "+5V_FAN"}, lcsc="C15127")
place("Package_TO_SOT_SMD", "SOT-23", "Q8", "AO3401A", 23.0, 25.0, 0, pads={"1": "GATE", "2": "VIN_F", "3": "+5V_FAN"}, lcsc="C15127")
place("Resistor_SMD", "R_0603_1608Metric", "R3", "100k", 18.5, 31.0, 0, pads={"1": "GATE", "2": "GND"}, lcsc="C25803")
# Klemmdiode 6,2 V: Kathode (1) an Source, Anode (2) an Gate – begrenzt die Gate-Spannung
place("Diode_SMD", "D_SOD-123", "D1", "MM1Z6V2", 18.5, 35.0, 0, pads={"1": "VIN_F", "2": "GATE"}, lcsc="C22379464")
# Überspannung: PNP BC857C, 1 = Basis, 2 = Emitter (Source/VIN_F), 3 = Kollektor (Gate)
place("Package_TO_SOT_SMD", "SOT-23", "Q9", "BC857C", 12.0, 31.0, 0, pads={"1": "OV_B", "2": "VIN_F", "3": "GATE"}, lcsc="C20069137")
place("Resistor_SMD", "R_0603_1608Metric", "R4", "10k", 12.0, 36.0, 0, pads={"1": "VIN_F", "2": "OV_B"}, lcsc="C25804")
# Z-Diode 5,6 V: Kathode (1) an der PNP-Basis, Anode (2) über 10k nach GND – ab ca. 6,2 V sperrt der PNP die MOSFETs
place("Diode_SMD", "D_SOD-123", "D2", "BZT52C5V6", 12.0, 40.0, 0, pads={"1": "OV_B", "2": "OV_Z"}, lcsc="C173406")
place("Resistor_SMD", "R_0603_1608Metric", "R5", "10k", 12.0, 44.0, 0, pads={"1": "OV_Z", "2": "GND"}, lcsc="C25804")
place("Capacitor_SMD", "CP_Elec_8x10.5", "C1", "470uF 25V", 33.0, 30.0, 90, pads={"1": "+5V_FAN", "2": "GND"}, lcsc="C48935394")
place("Capacitor_SMD", "C_0603_1608Metric", "C2", "100nF", 27.5, 36.0, 90, pads={"1": "+5V_FAN", "2": "GND"}, lcsc="C14663")
# TVS nur hinter den MOSFETs: Kathode (1) an +5V_FAN
place("Diode_SMD", "D_SMA", "D3", "SMAJ5.0A", 27.5, 42.0, 90, pads={"1": "+5V_FAN", "2": "GND"}, lcsc="C2925443")
# Messpunkte für den Test mit Labornetzteil
place("TestPoint", "TestPoint_Pad_D1.5mm", "TP1", "VIN", 8.0, 50.0, pads={"1": "VIN_F"})
place("TestPoint", "TestPoint_Pad_D1.5mm", "TP2", "+5V_FAN", 13.0, 50.0, pads={"1": "+5V_FAN"})
place("TestPoint", "TestPoint_Pad_D1.5mm", "TP3", "GND", 18.0, 50.0, pads={"1": "GND"})

# ---------- E-Paper, Taster, Temperaturfühler ----------
place("Connector_PinHeader_2.54mm", "PinHeader_1x08_P2.54mm_Vertical", "J4", "E-PAPER", 80.0, 12.0,
      pads={"1": "VSYS", "2": "GND", "3": "EPD_DIN", "4": "EPD_CLK", "5": "EPD_CS", "6": "EPD_DC", "7": "EPD_RST", "8": "EPD_BUSY"},
      lcsc="C32713274")
place("Connector_PinHeader_2.54mm", "PinHeader_1x08_P2.54mm_Vertical", "J5", "BUTTONS", 91.0, 12.0,
      pads={"1": "BTN1", "2": "GND", "3": "BTN2", "4": "GND", "5": "BTN3", "6": "GND", "7": "BTN4", "8": "GND"},
      lcsc="C32713274")
for i, ty in enumerate((22.0, 39.0, 56.0)):
    # DS18B20: GND – DQ – VDD (Reihenfolge der Fühlerbeine); Pin 1 unten, Pins nach oben
    place("TerminalBlock", "TerminalBlock_MaiXu_MX126-5.0-03P_1x03_P5.00mm", f"J{6 + i}", f"DS18B20 #{i + 1}", 108.5, ty, 90,
          pads={"1": "GND", "2": "ONEWIRE", "3": "+3V3"}, lcsc="C5188435")
    # Masse-Pin vollflächig an die Massefläche (große Klemmen-Pads: sonst unvollständige Wärmefallen)
    for pad in board.FindFootprintByReference(f"J{6 + i}").Pads():
        if pad.GetNumber() == "1":
            pad.SetLocalZoneConnection(pcbnew.ZONE_CONNECTION_FULL)
place("Resistor_SMD", "R_0603_1608Metric", "R6", "4.7k", 101.0, 45.0, 90, pads={"1": "ONEWIRE", "2": "+3V3"}, lcsc="C23162")

# ---------- Befestigung ----------
for i, (hx, hy) in enumerate(((3.6, 3.6), (W - 3.6, 3.6), (3.6, H - 3.6), (W - 3.6, H - 3.6))):
    place("MountingHole", "MountingHole_3.2mm_M3", f"H{i + 1}", "M3", hx, hy, dnp=True)

# ---------- Umriss (abgerundete Ecken 2 mm) ----------
def edge_line(x1, y1, x2, y2):
    s = pcbnew.PCB_SHAPE(board)
    s.SetShape(pcbnew.SHAPE_T_SEGMENT)
    s.SetStart(pcbnew.VECTOR2I(mm(x1), mm(y1)))
    s.SetEnd(pcbnew.VECTOR2I(mm(x2), mm(y2)))
    s.SetLayer(pcbnew.Edge_Cuts)
    s.SetWidth(mm(0.1))
    board.Add(s)


def edge_arc(cx, cy, sx, sy, angle):
    s = pcbnew.PCB_SHAPE(board)
    s.SetShape(pcbnew.SHAPE_T_ARC)
    s.SetCenter(pcbnew.VECTOR2I(mm(cx), mm(cy)))
    s.SetStart(pcbnew.VECTOR2I(mm(sx), mm(sy)))
    s.SetArcAngleAndEnd(pcbnew.EDA_ANGLE(angle, pcbnew.DEGREES_T))
    s.SetLayer(pcbnew.Edge_Cuts)
    s.SetWidth(mm(0.1))
    board.Add(s)


r = 2.0
edge_line(r, 0, W - r, 0)
edge_line(W, r, W, H - r)
edge_line(W - r, H, r, H)
edge_line(0, H - r, 0, r)
edge_arc(W - r, r, W - r, 0, 90)
edge_arc(W - r, H - r, W, H - r, 90)
edge_arc(r, H - r, r, H, 90)
edge_arc(r, r, 0, r, 90)

# ---------- Beschriftung ----------
def text(t, x, y, size=1.2, layer=pcbnew.F_SilkS, bold=False):
    tx = pcbnew.PCB_TEXT(board)
    tx.SetText(t)
    tx.SetPosition(pcbnew.VECTOR2I(mm(x), mm(y)))
    tx.SetLayer(layer)
    tx.SetTextSize(pcbnew.VECTOR2I(mm(size), mm(size)))
    tx.SetTextThickness(mm(size * 0.15))
    tx.SetBold(bold)
    board.Add(tx)


text("BitaxeTuner Fan Board v1.0", 27.0, 4.0, 1.6, bold=True)
text("PICO 2 H  -  USB -> RASPBERRY PI", PX + 8.9, 5.0, 1.0)
text("FAN POWER", 6.0, 12.0, 1.0)
text("5V ONLY", 6.0, 13.6, 1.0)
text("E-PAPER", 80.0, 7.0, 1.0)
for i, lbl in enumerate(("VSYS", "GND", "DIN", "CLK", "CS", "DC", "RST", "BUSY")):
    text(lbl, 85.0, 12.0 + 2.54 * i, 0.9)
text("BUTTONS", 92.5, 7.0, 1.0)
for i, lbl in enumerate(("B1", "G", "B2", "G", "B3", "G", "B4", "G")):
    text(lbl, 94.6, 12.0 + 2.54 * i, 0.9)
text("DS18B20", 101.0, 59.5, 1.0)
text("1=GND 2=DQ 3=3V3", 101.0, 61.2, 0.8)

for n in range(6):
    text(f"K{n + 1}", 10.0 + 15.0 * n + 3.8, 74.0, 1.2, bold=True)
text("TP: VIN  5V  GND", 13.0, 53.0, 0.9)
text("github.com/Elemirus1996/BitaxeTuner", 61.0, 62.4, 0.9)

# Alle durchgesteckten Masse-Pins vollflächig an die Masseflächen (sichere Anbindung, keine unvollständigen Wärmefallen)
for fp in board.GetFootprints():
    for pad in fp.Pads():
        if pad.GetNetname() == "GND" and pad.GetAttribute() == pcbnew.PAD_ATTRIB_PTH:
            pad.SetLocalZoneConnection(pcbnew.ZONE_CONNECTION_FULL)

# ---------- Regeln (JLCPCB-tauglich) ----------
ds = board.GetDesignSettings()
nc = ds.m_NetSettings.GetDefaultNetclass()
nc.SetTrackWidth(mm(0.3))
nc.SetClearance(mm(0.2))
nc.SetViaDiameter(mm(0.7))
nc.SetViaDrill(mm(0.35))
ds.m_TrackMinWidth = mm(0.15)
ds.m_MinClearance = mm(0.15)
ds.m_ViasMinSize = mm(0.5)
ds.m_MinThroughDrill = mm(0.3)
ds.m_CopperEdgeClearance = mm(0.3)

pcbnew.SaveBoard(OUT, board)

with open(OUT.replace(".kicad_pcb", "_bom_raw.tsv"), "w", encoding="utf-8") as f:
    for row in BOM:
        f.write("\t".join(row) + "\n")
print("ok", len(board.GetFootprints()), "Bauteile,", len(nets), "Netze")
