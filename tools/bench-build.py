#!/usr/bin/env python3
"""Benchmark-Vergleich mit der Community (0.9.26) – wird von .github/workflows/bench.yml stündlich aufgerufen.

Liest neue Einsendungen aus dem ntfy.sh-Thema (messages.jsonl, eine ntfy-Nachricht je Zeile), prüft sie gegen die Grenzen
der Geräteprofile und verwirft Ausreißer. Die angenommenen Einzelwerte bleiben privat (samples.jsonl, nur im Actions-Cache);
veröffentlicht wird nur stats.json mit zusammengefassten Werten je Modell (Anzahl, Quantile, Mediane) – keine Einzelwerte.

Aufruf: bench-build.py <samples.jsonl alt> <messages.jsonl> <DeviceProfiles.json> <samples.jsonl neu> <stats.json>
"""
import json
import statistics
import sys
import time

MAX_PER_RUN = 200          # Spam-Bremse: höchstens so viele neue Einsendungen je Lauf
KEEP_DAYS = 365            # ältere Einsendungen fallen aus der Statistik
MAX_SAMPLES = 50000


def load_jsonl(path):
    out = []
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    out.append(json.loads(line))
                except ValueError:
                    pass
    except FileNotFoundError:
        pass
    return out


def num(v, lo, hi):
    return isinstance(v, (int, float)) and not isinstance(v, bool) and lo <= v <= hi


def text(v, n):
    return v is None or (isinstance(v, str) and len(v) <= n and all(c.isprintable() for c in v))


def validate(m, profiles):
    """Einsendung prüfen; liefert das bereinigte Sample oder None."""
    if not isinstance(m, dict) or m.get("app") != "bitaxetuner" or m.get("v") != 1 or m.get("kind") != "bench":
        return None
    p = profiles.get(m.get("model"))
    if p is None:
        return None
    if not (text(m.get("asic"), 20) and text(m.get("fw"), 30)):
        return None
    freq, mv, gh, jth, temp = m.get("freq"), m.get("mv"), m.get("gh"), m.get("jth"), m.get("temp")
    if not num(freq, p["MinFrequencyMhz"] * 0.9, p["MaxFrequencyMhz"] * 1.1):
        return None
    if not num(mv, p["MinVoltageMv"] - 50, p["MaxVoltageMv"] + 50):
        return None
    if not (num(gh, 1, 100000) and num(jth, 5, 60) and num(temp, 20, 95)):
        return None
    # Leistung muss zum Modell passen (J/TH × TH/s = W)
    watts = jth * gh / 1000
    if watts > p.get("MaxPowerW", 1000) * 1.3:
        return None
    return {"model": m["model"], "asic": m.get("asic"), "fw": m.get("fw"), "freq": int(freq), "mv": int(mv),
            "gh": round(float(gh), 1), "jth": round(float(jth), 2), "temp": round(float(temp), 1)}


def quantile(sorted_vals, q):
    if not sorted_vals:
        return None
    pos = (len(sorted_vals) - 1) * q
    lo = int(pos)
    hi = min(lo + 1, len(sorted_vals) - 1)
    return sorted_vals[lo] + (sorted_vals[hi] - sorted_vals[lo]) * (pos - lo)


def stats_for(samples):
    jth = sorted(s["jth"] for s in samples)
    if len(jth) >= 10:   # grobe Ausreißer (3 × Interquartilsabstand) aus der Verteilung nehmen
        q1, q3 = quantile(jth, 0.25), quantile(jth, 0.75)
        iqr = q3 - q1
        keep = [v for v in jth if q1 - 3 * iqr <= v <= q3 + 3 * iqr]
        samples = [s for s in samples if q1 - 3 * iqr <= s["jth"] <= q3 + 3 * iqr]
        jth = keep
    return {
        "n": len(samples),
        "jth": [round(quantile(jth, q), 2) for q in (0.10, 0.25, 0.50, 0.75, 0.90)],
        "gh": round(statistics.median(s["gh"] for s in samples), 1),
        "temp": round(statistics.median(s["temp"] for s in samples), 1),
        "freq": round(statistics.median(s["freq"] for s in samples)),
        "mv": round(statistics.median(s["mv"] for s in samples)),
    }


def main(old_path, messages_path, profiles_path, samples_out, stats_out):
    with open(profiles_path, encoding="utf-8") as f:
        profiles = {p["Id"]: p for p in json.load(f)}
    now = int(time.time())
    samples = [s for s in load_jsonl(old_path) if isinstance(s, dict) and now - s.get("t", 0) <= KEEP_DAYS * 86400]
    seen = {(s["model"], s["freq"], s["mv"], s["gh"], s["jth"], s["temp"]) for s in samples if now - s.get("t", 0) <= 86400}
    accepted = rejected = 0
    for msg in load_jsonl(messages_path):
        if msg.get("event") != "message" or accepted >= MAX_PER_RUN:
            continue
        try:
            body = json.loads(msg.get("message", ""))
        except ValueError:
            rejected += 1
            continue
        s = validate(body, profiles)
        if s is None:
            rejected += 1
            continue
        key = (s["model"], s["freq"], s["mv"], s["gh"], s["jth"], s["temp"])
        if key in seen:
            continue
        seen.add(key)
        s["t"] = int(msg.get("time", now))
        samples.append(s)
        accepted += 1
    samples = samples[-MAX_SAMPLES:]
    by_model = {}
    for s in samples:
        by_model.setdefault(s["model"], []).append(s)
    stats = {"updated": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(now)),
             "models": {m: stats_for(v) for m, v in sorted(by_model.items())}}
    with open(samples_out, "w", encoding="utf-8") as f:
        for s in samples:
            f.write(json.dumps(s, separators=(",", ":")) + "\n")
    with open(stats_out, "w", encoding="utf-8") as f:
        json.dump(stats, f, ensure_ascii=False, indent=1)
    print(f"{accepted} angenommen, {rejected} verworfen, {len(samples)} gespeichert, {len(stats['models'])} Modelle")


if __name__ == "__main__":
    if len(sys.argv) != 6:
        sys.exit(__doc__)
    main(*sys.argv[1:])
