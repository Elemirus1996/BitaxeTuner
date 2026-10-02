# Stückliste (BOM) und Bestückungsliste (CPL) im JLCPCB-Format aus der Platine und der Bauteil-Tabelle des Generators.
import csv
import sys
from collections import OrderedDict

raw, pos_csv, out_dir = sys.argv[1], sys.argv[2], sys.argv[3]
rows = [line.rstrip("\n").split("\t") for line in open(raw, encoding="utf-8") if line.strip()]
rows = [r for r in rows if r[3]]   # ohne LCSC-Nummer (Messpunkte = nur Kupfer) nicht bestücken
lcsc_of = {r[0]: r[3] for r in rows}
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

with open(pos_csv, encoding="utf-8") as f, open(f"{out_dir}/CPL_JLCPCB.csv", "w", newline="", encoding="utf-8") as o:
    w = csv.writer(o)
    w.writerow(["Designator", "Mid X", "Mid Y", "Layer", "Rotation"])
    n = 0
    for r in csv.DictReader(f):
        if r["Ref"] not in lcsc_of:
            continue  # Befestigungslöcher, Messpunkte: nicht bestücken
        w.writerow([r["Ref"], f'{float(r["PosX"]):.4f}mm', f'{float(r["PosY"]):.4f}mm',
                    "Top" if r["Side"].lower() == "top" else "Bottom", f'{float(r["Rot"]):.1f}'])
        n += 1
print("BOM:", len(groups), "Positionen,", len(rows), "Bauteile; CPL:", n, "Einträge")
