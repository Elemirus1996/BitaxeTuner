# Stückliste (BOM) und Bestückungsliste (CPL) im JLCPCB-Format.
# Aufruf mit dem Python von KiCad: python export_jlc.py <platine.kicad_pcb> <bauteile.tsv> <ausgabeordner>
#
# CPL: JLCPCB erwartet die Bauteilmitte, nicht den Bezugspunkt der KiCad-Bauform (bei Steckern = Pin 1). Die Mitte wird
# daher aus den nummerierten Pads berechnet (ohne Befestigungs-/Führungslöcher). Dazu Drehkorrekturen je Bauform, weil
# JLCPCB bei manchen Gehäusen den Nullpunkt anders festlegt – geprüft in der JLCPCB-Bestückungsvorschau am 03.10.2026,
# Lüfterstecker und USB-C nach der Freigabe durch den JLCPCB-Ingenieur (Auftrag SMT026100360073) am 04.10.2026.
import csv
import sys
from collections import OrderedDict

import pcbnew

pcb, raw, out_dir = sys.argv[1], sys.argv[2], sys.argv[3]
rows = [line.rstrip("\n").split("\t") for line in open(raw, encoding="utf-8") if line.strip()]
rows = [r for r in rows if len(r) > 3 and r[3]]   # ohne LCSC-Nummer (Messpunkte = nur Kupfer) nicht bestücken
lcsc_of = {r[0]: r[3] for r in rows}
fp_of = {r[0]: r[2].split(":")[1] for r in rows}

# Gleiche Teile zusammenfassen; verständliche Bezeichnung je Teil
NAMES = {"C2905423": "Female header 1x20 2.54mm (Pico 2 H)", "C240840": "Molex 47053-1000 fan header 4P",
         "C32713274": "Pin header 1x08 2.54mm", "C5188435": "Terminal block 3P 5.0mm", "C3151650": "GCT USB4125-GF-A USB-C 6P"}

groups = OrderedDict()
for ref, value, fp, lcsc in rows:
    key = (NAMES.get(lcsc, value), fp.split(":")[1], lcsc)
    groups.setdefault(key, []).append(ref)
with open(f"{out_dir}/BOM_JLCPCB.csv", "w", newline="", encoding="utf-8") as f:
    w = csv.writer(f)
    w.writerow(["Comment", "Designator", "Footprint", "LCSC Part #"])
    for (value, fp, lcsc), refs in groups.items():
        w.writerow([value, ",".join(refs), fp, lcsc])

# Drehkorrektur je Bauform (Grad, wird zur KiCad-Drehung addiert)
ROTATION_FIX = {
    "SOT-23": 180.0,                              # Q1–Q9
    "PinSocket_1x20_P2.54mm_Vertical": 90.0,      # J1, J2
    "PinHeader_1x08_P2.54mm_Vertical": 90.0,      # J4, J5
    "FanPinHeader_1x04_P2.54mm_Vertical": 180.0,  # J11–J16 (Molex 47053, laut JLCPCB-Freigabe 04.10.2026)
}
# Feste Werte aus der JLCPCB-Vorschau, wo eine Formel nicht reicht (Ref → (Drehung, dx, dy in mm))
# J3 (USB-C GCT USB4125): Drehung −90°, Bauteilmitte bei JLCPCB 1,231 mm weiter rechts als der KiCad-Bezugspunkt
# (Wert aus der Freigabe durch den JLCPCB-Ingenieur, 04.10.2026; dx/dy in mm, KiCad-Richtung, y nach unten).
MANUAL = {"J3": (-90.0, 1.231, 0.0)}


def norm(angle):
    a = angle % 360.0
    return a - 360.0 if a > 180.0 else a


board = pcbnew.LoadBoard(pcb)
with open(f"{out_dir}/CPL_JLCPCB.csv", "w", newline="", encoding="utf-8") as o:
    w = csv.writer(o)
    w.writerow(["Designator", "Mid X", "Mid Y", "Layer", "Rotation"])
    n = 0
    for fp in sorted(board.GetFootprints(), key=lambda f: f.GetReference()):
        ref = fp.GetReference()
        if ref not in lcsc_of:
            continue  # Befestigungslöcher, Messpunkte: nicht bestücken
        # Reine Durchsteckteile (Stecker, Leisten, Klemmen): Mitte der nummerierten Pads. SMD-Teile (auch die USB-C-Buchse
        # mit SMD-Kontakten): Bezugspunkt der Bauform, der dort schon die Gehäusemitte ist.
        pads = [p for p in fp.Pads() if p.GetNumber() not in ("", "SH")]
        if pads and all(p.GetAttribute() == pcbnew.PAD_ATTRIB_PTH for p in pads):
            x = sum(pcbnew.ToMM(p.GetPosition().x) for p in pads) / len(pads)
            y = sum(pcbnew.ToMM(p.GetPosition().y) for p in pads) / len(pads)
        else:
            x, y = pcbnew.ToMM(fp.GetPosition().x), pcbnew.ToMM(fp.GetPosition().y)
        rot = fp.GetOrientationDegrees() + ROTATION_FIX.get(fp_of[ref], 0.0)
        if ref in MANUAL:
            rot, dx, dy = MANUAL[ref]
            x, y = x + dx, y + dy
        w.writerow([ref, f"{x:.4f}mm", f"{-y:.4f}mm", "Top" if fp.GetLayer() == pcbnew.F_Cu else "Bottom", f"{norm(rot):.1f}"])
        n += 1
print("BOM:", len(groups), "Positionen,", len(rows), "Bauteile; CPL:", n, "Einträge")
