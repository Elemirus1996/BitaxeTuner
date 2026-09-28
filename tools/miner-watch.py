#!/usr/bin/env python3
"""Täglicher Check: neue Miner-Modelle in ESP-Miner (Bitaxe) und ESP-Miner-NerdQAxePlus (NerdAxe-Familie) finden,
die noch kein Geräteprofil haben, und in tools/new-miners.txt sammeln.

Übernommen werden sie NICHT automatisch – falsche Grenzwerte könnten einen Miner beschädigen. Beim nächsten Release
werden die gesammelten Modelle geprüft, in src/BitaxeTuner.Core/Profiles/DeviceProfiles.json übernommen und die
Liste geleert. Ausgabe (für GitHub Actions): Zeile „NEW=<Anzahl neu gefundener Einträge>“.

Aufruf: python tools/miner-watch.py [--dry-run]
"""
import datetime
import json
import os
import re
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILES = os.path.join(ROOT, 'src', 'BitaxeTuner.Core', 'Profiles', 'DeviceProfiles.json')
LIST = os.path.join(ROOT, 'tools', 'new-miners.txt')

ESP_MINER = 'https://raw.githubusercontent.com/bitaxeorg/ESP-Miner/master/main/device_config.h'
NERD_API = 'https://api.github.com/repos/shufps/ESP-Miner-NerdQAxePlus/contents/main/boards'

HEADER = """# Neue Miner-Modelle ohne Geräteprofil – gesammelt vom täglichen Check (.github/workflows/daily.yml).
# Werden beim nächsten Release geprüft, in DeviceProfiles.json übernommen und hier gelöscht.
# Format: <Kennung> | <Quelle> | <Eckdaten aus der Quelle> | gefunden am
"""


def fetch(url):
    headers = {'User-Agent': 'BitaxeTuner-miner-watch'}
    token = os.environ.get('GITHUB_TOKEN')
    if token and 'api.github.com' in url:
        headers['Authorization'] = f'Bearer {token}'
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as r:
        return r.read().decode('utf-8', errors='replace')


def esp_miner_boards():
    """board_version → Eckdaten der Familie (Name, ASIC, Anzahl, Leistungsbudget)."""
    src = fetch(ESP_MINER)
    families = {}
    for m in re.finditer(r'static const FamilyConfig (\w+)\s*=\s*\{(.*?)\};', src):
        body = m.group(2)
        def f(k):
            x = re.search(rf'\.{k}\s*=\s*([^,]+)', body)
            return x.group(1).strip().strip('"') if x else '?'
        families[m.group(1)] = {'name': f('name'), 'asic': f('asic').replace('ASIC_', ''),
                                'count': f('asic_count'), 'max_power': f('max_power')}
    boards = {}
    for m in re.finditer(r'\.board_version\s*=\s*"([^"]+)"\s*,\s*\.family\s*=\s*(\w+)(.*?)\}', src):
        fam = families.get(m.group(2), {'name': m.group(2), 'asic': '?', 'count': '?', 'max_power': '?'})
        target = re.search(r'\.power_consumption_target\s*=\s*(\d+)', m.group(3))
        boards[m.group(1)] = (fam['name'], fam['asic'],
                              f"{fam['name']} · {fam['asic']} × {fam['count']} · Leistungsbudget {fam['max_power']} W"
                              + (f" · Ziel {target.group(1)} W" if target else ''))
    return boards


def nerd_boards():
    """deviceModel → Eckdaten aus der Board-Datei (ASIC, Anzahl, Spannungen, Leistung)."""
    files = [(x['name'], x['download_url']) for x in json.loads(fetch(NERD_API))
             if x['name'].endswith('.cpp') and x['name'] != 'board.cpp']
    boards = {}
    for name, url in files:
        src = fetch(url)
        model = re.search(r'm_deviceModel\s*=\s*"([^"]+)"', src)
        if not model:
            continue
        def v(k):
            x = re.search(rf'{k}\s*=\s*([^;]+);', src)
            return x.group(1).strip().strip('"') if x else '?'
        voltage = re.search(r'm_defaultAsicVoltageMillis\s*=[^;]*?(\d{3,4})\s*;', src)
        boards[model.group(1)] = (model.group(1), v('m_asicModel'),
                                  f"{v('m_asicModel')} × {v('m_asicCount')} · Spannung Standard {voltage.group(1) if voltage else '?'} mV"
                                  f" (max. {v('m_absMaxAsicVoltageMillis')}) · Eingang {v('m_minPin')}–{v('m_maxPin')} W · Datei {name}")
    return boards


def chip(asic):
    """„ASIC_BM1370XP“, „BM1370_HEX“ → „BM1370“."""
    m = re.search(r'BM\d{4}', asic or '')
    return m.group(0) if m else (asic or '?')


def profile_for(profiles, model, board):
    """Wie ProfileRegistry.FindProfile in der App: erst deviceModel (Teilstring, längster Treffer), dann Board (Präfix)."""
    if model:
        hits = [(len(m), p) for p in profiles for m in p.get('DeviceModelMatches') or [] if m.lower() in model.lower()]
        if hits:
            return max(hits, key=lambda x: x[0])[1]
    if board:
        hits = [(len(b), p) for p in profiles for b in p.get('BoardVersions') or [] if board.lower().startswith(b.lower())]
        if hits:
            return max(hits, key=lambda x: x[0])[1]
    return None


def check(profiles, key, source, model, board, asic, info, candidates):
    p = profile_for(profiles, model, board)
    if p is None:
        candidates[key] = (source, info + ' · derzeit generisches Profil')
    elif chip(asic) != '?' and chip(p.get('AsicModel')) != chip(asic):
        candidates[key] = (source, info + f" · würde als „{p['Name']}“ erkannt – ASIC passt nicht")


def main():
    dry = '--dry-run' in sys.argv
    profiles = json.load(open(PROFILES, encoding='utf-8-sig'))
    candidates = {}
    for board, (family, asic, info) in esp_miner_boards().items():
        # ESP-Miner meldet als deviceModel den Familiennamen
        check(profiles, f'ESP-Miner Board {board}', 'bitaxeorg/ESP-Miner', family, board, asic, info, candidates)
    for model, (_, asic, info) in nerd_boards().items():
        check(profiles, f'NerdQAxe deviceModel {model}', 'shufps/ESP-Miner-NerdQAxePlus', model, None, asic, info, candidates)

    existing = open(LIST, encoding='utf-8').read() if os.path.exists(LIST) else HEADER
    listed = {line.split(' | ')[0] for line in existing.splitlines() if line and not line.startswith('#')}
    today = datetime.date.today().isoformat()
    new = [f'{key} | {src} | {info} | {today}' for key, (src, info) in sorted(candidates.items()) if key not in listed]

    for line in new:
        print('NEU:', line)
    if new and not dry:
        with open(LIST, 'w', encoding='utf-8', newline='\n') as f:
            f.write(existing.rstrip('\n') + '\n' + '\n'.join(new) + '\n')
    print(f'NEW={len(new)}')
    print(f'OFFEN={len(listed) + len(new)}')


if __name__ == '__main__':
    main()
