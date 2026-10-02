#!/bin/bash
# Gesamter Ablauf: Platzieren → DSN → Freerouting → Leiterbahnen + Masseflächen → DRC
set -e
K="${KICAD_DIR:?KICAD_DIR auf den KiCad-10-Ordner setzen}"
cd "$(dirname "$0")/out"
"$K/bin/python.exe" ../build_board.py bitaxetuner-fan-board.kicad_pcb
"$K/bin/python.exe" ../to_dsn.py bitaxetuner-fan-board.kicad_pcb board.dsn
rm -f board.ses
timeout 900 java -jar "${FREEROUTING_JAR:?FREEROUTING_JAR auf freerouting-1.9.0.jar setzen}" -de board.dsn -do board.ses -mp 40 > freerouting.log 2>&1
"$K/bin/python.exe" ../finish_board.py bitaxetuner-fan-board.kicad_pcb board.ses
"$K/bin/kicad-cli.exe" pcb drc --format json --severity-all -o drc.json bitaxetuner-fan-board.kicad_pcb > /dev/null 2>&1 || true
PYTHONIOENCODING=utf-8 python - <<'PY'
import json,collections
d=json.load(open('drc.json',encoding='utf-8'))
print(collections.Counter((v['severity'],v['type']) for v in d['violations']), 'offen:', len(d.get('unconnected_items',[])))
for v in d['violations']: print(' ', v['severity'], v['type'], v['description'], [i['description'] for i in v['items']][:2])
PY
