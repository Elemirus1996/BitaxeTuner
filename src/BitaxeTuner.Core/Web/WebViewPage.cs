using System.Net;

namespace BitaxeTuner.Core.Web;

/// <summary>Seiten der Handy-Ansicht (ohne externe Abhängigkeiten, CSP-tauglich: kein Inline-Skript).</summary>
public static class WebViewPage
{
    private const string Head = """
        <!doctype html><html lang="de"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="color-scheme" content="dark light">
        <title>BitaxeTuner</title><link rel="stylesheet" href="/style.css"></head>
        """;

    public static string Login(string? error) => Head + $"""
        <body><main class="login">
        <h1>BitaxeTuner</h1>
        <p class="muted">Nur-Lese-Ansicht. PIN wie in den Einstellungen der App festgelegt.</p>
        <form method="post" action="/login">
          <input name="pin" type="password" inputmode="numeric" autocomplete="current-password" placeholder="PIN" autofocus required>
          <button type="submit">Anmelden</button>
        </form>
        {(error is null ? "" : $"<p class=\"error\">{WebUtility.HtmlEncode(error)}</p>")}
        </main></body></html>
        """;

    public const string Dashboard = Head + """
        <body>
        <header><h1>BitaxeTuner</h1><span id="updated" class="muted">lädt …</span><a href="/logout">Abmelden</a></header>
        <section id="total" class="total"></section>
        <p id="price" class="muted"></p>
        <section id="miners" class="grid"></section>
        <p class="muted small">Aktualisiert alle 10 s · nur Anzeige, Änderungen nur in der App am PC.</p>
        <script src="/app.js"></script>
        </body></html>
        """;

    public const string Style = """
        :root { --bg:#0f1216; --card:#171c24; --line:#242c38; --fg:#e9eef6; --muted:#7c879b; --ok:#4ec9a0; --warn:#e0b44a; --bad:#d95b5b; --accent:#f7931a; }
        @media (prefers-color-scheme: light) { :root { --bg:#f3f4f6; --card:#fff; --line:#e5e7eb; --fg:#111827; --muted:#6b7280; --ok:#16a34a; --warn:#b7791f; --bad:#dc2626; --accent:#c96f00; } }
        * { box-sizing:border-box; }
        body { margin:0; padding:16px; background:var(--bg); color:var(--fg); font:15px/1.4 system-ui, -apple-system, "Segoe UI", sans-serif; }
        header { display:flex; align-items:baseline; gap:12px; flex-wrap:wrap; margin-bottom:12px; }
        header h1 { font-size:20px; margin:0; color:var(--accent); }
        header a { margin-left:auto; color:var(--muted); }
        .muted { color:var(--muted); } .small { font-size:12px; }
        .total, .card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:12px; }
        .total { display:grid; grid-template-columns:repeat(auto-fit, minmax(120px, 1fr)); gap:8px; margin-bottom:8px; }
        .grid { display:grid; grid-template-columns:repeat(auto-fit, minmax(280px, 1fr)); gap:12px; }
        .k { font-size:11px; text-transform:uppercase; color:var(--muted); } .v { font-size:20px; font-weight:600; }
        .card h2 { font-size:16px; margin:0 0 6px; display:flex; align-items:center; gap:8px; }
        .dot { width:10px; height:10px; border-radius:50%; display:inline-block; }
        .row { display:grid; grid-template-columns:1fr 1fr; gap:6px 12px; margin:8px 0; }
        .ok { background:var(--ok); } .bad { background:var(--bad); } .warn { color:var(--warn); }
        svg { width:100%; height:48px; display:block; } .spark { color:var(--ok); }
        .login { max-width:320px; margin:15vh auto; text-align:center; }
        input, button { width:100%; font-size:18px; padding:12px; margin:6px 0; border-radius:8px; border:1px solid var(--line); background:var(--card); color:var(--fg); }
        button { background:var(--accent); color:#fff; border:none; font-weight:600; }
        .error { color:var(--bad); }
        """;

    public const string Script = """
        const de = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 1 });
        const hash = gh => gh >= 1000 ? (gh / 1000).toLocaleString('de-DE', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + ' TH/s' : de.format(gh) + ' GH/s';
        const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));
        const tile = (k, v) => `<div><div class="k">${k}</div><div class="v">${v}</div></div>`;
        function spark(points) {
          if (!points || points.length < 2) return '';
          const min = Math.min(...points), max = Math.max(...points), r = (max - min) || 1;
          const d = points.map((p, i) => `${(i / (points.length - 1) * 100).toFixed(1)},${(46 - (p - min) / r * 42).toFixed(1)}`).join(' ');
          return `<svg viewBox="0 0 100 48" preserveAspectRatio="none"><polyline fill="none" stroke="currentColor" stroke-width="1.5" vector-effect="non-scaling-stroke" points="${d}"/></svg>`;
        }
        async function load() {
          const r = await fetch('/api/status', { cache: 'no-store' });
          if (r.status === 401) { location.href = '/'; return; }
          const s = await r.json();
          document.getElementById('updated').textContent = 'Stand ' + s.time;
          document.getElementById('total').innerHTML =
            tile('Hashrate', hash(s.total.hashrate)) + tile('Leistung', de.format(s.total.power) + ' W') +
            tile('Effizienz', s.total.efficiency ? de.format(s.total.efficiency) + ' J/TH' : '–') +
            tile('Online', s.total.online + '/' + s.total.count) + tile('Temp max', de.format(s.total.maxTemp) + ' °C');
          document.getElementById('price').textContent = s.price ? `Strompreis (${s.price.source}): ${de.format(s.price.ct)} ct/kWh` : '';
          document.getElementById('miners').innerHTML = s.miners.map(m => `
            <article class="card">
              <h2><span class="dot ${m.online ? 'ok' : 'bad'}"></span>${esc(m.name)}</h2>
              ${m.online ? `
              <div class="row">
                ${tile('Hashrate', hash(m.hashrate))}${tile('Temp / VR', de.format(m.temp) + ' / ' + de.format(m.vrTemp) + ' °C')}
                ${tile('Leistung', de.format(m.power) + ' W')}${tile('Effizienz', m.efficiency ? de.format(m.efficiency) + ' J/TH' : '–')}
                ${tile('Frequenz', m.frequency + ' MHz')}${tile('Spannung', m.voltage + ' mV')}
              </div>
              <div class="spark">${spark(m.history)}</div>
              <div class="small muted">${esc(m.pool)}</div>` : `<p class="muted">${esc(m.error || 'offline')}</p>`}
              ${m.lastTuning ? `<div class="small">Letzte Änderung: ${esc(m.lastTuning)}</div>` : ''}
              ${m.automation ? `<div class="small muted">${esc(m.automation)}</div>` : ''}
              ${m.soak ? `<div class="small warn">${esc(m.soak)}</div>` : ''}
            </article>`).join('');
        }
        load().catch(() => {}); setInterval(() => load().catch(() => {}), 10000);
        """;
}
