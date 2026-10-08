#!/usr/bin/env python3
"""Neuigkeiten aus der Solo-Mining-Welt für die E-Paper-Seite „Neuigkeiten“ sammeln (GitHub Action news.yml).

Quellen (nur öffentliche Daten, keine Nutzerdaten):
  firmware    – neue stabile Releases von ESP-Miner (Bitaxe) und ESP-Miner-NerdQAxePlus (NerdAxe-Familie)
  miner       – neue Miner-Modelle (tools/new-miners.txt aus dem täglichen Check)
  solo        – Solo-Blockfunde: BTC einzeln über mempool.space (Solo CK, Public Pool, Braiins Solo),
                BCH als Tagessumme über Blockchair („solo“ im Coinbase-Text)
  network     – Difficulty-Anpassungen im Bitcoin-Netzwerk (mempool.space)
  bitaxetuner – neue BitaxeTuner-Versionen und Einträge aus tools/news-manual.json

Die alte news.json wird fortgeschrieben (Funde bleiben erhalten, auch wenn die Quelle sie nicht mehr liefert);
behalten werden höchstens 60 Einträge der letzten 120 Tage. Fällt eine Quelle aus, bleiben ihre bisherigen Einträge.

Aufruf: python tools/news-build.py <alte news.json oder -> <neue news.json>
"""
import datetime
import json
import os
import re
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NEW_MINERS = os.path.join(ROOT, 'tools', 'new-miners.txt')
MANUAL = os.path.join(ROOT, 'tools', 'news-manual.json')

FIRMWARE = [('bitaxeorg/ESP-Miner', 'ESP-Miner (Bitaxe)'), ('shufps/ESP-Miner-NerdQAxePlus', 'ESP-Miner NerdQAxe+')]
OWN_REPO = os.environ.get('GITHUB_REPOSITORY', 'Elemirus1996/BitaxeTuner')
SOLO_POOLS = ['solock', 'publicpool', 'braiinssolo']
KEEP_DAYS = 120
KEEP_ITEMS = 60
NOW = datetime.datetime.now(datetime.timezone.utc)


def fetch(url):
    headers = {'User-Agent': 'BitaxeTuner-news', 'Accept': 'application/json'}
    token = os.environ.get('GITHUB_TOKEN')
    if token and 'api.github.com' in url:
        headers['Authorization'] = f'Bearer {token}'
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as r:
        return json.loads(r.read().decode('utf-8', errors='replace'))


