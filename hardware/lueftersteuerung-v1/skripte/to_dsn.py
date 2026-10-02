# Platine → Specctra-DSN für Freerouting; Stromnetze als eigene Klasse mit breiten Bahnen.
import re
import sys
import pcbnew

pcb, dsn = sys.argv[1], sys.argv[2]
board = pcbnew.LoadBoard(pcb)
if not pcbnew.ExportSpecctraDSN(board, dsn):
    raise SystemExit("DSN-Export fehlgeschlagen")

text = open(dsn, encoding="utf-8").read()
unit = re.search(r"\(resolution (\w+) (\d+)\)", text)
print("Auflösung:", unit.group(0) if unit else "?")
# Breiten in DSN-Einheiten (um): Strom 1,0 mm, GND 0,6 mm, Rest 0,3 mm aus der Standardklasse
POWER = ["+5V_FAN", "VIN", "VIN_F"]
GROUND = ["GND"]


def strip_nets(nets):
    global text
    for n in nets:
        # aus der Standardklasse entfernen (KiCad schreibt Netznamen dort ohne Anführungszeichen)
        for q in (n, f'"{n}"'):
            text = re.sub(r'(\(class kicad_default[^()]*?)\s' + re.escape(q) + r'(?=[\s)])', r'\1', text, count=1)


def quote(n):
    return n


strip_nets(POWER + GROUND)
extra = (
    f'\n    (class power {" ".join(quote(n) for n in POWER)}\n'
    '      (circuit (use_via "Via[0-1]_700:350_um"))\n'
    '      (rule (width 1000) (clearance 200))\n    )'
    f'\n    (class ground {" ".join(quote(n) for n in GROUND)}\n'
    '      (circuit (use_via "Via[0-1]_700:350_um"))\n'
    '      (rule (width 600) (clearance 200))\n    )'
)
i = text.rindex("(class kicad_default")
depth, j = 0, i
while True:
    if text[j] == "(":
        depth += 1
    elif text[j] == ")":
        depth -= 1
        if depth == 0:
            break
    j += 1
text = text[: j + 1] + extra + text[j + 1:]
open(dsn, "w", encoding="utf-8").write(text)
print("ok")
