#!/usr/bin/env python3
"""Download-Zahlen aller Releases (GitHub-API) als Kurzbericht für ntfy, mit Veränderung seit dem letzten Lauf.

Aufruf: python tools/download-report.py <state.json>
Ausgabe: erste Zeile = Titel (nur ASCII, für den HTTP-Kopf), danach der Text. Die Zustandsdatei wird aktualisiert.
Hinweis: Automatische Updates von Desktop-App und Server laden ebenfalls aus den Releases und zählen mit.
"""
import json
import os
import sys
import urllib.request

REPO = os.environ.get('GITHUB_REPOSITORY', 'Elemirus1996/BitaxeTuner')

KINDS = [  # (Anzeige, Erkennung im Dateinamen)
    ('Desktop Windows', lambda n: n.startswith('BitaxeTuner-Setup-') or '-portable-win-' in n),
    ('Server Pi-Image', lambda n: n.endswith('-pi-arm64.img.xz')),
    ('Server Linux/Pi-Paket', lambda n: n.startswith('BitaxeTuner-Server-') and n.endswith('.tar.gz')),
    ('Server Windows', lambda n: n.startswith('BitaxeTuner-Server-Setup-')),
]


def api(path):
    headers = {'User-Agent': 'BitaxeTuner-download-report', 'Accept': 'application/vnd.github+json'}
    if os.environ.get('GITHUB_TOKEN'):
        headers['Authorization'] = f"Bearer {os.environ['GITHUB_TOKEN']}"
    with urllib.request.urlopen(urllib.request.Request(f'https://api.github.com{path}', headers=headers), timeout=30) as r:
        return json.load(r)


def main():
    state_file = sys.argv[1]
    releases = api(f'/repos/{REPO}/releases?per_page=100')
    per_kind = {k: 0 for k, _ in KINDS}
    total = 0
    latest_tag, latest_total = None, 0
    for rel in releases:
        rel_total = 0
        for a in rel.get('assets', []):
            name, count = a['name'], a['download_count']
            for kind, match in KINDS:
                if match(name):
                    per_kind[kind] += count
                    rel_total += count
                    break
        total += rel_total
        if latest_tag is None and not rel.get('draft') and not rel.get('prerelease'):
            latest_tag, latest_total = rel['tag_name'], rel_total

    try:
        prev = json.load(open(state_file, encoding='utf-8'))
    except (OSError, ValueError):
        prev = {}
    delta = total - prev['total'] if 'total' in prev else None

    title = f'BitaxeTuner: {total} Downloads' + (f' (+{delta})' if delta is not None else '')
    lines = [f'Gesamt: {total}' + (f' · seit gestern +{delta}' if delta is not None else ' · erster Bericht'),
             f'Aktuelle Version {latest_tag}: {latest_total}' if latest_tag else 'Noch kein Release']
    for kind, _ in KINDS:
        d = per_kind[kind] - prev.get('kinds', {}).get(kind, 0) if 'kinds' in prev else None
        lines.append(f'{kind}: {per_kind[kind]}' + (f' (+{d})' if d else ''))
    lines.append('(ohne Docker-Pulls; automatische Updates zählen mit)')

    os.makedirs(os.path.dirname(os.path.abspath(state_file)), exist_ok=True)
    json.dump({'total': total, 'kinds': per_kind}, open(state_file, 'w', encoding='utf-8'))
    print(title)
    print('\n'.join(lines))


if __name__ == '__main__':
    main()