def iso(ts):
    if isinstance(ts, (int, float)):
        ts = datetime.datetime.fromtimestamp(ts, datetime.timezone.utc)
    elif isinstance(ts, str):
        ts = datetime.datetime.fromisoformat(ts.replace('Z', '+00:00'))
        if ts.tzinfo is None:
            ts = ts.replace(tzinfo=datetime.timezone.utc)
    return ts.astimezone(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')


def item(id_, date, kind, de, en, text_de='', text_en='', url='', coin=None):
    x = {'id': id_, 'date': iso(date), 'kind': kind, 'title': {'de': de, 'en': en}, 'text': {'de': text_de, 'en': text_en}, 'url': url}
    if coin:
        x['coin'] = coin
    return x


def num_de(v, digits):
    return f'{v:,.{digits}f}'.replace(',', ' ').replace('.', ',')


def firmware():
    out = []
    for repo, name in FIRMWARE:
        for r in fetch(f'https://api.github.com/repos/{repo}/releases?per_page=10'):
            if r.get('draft') or r.get('prerelease') or not r.get('published_at'):
                continue
            tag = r['tag_name']
            out.append(item(f'fw-{repo}-{tag}', r['published_at'], 'firmware',
                            f'Firmware {name} {tag}', f'Firmware {name} {tag}',
                            'Neue stabile Version erschienen.', 'New stable release.', r.get('html_url', '')))
    return out


def own_releases():
    out = []
    releases = [r for r in fetch(f'https://api.github.com/repos/{OWN_REPO}/releases?per_page=10')
                if not r.get('draft') and not r.get('prerelease') and r.get('published_at')]
    for r in releases[:2]:   # nur die neuesten – sonst füllen eigene Versionen die Seite
        tag = r['tag_name']
        out.append(item(f'bt-{tag}', r['published_at'], 'bitaxetuner', f'BitaxeTuner {tag} erschienen', f'BitaxeTuner {tag} released',
                        'Update über Einstellungen → Server-Update bzw. die App.', 'Update via Settings → Server update or the app.',
                        r.get('html_url', '')))
    if os.path.exists(MANUAL):
        for m in json.load(open(MANUAL, encoding='utf-8')):
            out.append(item(f"manual-{m['id']}", m['date'], m.get('kind', 'bitaxetuner'), m['title']['de'], m['title']['en'],
                            m.get('text', {}).get('de', ''), m.get('text', {}).get('en', ''), m.get('url', '')))
    return out


def new_miners():
    """Zeilen „<Kennung> | <Quelle> | <Eckdaten> | gefunden am“ aus dem täglichen Check."""
    out = []
    if not os.path.exists(NEW_MINERS):
        return out
    for line in open(NEW_MINERS, encoding='utf-8'):
        if line.startswith('#') or '|' not in line:
            continue
        parts = [p.strip() for p in line.split('|')]
        if len(parts) < 4:
            continue
        model, source, facts, found = parts[:4]
        try:
            date = datetime.datetime.strptime(found, '%Y-%m-%d').replace(hour=12, tzinfo=datetime.timezone.utc)
        except ValueError:
            continue
        facts = re.sub(r'\s*·\s*Datei .*$', '', facts)
        out.append(item(f'miner-{model}', date, 'miner', f'Neues Miner-Modell: {model}', f'New miner model: {model}',
                        f'{facts} ({source})', f'{facts} ({source})'))
    return out


def solo_btc():
    out = []
    for slug in SOLO_POOLS:
        for b in fetch(f'https://mempool.space/api/v1/mining/pool/{slug}/blocks')[:10]:
            pool = b.get('extras', {}).get('pool', {}).get('name', slug)
            reward = b.get('extras', {}).get('reward', 0) / 1e8
            h = b['height']
            out.append(item(f'solo-btc-{h}', b['timestamp'], 'solo', f'Solo-Block BTC #{h} – {pool}', f'Solo block BTC #{h} – {pool}',
                            f'Belohnung {num_de(reward, 3)} BTC', f'Reward {reward:,.3f} BTC', f"https://mempool.space/block/{b['id']}", 'BTC'))
    return out


def solo_bch(day=None):
    """BCH: Solo-Funde sind dort häufig (oft gemietete Rechenleistung) – daher ein Eintrag je Tag mit Anzahl und Pools."""
    day = day or (NOW - datetime.timedelta(days=1)).date()
    blocks = []
    for offset in (0, 100):
        blocks += fetch(f'https://api.blockchair.com/bitcoin-cash/blocks?q=time({day.isoformat()})&limit=100&offset={offset}')['data']
    counts = {}
    for b in blocks:
        text = bytes.fromhex(b.get('coinbase_data_hex') or '').decode('latin1', 'replace')
        if not re.search(r'solo', text, re.I):
            continue
        tag = re.search(r'/([^/]*solo[^/]*)/', text, re.I) or re.search(r'([A-Za-z.]*solo[A-Za-z.]*)', text, re.I)
        who = re.sub(r'[^ -~]', '', tag.group(1)).strip() if tag else 'solo'
        who = 'solo.ckpool' if who.lower() == 'solo mined bch' else who
        counts[who] = counts.get(who, 0) + 1
    if not counts:
        return []
    total = sum(counts.values())
    pools = ', '.join(f'{k} {v}' for k, v in sorted(counts.items(), key=lambda kv: -kv[1])[:4])
    date = datetime.datetime.combine(day, datetime.time(12, 0), datetime.timezone.utc)
    return [item(f'solo-bch-day-{day.isoformat()}', date, 'solo',
                 f'BCH: {total} Solo-Blöcke am {day.strftime("%d.%m.")}', f'BCH: {total} solo blocks on {day.strftime("%b %d")}',
                 pools, pools, f'https://blockchair.com/bitcoin-cash/blocks?q=time({day.isoformat()})', 'BCH')]


def network():
    out = []
    for ts, height, difficulty, change in fetch('https://mempool.space/api/v1/mining/difficulty-adjustments/3m')[:6]:
        pct = (change - 1) * 100 if change > 0.5 else change   # mempool liefert das Verhältnis (z. B. 1.035)
        sign = '+' if pct >= 0 else '−'
        digits = 2 if abs(pct) < 0.1 else 1
        out.append(item(f'diff-{height}', ts, 'network', f'Difficulty-Anpassung {sign}{abs(pct):.{digits}f} %'.replace('.', ','),
                        f'Difficulty adjustment {sign}{abs(pct):.{digits}f} %', f'Block #{height} · {difficulty / 1e12:.1f} T'.replace('.', ','),
                        f'Block #{height} · {difficulty / 1e12:.1f} T', f'https://mempool.space/block/{height}', 'BTC'))
    return out


def main():
    old_path, new_path = sys.argv[1], sys.argv[2]
    old = []
    if old_path != '-' and os.path.exists(old_path):
        try:
            old = json.load(open(old_path, encoding='utf-8')).get('items', [])
        except (ValueError, OSError):
            old = []
    items = {x['id']: x for x in old}
    sources = {'firmware': firmware, 'bitaxetuner': own_releases, 'miner': new_miners, 'solo-btc': solo_btc,
               'solo-bch': solo_bch, 'network': network}
    failed = []
    for name, fn in sources.items():
        try:
            for x in fn():
                items[x['id']] = x
        except Exception as e:   # eine ausgefallene Quelle hält die anderen nicht auf
            failed.append(f'{name}: {e}')
    cutoff = (NOW - datetime.timedelta(days=KEEP_DAYS)).strftime('%Y-%m-%dT%H:%M:%SZ')
    keep = sorted((x for x in items.values() if x['date'] >= cutoff), key=lambda x: x['date'], reverse=True)[:KEEP_ITEMS]
    with open(new_path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump({'version': 1, 'updated': iso(NOW), 'items': keep}, f, ensure_ascii=False, indent=1)
        f.write('\n')
    print(f'ITEMS={len(keep)}')
    for x in failed:
        print('FEHLER:', x)


if __name__ == '__main__':
    main()
