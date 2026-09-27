/* BitaxeTuner – Browser-Oberfläche des Servers.
   Kein Build-Schritt, keine externen Bibliotheken (läuft offline im Heimnetz, CSP "script-src 'self'").
   Texte aus Geräten/Konfiguration werden nie als HTML eingefügt (nur textContent). */
'use strict';

const S = { role: 'None', csrf: null, status: null, info: null, es: null, logEs: null, detail: null, route: null, chartRange: '1h' };
const de = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 2 });
const $ = (sel, root = document) => root.querySelector(sel);
const isAdmin = () => S.role === 'Admin';

// ---------- DOM ----------

function h(tag, props, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else if (k === 'style') el.style.cssText = v;
    else if ((k === 'value' || k === 'checked' || k === 'disabled' || k === 'selected') ) el[k] = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat(Infinity)) {
    if (kid == null || kid === false) continue;
    el.append(kid instanceof Node ? kid : String(kid));
  }
  return el;
}
const mount = (...nodes) => { const app = $('#app'); app.replaceChildren(...nodes); };

// ---------- Formatierung ----------

const n = (v, d = 1) => v == null || Number.isNaN(v) ? '–' : new Intl.NumberFormat('de-DE', { minimumFractionDigits: d, maximumFractionDigits: d }).format(v);
function hash(gh) {
  if (gh == null) return '–';
  return gh >= 1000 ? `${n(gh / 1000, 2)} TH/s` : `${n(gh, 0)} GH/s`;
}
function dur(sec) {
  if (sec == null) return '–';
  const d = Math.floor(sec / 86400), hh = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60);
  return d > 0 ? `${d} T ${hh} h` : `${hh} h ${m} min`;
}
const time = t => t ? new Date(t).toLocaleString('de-DE', { dateStyle: 'short', timeStyle: 'short' }) : '–';

// ---------- API ----------

class ApiError extends Error { constructor(m, s) { super(m); this.status = s; } }

async function api(path, { method = 'GET', body } = {}) {
  const headers = { Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (method !== 'GET' && S.csrf) headers['X-CSRF-Token'] = S.csrf;
  const r = await fetch('/api/v1' + path, { method, headers, credentials: 'same-origin', body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (r.status === 401 && !['/login', '/setup'].includes(path)) {
    S.role = 'None';
    stopEvents();
    renderLogin();
    throw new ApiError('Bitte anmelden.', 401);
  }
  if (!r.ok) throw new ApiError(data?.error || `Fehler ${r.status}`, r.status);
  return data;
}

/** Aufruf mit Fehlermeldung als Hinweis; liefert null bei Fehler. */
async function run(fn, okText) {
  try {
    const r = await fn();
    if (okText) toast(okText, 'ok');
    return r ?? true;
  } catch (e) {
    if (e.status !== 401) toast(e.message, 'error');
    return null;
  }
}

// ---------- Hinweise und Dialoge ----------

function toast(text, kind = 'info', ms = 6000) {
  const t = h('div', { class: `toast ${kind}` }, text);
  $('#toasts').append(t);
  setTimeout(() => t.remove(), ms);
}

/** Bestätigung mit Text (z. B. alter → neuer Wert vom Server). */
function confirmBox(title, body, okLabel = 'Bestätigen', danger = false) {
  const dlg = $('#dialog');
  $('#dialog-title').textContent = title;
  const b = $('#dialog-body');
  b.classList.toggle('html', body instanceof Node);
  b.replaceChildren(body instanceof Node ? body : document.createTextNode(body));
  const ok = $('#dialog-ok');
  ok.textContent = okLabel;
  ok.className = `btn ${danger ? 'danger' : 'primary'}`;
  dlg.returnValue = '';
  dlg.showModal();
  return new Promise(res => dlg.addEventListener('close', () => res(dlg.returnValue === 'ok'), { once: true }));
}

// ---------- Start, Anmeldung ----------

async function boot() {
  applyTheme(localStorageGet('theme'));
  $('#theme').addEventListener('click', () => {
    const next = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
    localStorageSet('theme', next);
    applyTheme(next);
  });
  $('#logout').addEventListener('click', async () => {
    await run(() => api('/logout', { method: 'POST' }));
    S.role = 'None'; S.csrf = null;
    stopEvents();
    renderLogin();
  });
  window.addEventListener('hashchange', route);

  S.info = await api('/info');
  $('#version').textContent = 'v' + S.info.version;
  if (S.info.setupRequired) return renderSetup();
  const session = await api('/session');
  S.role = session.role; S.csrf = session.csrf;
  if (S.role === 'None') return renderLogin();
  started();
}

function localStorageGet(k) { try { return localStorage.getItem('bt.' + k); } catch { return null; } }
function localStorageSet(k, v) { try { localStorage.setItem('bt.' + k, v); } catch { /* egal */ } }
function applyTheme(t) {
  if (t === 'light' || t === 'dark') document.documentElement.dataset.theme = t;
  else delete document.documentElement.dataset.theme;
}

function renderSetup() {
  $('#nav').hidden = true;
  const code = h('input', { autocomplete: 'one-time-code', placeholder: '1234-5678-9012' });
  const pw = h('input', { type: 'password', autocomplete: 'new-password' });
  const pw2 = h('input', { type: 'password', autocomplete: 'new-password' });
  const submit = async e => {
    e.preventDefault();
    if (pw.value !== pw2.value) return toast('Die Passwörter stimmen nicht überein.', 'error');
    const r = await run(() => api('/setup', { method: 'POST', body: { code: code.value, password: pw.value } }));
    if (r) { S.role = r.role; S.csrf = r.csrf; S.info.setupRequired = false; toast('Server eingerichtet.', 'ok'); started(); }
  };
  mount(h('div', { class: 'login' }, h('form', { class: 'card stack', onsubmit: submit },
    h('h2', {}, 'Server einrichten'),
    h('p', { class: 'muted' }, 'Den Einrichtungs-Code findest du im Protokoll des Dienstes (Raspberry Pi: „journalctl -u bitaxetuner“, Docker: „docker logs bitaxetuner“, Windows: Ereignisanzeige bzw. Konsole).'),
    h('div', {}, h('label', {}, 'Einrichtungs-Code'), code),
    h('div', {}, h('label', {}, 'Admin-Passwort (mind. 10 Zeichen)'), pw),
    h('div', {}, h('label', {}, 'Passwort wiederholen'), pw2),
    h('button', { class: 'btn primary', type: 'submit' }, 'Einrichten'))));
  code.focus();
}

function renderLogin() {
  $('#nav').hidden = true;
  $('#logout').hidden = true;
  $('#role').textContent = '';
  const pw = h('input', { type: 'password', autocomplete: 'current-password' });
  const submit = async e => {
    e.preventDefault();
    const r = await run(() => api('/login', { method: 'POST', body: { password: pw.value } }));
    if (r) { S.role = r.role; S.csrf = r.csrf; started(); }
  };
  mount(h('div', { class: 'login' }, h('form', { class: 'card stack', onsubmit: submit },
    h('h2', {}, 'Anmelden'),
    h('p', { class: 'muted' }, 'Admin-Passwort oder PIN der Ansicht („Nur ansehen“).'),
    h('div', {}, h('label', {}, 'Passwort oder PIN'), pw),
    h('button', { class: 'btn primary', type: 'submit' }, 'Anmelden'))));
  pw.focus();
}

function started() {
  $('#nav').hidden = false;
  $('#logout').hidden = false;
  $('#role').textContent = isAdmin() ? 'Admin' : 'Nur ansehen';
  document.querySelectorAll('[data-admin]').forEach(e => { e.hidden = !isAdmin(); });
  startEvents();
  route();
}

// ---------- Live-Ereignisse ----------

function startEvents() {
  stopEvents();
  const es = new EventSource('/api/v1/events');
  S.es = es;
  es.onopen = () => $('#live').classList.add('on');
  es.onerror = () => $('#live').classList.remove('on');
  es.addEventListener('status', e => { S.status = JSON.parse(e.data); onStatus(); });
  es.addEventListener('benchmark', e => {
    const d = JSON.parse(e.data);
    if (S.route?.view === 'device' && S.route.id === d.id) updateBenchmark(d.run);
  });
  es.addEventListener('device', e => {
    const d = JSON.parse(e.data);
    if (S.route?.view === 'device' && S.route.id === d.id) reloadDetailSoon();
  });
  es.addEventListener('log', e => {
    const d = JSON.parse(e.data);
    if (S.route?.view === 'device' && S.route.id === d.id) appendAppLog(d.line);
  });
  es.addEventListener('tuning', e => {
    const d = JSON.parse(e.data);
    toast(`Tuning (${d.source}): ${d.change}`);
    if (S.route?.view === 'device' && S.route.id === d.id) reloadDetailSoon();
  });
  es.addEventListener('notification', e => { const d = JSON.parse(e.data); toast(`${d.title}: ${d.text}`, d.priority === 'Urgent' || d.priority === 'High' ? 'error' : 'info', 10000); });
  es.addEventListener('reload', () => { toast('Daten wurden übernommen – lade neu …'); setTimeout(() => location.reload(), 1500); });
  es.addEventListener('message', e => { const d = JSON.parse(e.data); $('#statusline').textContent = d.text; });
}

function stopEvents() {
  S.es?.close(); S.es = null;
  S.logEs?.close(); S.logEs = null;
  $('#live').classList.remove('on');
}

let reloadTimer = null;
function reloadDetailSoon() {
  clearTimeout(reloadTimer);
  reloadTimer = setTimeout(() => { if (S.route?.view === 'device') loadDevice(false); }, 400);
}

function onStatus() {
  const s = S.status;
  if (!s) return;
  $('#statusline').textContent = `${s.totals.online}/${s.totals.count} online · ${hash(s.totals.hashrate)} · ${n(s.totals.power, 1)} W · Stand ${new Date(s.time).toLocaleTimeString('de-DE')}`;
  const v = S.route?.view;
  if (v === 'overview') renderOverview();
  else if (v === 'compare') renderCompare();
  else if (v === 'device') updateDeviceLive();
  else if (v === 'fans') updateFanTable();
}

// ---------- Navigation ----------

function route() {
  if (S.role === 'None') return;
  const parts = location.hash.replace(/^#\/?/, '').split('/').filter(Boolean);
  const view = parts[0] || 'overview';
  S.logEs?.close(); S.logEs = null;
  document.querySelectorAll('#nav a').forEach(a => a.classList.toggle('active', a.dataset.nav === view));
  if (view === 'device' && parts[1]) {
    S.route = { view, id: parts[1], tab: parts[2] || 'live' };
    return loadDevice(true);
  }
  S.route = { view };
  if (view === 'compare') return renderCompare();
  if (view === 'fans') return renderFans();
  if (view === 'tax' && isAdmin()) return renderTax();
  if (view === 'settings' && isAdmin()) return renderSettings();
  S.route = { view: 'overview' };
  if (S.status) renderOverview(); else api('/status').then(s => { S.status = s; renderOverview(); });
}

// ---------- Übersicht ----------

function tile(label, value, sub, cls) {
  return h('div', { class: 'tile' }, h('div', { class: 'label' }, label), h('div', { class: `value ${cls || ''}` }, value), sub ? h('div', { class: 'sub' }, sub) : null);
}

function dotClass(d) { return d.online ? 'on' : d.maintenance ? 'maint' : 'off'; }

function renderOverview() {
  const s = S.status;
  const t = s.totals;
  const chart = h('canvas');
  const devs = s.devices.map(d => h('a', { class: 'card device', href: `#/device/${d.id}` },
    h('div', { class: 'head' }, h('span', { class: `dot ${dotClass(d)}` }), h('b', {}, d.name),
      d.benchmark?.running ? h('span', { class: 'pill' }, 'Benchmark') : null,
      d.soak ? h('span', { class: 'pill' }, 'Dauertest') : null,
      d.simulated ? h('span', { class: 'pill gray' }, 'Simulation') : null),
    d.online
      ? h('div', { class: 'kv num' },
          h('div', {}, h('span', {}, 'Hashrate'), hash(d.hashrate)),
          h('div', {}, h('span', {}, 'ASIC / VR'), `${n(d.temp, 1)} / ${n(d.vrTemp, 0)} °C`),
          h('div', {}, h('span', {}, 'Leistung'), `${n(d.power, 1)} W`),
          h('div', {}, h('span', {}, 'Effizienz'), d.efficiency ? `${n(d.efficiency, 2)} J/TH` : '–'),
          h('div', {}, h('span', {}, 'Takt'), `${d.frequency ?? '–'} MHz / ${d.voltage ?? '–'} mV`),
          h('div', {}, h('span', {}, 'Laufzeit'), dur(d.uptimeSeconds)))
      : h('div', { class: d.maintenance ? 'warn' : 'danger' }, d.maintenance ? 'Neustart/Tuning …' : (d.error || 'offline')),
    d.automation && d.automation !== 'keine Automatik' ? h('div', { class: 'small muted', style: 'margin-top:6px' }, d.automation) : null,
    d.benchmark?.running ? h('div', { class: 'progress', style: 'margin-top:8px' }, h('div', { style: `width:${d.benchmark.overallProgress}%` })) : null,
    d.fan ? h('div', { class: `small ${d.fan.stalled ? 'danger' : 'muted'}`, style: 'margin-top:6px' },
      `VR-Lüfter K${d.fan.channel}: ${d.fan.percent} %${d.fan.rpm != null ? ` · ${d.fan.rpm} U/min` : ''}${d.fan.stalled ? ' · steht!' : ''}`) : null,
    d.suggestion ? h('div', { class: 'small warn', style: 'margin-top:6px' }, `Vorschlag offen: ${d.suggestion.frequencyMhz} MHz / ${d.suggestion.coreVoltageMv} mV`) : null));

  mount(h('div', { class: 'stack' },
    h('div', { class: 'tiles' },
      tile('Hashrate gesamt', hash(t.hashrate), `${t.online}/${t.count} Miner online`, 'ok'),
      tile('Leistung', `${n(t.power, 1)} W`, t.costPerDay != null ? `${n(t.costPerDay, 2)} ${t.currency} pro Tag` : ''),
      tile('Effizienz', t.efficiency ? `${n(t.efficiency, 2)} J/TH` : '–', 'gesamt'),
      tile('Max. Temperatur', t.maxTemp != null ? `${n(t.maxTemp, 1)} °C` : '–', 'ASIC'),
      s.price ? tile('Strompreis', `${n(s.price.ct, 2)} ct/kWh`, s.price.source) : null),
    h('div', { class: 'card' }, h('div', { class: 'chart-head' }, h('h3', {}, 'Hashrate gesamt'), h('span', { class: 'muted small' }, 'live')), h('div', { class: 'chart' }, chart)),
    s.devices.length ? h('div', { class: 'devices' }, devs) : h('div', { class: 'card muted' }, 'Noch keine Miner eingetragen.', isAdmin() ? ' Unter Einstellungen → Geräte hinzufügen.' : ''),
    isAdmin() && s.devices.length ? soakBatchCard(s.devices) : null,
    !s.running ? h('div', { class: 'banner row' },
      h('span', { style: 'flex:1' }, 'Der Motor ist pausiert – der Server fragt keine Miner ab (z. B. weil die Desktop-App im Modus „Lokal“ läuft oder Daten übertragen werden).'),
      isAdmin() ? h('button', {
        class: 'btn primary small', onclick: async () => {
          if (!await confirmBox('Motor fortsetzen', 'Server-Abfragen wieder starten?\n\nLäuft die Desktop-App noch im Modus „Lokal“, würden die Miner doppelt abgefragt. Stelle sie vorher auf „Server“ um oder beende sie.', 'Fortsetzen')) return;
          run(() => api('/admin/pause', { method: 'POST', body: { paused: false } }), 'Motor läuft wieder.');
        },
      }, 'Fortsetzen …') : null) : null));
  drawChart(chart, [{ points: s.history.map(p => [p[0], p[1]]), color: cssVar('--ok'), format: hash }], []);
}

/** Dauertest für mehrere Miner: Auswahl, eine Dauer, eine Bestätigung; laufende Tests mit Restzeit. */
function soakBatchCard(devices) {
  const running = devices.filter(d => d.soak);
  const left = u => { const m = Math.max(0, (new Date(u) - Date.now()) / 60000); return m >= 120 ? `${n(m / 60, 0)} h` : `${n(m, 0)} min`; };
  const start = async () => {
    const p = await run(() => api('/soak/prepare', { method: 'POST', body: {} }));
    if (!p) return;
    const sel = new Set(p.miners.filter(m => m.eligible).map(m => m.id));
    if (!sel.size) { toast('Kein Miner ist gerade bereit (offline, Benchmark oder Dauertest läuft).', 'error'); return; }
    const hours = h('select', {}, [6, 12, 24, 48, 72].map(x => h('option', { value: x, selected: x === 24 }, `${x} h`)));
    const body = h('div', { class: 'stack' },
      h('p', {}, 'Beobachtet wird jeweils die aktuelle Einstellung. Am Miner wird nichts geändert; Zeitplan/Strompreis-Regeln pausieren so lange. Schlägt ein Test fehl, gibt es einen Vorschlag (nur nach Bestätigung).'),
      h('div', { class: 'row' }, h('label', {}, 'Dauer'), hours),
      h('div', { class: 'stack' }, p.miners.map(m => h('label', { class: `row ${m.eligible ? '' : 'muted'}` },
        h('input', { type: 'checkbox', checked: sel.has(m.id), disabled: !m.eligible, onchange: e => { e.target.checked ? sel.add(m.id) : sel.delete(m.id); } }),
        h('span', {}, `${m.name}: ${m.eligible ? `${m.frequencyMhz} MHz / ${m.coreVoltageMv} mV` : m.reason}`)))));
    if (!await confirmBox('Dauertest für mehrere Miner', body, 'Starten')) return;
    if (!sel.size) { toast('Kein Miner ausgewählt.', 'error'); return; }
    const r = await run(() => api('/soak/start', { method: 'POST', body: { hours: Number(hours.value), ids: [...sel] } }));
    if (!r) return;
    const skipped = r.results.filter(x => !x.started);
    toast(`Dauertest gestartet für ${r.started} Miner.` + (skipped.length ? ` Übersprungen: ${skipped.map(x => `${x.name} (${x.message})`).join(', ')}` : ''), skipped.length ? 'info' : 'ok', 10000);
  };
  const stopAll = async () => {
    if (!await confirmBox('Alle Dauertests abbrechen', `${running.length} laufende(n) Dauertest(s) abbrechen? Die Einstellungen der Miner bleiben, wie sie sind.`, 'Abbrechen', true)) return;
    run(() => api('/soak/stop-all', { method: 'POST', body: {} }), 'Dauertests abgebrochen.');
  };
  return h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h3', {}, 'Dauertest'), h('span', { class: 'spacer' }),
      running.length ? h('button', { class: 'btn small danger', onclick: stopAll }, 'Alle abbrechen') : null,
      h('button', { class: 'btn small', onclick: start }, 'Dauertest für mehrere Miner …')),
    running.length
      ? h('div', { class: 'stack small' }, running.map(d => h('div', {}, h('b', {}, d.name), ` · ${d.soak.status} · noch ${left(d.soak.until)}`)))
      : h('p', { class: 'muted small' }, 'Kein Dauertest aktiv. Prüft die aktuelle Einstellung mehrerer Miner über Stunden, ohne etwas zu ändern.'));
}

// ---------- Diagramm (Canvas, ohne Bibliothek) ----------

function cssVar(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }

function drawChart(canvas, series, markers, opts = {}) {
  requestAnimationFrame(() => {
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth, hgt = canvas.clientHeight;
    if (!w || !hgt) return;
    canvas.width = w * dpr; canvas.height = hgt * dpr;
    const c = canvas.getContext('2d');
    c.scale(dpr, dpr);
    c.clearRect(0, 0, w, hgt);
    const all = series.flatMap(s => s.points);
    c.font = '11px system-ui, sans-serif';
    c.fillStyle = cssVar('--muted');
    if (all.length < 2) { c.fillText('noch keine Daten', 8, hgt / 2); return; }
    const x0 = Math.min(...all.map(p => p[0])), x1 = Math.max(...all.map(p => p[0]));
    const pad = { l: 6, r: 64, t: 8, b: 16 };
    const X = t => pad.l + (t - x0) / Math.max(1, x1 - x0) * (w - pad.l - pad.r);
    series.forEach((s, idx) => {
      const vals = s.points.map(p => p[1]).filter(v => v != null);
      if (!vals.length) return;
      let lo = Math.min(...vals), hi = Math.max(...vals);
      if (hi - lo < 1e-9) { lo -= 1; hi += 1; }
      const Y = v => pad.t + (1 - (v - lo) / (hi - lo)) * (hgt - pad.t - pad.b);
      c.strokeStyle = s.color; c.lineWidth = 1.6; c.beginPath();
      s.points.forEach((p, i) => { const x = X(p[0]), y = Y(p[1]); i ? c.lineTo(x, y) : c.moveTo(x, y); });
      c.stroke();
      if (idx === 0) {
        c.lineTo(X(s.points.at(-1)[0]), hgt - pad.b); c.lineTo(X(s.points[0][0]), hgt - pad.b); c.closePath();
        c.globalAlpha = 0.15; c.fillStyle = s.color; c.fill(); c.globalAlpha = 1;
      }
      const fmt = s.format || (v => n(v, 1));
      c.fillStyle = s.color;
      c.fillText(fmt(hi), w - pad.r + 6, pad.t + 10 + idx * 26);
      c.fillText(fmt(lo), w - pad.r + 6, hgt - pad.b - idx * 26);
    });
    c.strokeStyle = cssVar('--marker'); c.setLineDash([4, 3]);
    for (const m of markers) {
      if (m < x0 || m > x1) continue;
      c.beginPath(); c.moveTo(X(m), pad.t); c.lineTo(X(m), hgt - pad.b); c.stroke();
    }
    c.setLineDash([]);
    c.fillStyle = cssVar('--muted');
    const span = (x1 - x0) / 3600000;
    c.fillText(span < 2 ? `${Math.round(span * 60)} min` : span < 48 ? `${Math.round(span)} h` : `${Math.round(span / 24)} Tage`, pad.l, hgt - 3);
  });
}

// ---------- Vergleich ----------

function renderCompare() {
  const s = S.status;
  if (!s) { api('/status').then(x => { S.status = x; renderCompare(); }); return; }
  const rows = [
    ['Status', d => d.online ? 'online' : d.error || 'offline'],
    ['Modell / Profil', d => `${d.model || '–'} / ${d.profile}`],
    ['Hashrate', d => hash(d.hashrate)],
    ['Soll-Hashrate', d => hash(d.expectedHashrate)],
    ['ASIC-Temperatur', d => d.temp != null ? `${n(d.temp, 1)} °C` : '–'],
    ['VR-Temperatur', d => d.vrTemp != null ? `${n(d.vrTemp, 0)} °C` : '–'],
    ['Leistung', d => d.power != null ? `${n(d.power, 1)} W` : '–'],
    ['Effizienz', d => d.efficiency ? `${n(d.efficiency, 2)} J/TH` : '–'],
    ['Frequenz / Spannung', d => `${d.frequency ?? '–'} MHz / ${d.voltage ?? '–'} mV`],
    ['Fehlerrate', d => d.errorPercent != null ? `${n(d.errorPercent, 2)} %` : '–'],
    ['Lüfter', d => d.fanRpm != null ? `${d.fanRpm} rpm (${d.fanPercent ?? '–'} %)` : '–'],
    ['Shares', d => d.sharesAccepted != null ? `${d.sharesAccepted} / ${d.sharesRejected} abgelehnt` : '–'],
    ['Best Diff', d => d.bestDiff || '–'],
    ['Laufzeit', d => dur(d.uptimeSeconds)],
    ['Firmware', d => d.firmwareText || '–'],
    ['Pool', d => d.pool || '–'],
    ['Automatik', d => d.automation || '–'],
  ];
  mount(h('div', { class: 'card' }, h('h2', {}, 'Vergleich'), h('div', { class: 'table-wrap' }, h('table', {},
    h('thead', {}, h('tr', {}, h('th', {}), s.devices.map(d => h('th', {}, h('a', { href: `#/device/${d.id}` }, d.name))))),
    h('tbody', {}, rows.map(([label, f]) => h('tr', {}, h('th', {}, label), s.devices.map(d => h('td', { class: 'num' }, f(d))))))))));
}

// ---------- Gerät ----------

const TABS = [
  ['live', 'Live'], ['benchmark', 'Benchmark', true], ['results', 'Ergebnisse'], ['compare', 'Vorher/Nachher'],
  ['automation', 'Automatik', true], ['backups', 'Sicherungen', true], ['log', 'Protokolle', true],
];

async function loadDevice(full) {
  const id = S.route.id;
  const detail = await run(() => api(`/devices/${id}`));
  if (!detail || S.route?.id !== id) return;
  S.detail = detail;
  if (full || !$('#device-page')) renderDevice();
  else refreshDeviceTab();
}

function summaryOf(id) { return S.status?.devices.find(d => d.id === id) || S.detail?.summary; }

function renderDevice() {
  const d = S.detail.summary;
  const tab = TABS.some(t => t[0] === S.route.tab && (!t[2] || isAdmin())) ? S.route.tab : 'live';
  S.route.tab = tab;
  mount(h('div', { id: 'device-page', class: 'stack' },
    h('div', { class: 'titlebar' },
      h('span', { id: 'dev-dot', class: `dot ${dotClass(d)}` }),
      h('h2', {}, d.name),
      h('span', { class: 'muted' }, [d.model, d.firmware, isAdmin() ? d.host : null].filter(Boolean).join(' · ')),
      h('span', { class: 'spacer' }),
      h('span', { class: 'pill' }, S.detail.profile.name)),
    d.suggestion && isAdmin() ? suggestionBanner(d) : null,
    h('div', { class: 'tabs' }, TABS.filter(t => !t[2] || isAdmin()).map(([k, label]) =>
      h('a', { href: `#/device/${d.id}/${k}`, class: k === tab ? 'active' : null }, label))),
    h('div', { id: 'tab' })));
  refreshDeviceTab();
}

function refreshDeviceTab() {
  const tab = $('#tab');
  if (!tab) return;
  const render = { live: tabLive, benchmark: tabBenchmark, results: tabResults, compare: tabCompare, automation: tabAutomation, backups: tabBackups, log: tabLog }[S.route.tab];
  // Protokoll-Tab nicht bei jedem Neuladen neu aufbauen (Live-Log liefe sonst neu an)
  if (S.route.tab === 'log' && $('#miner-log')) { fillAppLog(); return; }
  tab.replaceChildren(render());
}

function suggestionBanner(d) {
  const s = d.suggestion;
  return h('div', { class: 'banner danger row' },
    h('span', { style: 'flex:1' }, `Dauertest fehlgeschlagen – Vorschlag: ${s.frequencyMhz} MHz / ${s.coreVoltageMv} mV (nächstniedrigere stabile Einstellung).`),
    h('button', { class: 'btn primary small', onclick: () => applyChange(s.frequencyMhz, s.coreVoltageMv) }, 'Prüfen & anwenden'),
    h('button', { class: 'btn ghost small', onclick: () => run(() => api(`/devices/${d.id}/suggestion/dismiss`, { method: 'POST', body: {} })).then(reloadDetailSoon) }, 'Verwerfen'));
}

/** Frequenz/Spannung: Server liefert den Bestätigungstext (alt → neu, Grenzen), erst danach ausführen. */
async function applyChange(frequency, voltage) {
  const id = S.route.id;
  const preview = await run(() => api(`/devices/${id}/change/preview`, { method: 'POST', body: { frequency, voltage } }));
  if (!preview) return;
  if (!await confirmBox('Einstellung anwenden', preview.confirmText, 'Anwenden')) return;
  if (await run(() => api(`/devices/${id}/change`, { method: 'POST', body: { frequency, voltage } }), 'Einstellung angewendet.')) reloadDetailSoon();
}

function tabLive() {
  const d = summaryOf(S.route.id);
  const p = S.detail.profile;
  const chartHash = h('canvas'), chartTemp = h('canvas'), chartPower = h('canvas');
  const markers = h('div', { class: 'small muted' });
  const ranges = ['1h', '24h', '7d', '30d'];
  const rangeBar = h('span', { class: 'range' }, ranges.map(r => h('a', { href: '#', class: r === S.chartRange ? 'active' : null, onclick: e => { e.preventDefault(); S.chartRange = r; refreshDeviceTab(); } }, r === '1h' ? '1 h' : r === '24h' ? '24 h' : r === '7d' ? '7 Tage' : '30 Tage')));

  api(`/devices/${S.route.id}/history?range=${S.chartRange}`).then(hist => {
    const tm = hist.tuning.map(t => t.time);
    drawChart(chartHash, [{ points: hist.samples.map(s => [s[0], s[1]]), color: cssVar('--ok'), format: hash }], tm);
    drawChart(chartTemp, [{ points: hist.samples.map(s => [s[0], s[2]]), color: cssVar('--warn'), format: v => `${n(v, 1)} °C` }], tm);
    drawChart(chartPower, [{ points: hist.samples.map(s => [s[0], s[3]]), color: cssVar('--info'), format: v => `${n(v, 1)} W` }], tm);
    markers.replaceChildren(...hist.tuning.slice(-8).reverse().map(t => h('div', {}, `┊ ${time(t.time)} ${t.source}: ${t.change}${t.note ? ' – ' + t.note : ''}`)));
  }).catch(e => toast(e.message, 'error'));

  const freq = h('input', { type: 'number', value: d.frequency ?? p.defaultFrequencyMhz, min: p.minFrequencyMhz, max: p.maxFrequencyMhz, step: 5 });
  const volt = h('input', { type: 'number', value: d.voltage ?? p.defaultVoltageMv, min: p.minVoltageMv, max: p.maxVoltageMv, step: 5 });
  const profileSel = isAdmin() && S.detail.profiles ? h('select', {
    onchange: async e => {
      if (!await confirmBox('Profil wählen', `Profil „${e.target.selectedOptions[0].textContent}“ für dieses Gerät dauerhaft verwenden? Grenzen für Frequenz und Spannung richten sich danach.`)) { e.target.value = p.id; return; }
      if (await run(() => api(`/devices/${S.route.id}/profile`, { method: 'POST', body: { id: e.target.value } }), 'Profil gespeichert.')) loadDevice(true);
    },
  }, S.detail.profiles.map(x => h('option', { value: x.id, selected: x.id === p.id }, x.name))) : null;

  return h('div', { class: 'stack' },
    h('div', { class: 'tiles', id: 'live-tiles' }, liveTiles(d)),
    h('div', { class: 'card' }, h('div', { class: 'chart-head' }, h('h3', {}, 'Hashrate'), rangeBar), h('div', { class: 'chart' }, chartHash), markers),
    h('div', { class: 'grid', style: 'grid-template-columns:repeat(auto-fit,minmax(300px,1fr))' },
      h('div', { class: 'card' }, h('h3', {}, 'ASIC-Temperatur'), h('div', { class: 'chart' }, chartTemp)),
      h('div', { class: 'card' }, h('h3', {}, 'Leistung'), h('div', { class: 'chart' }, chartPower))),
    isAdmin() ? h('div', { class: 'card stack' },
      h('h3', {}, 'Manuell einstellen'),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, `Frequenz (MHz, ${p.minFrequencyMhz}–${p.maxFrequencyMhz})`), freq),
        h('div', {}, h('label', {}, `Kernspannung (mV, ${p.minVoltageMv}–${p.maxVoltageMv})`), volt),
        h('button', { class: 'btn primary', onclick: () => applyChange(+freq.value, +volt.value), disabled: d.benchmark?.running }, 'Prüfen & anwenden'),
        h('button', {
          class: 'btn', onclick: async () => {
            if (!await confirmBox('Neustart', `${d.name} neu starten? Watchdog und Offline-Meldung pausieren dabei.`, 'Neu starten')) return;
            run(() => api(`/devices/${S.route.id}/restart`, { method: 'POST', body: {} }), 'Neustart ausgelöst.');
          },
        }, 'Miner neu starten')),
      profileSel ? h('div', { class: 'form' }, h('div', {}, h('label', {}, 'Profil'), profileSel)) : null,
      S.detail.profile.notes ? h('p', { class: 'muted small' }, S.detail.profile.notes) : null) : null);
}

function liveTiles(d) {
  return [
    tile('Hashrate', hash(d.hashrate), d.expectedHashrate ? `Soll ${hash(d.expectedHashrate)}` : '', d.online ? 'ok' : 'danger'),
    tile('ASIC / VR', d.temp != null ? `${n(d.temp, 1)} °C` : '–', d.vrTemp != null ? `VR ${n(d.vrTemp, 0)} °C` : ''),
    tile('Leistung', d.power != null ? `${n(d.power, 1)} W` : '–', d.efficiency ? `${n(d.efficiency, 2)} J/TH` : ''),
    tile('Frequenz', d.frequency != null ? `${d.frequency} MHz` : '–', d.voltage != null ? `${d.voltage} mV` : ''),
    tile('Lüfter', d.fanRpm != null ? `${d.fanRpm} rpm` : '–', d.fanPercent != null ? `${d.fanPercent} %` : ''),
    tile('Shares', d.sharesAccepted ?? '–', d.errorPercent != null ? `Fehlerrate ${n(d.errorPercent, 2)} %` : ''),
    tile('Laufzeit', dur(d.uptimeSeconds), d.bestDiff ? `Best ${d.bestDiff}` : ''),
    tile('Status', d.online ? 'online' : d.maintenance ? 'Neustart …' : 'offline', d.pool || d.error || '', d.online ? 'ok' : 'warn'),
    d.fan ? tile(`VR-Lüfter K${d.fan.channel}`, `${d.fan.percent} %`, d.fan.stalled ? 'Lüfter steht!' : d.fan.rpm != null ? `${d.fan.rpm} U/min` : d.fan.reason, d.fan.stalled ? 'danger' : '') : null,
  ].filter(Boolean);
}

function updateDeviceLive() {
  const d = S.status?.devices.find(x => x.id === S.route.id);
  if (!d) return;
  const dot = $('#dev-dot');
  if (dot) dot.className = `dot ${dotClass(d)}`;
  const tiles = $('#live-tiles');
  if (tiles) tiles.replaceChildren(...liveTiles(d));
  if (d.benchmark) updateBenchmark(d.benchmark);
}

// ---------- Benchmark ----------

const BENCH_FIELDS = [
  ['startFrequencyMhz', 'Start-Frequenz (MHz)'], ['maxFrequencyMhz', 'Max. Frequenz (MHz)'], ['frequencyStepMhz', 'Schritt (MHz)'],
  ['startVoltageMv', 'Start-Spannung (mV)'], ['minVoltageMv', 'Min. Spannung (mV)'], ['maxVoltageMv', 'Max. Spannung (mV)'], ['voltageStepMv', 'Schritt (mV)'],
  ['warmupSeconds', 'Aufwärmen (s)'], ['measureSeconds', 'Messdauer (s)'], ['sampleIntervalSeconds', 'Messintervall (s)'],
  ['maxChipTempC', 'Max. Chip (°C)'], ['maxVrTempC', 'Max. VR (°C)'], ['maxPowerW', 'Max. Leistung (W)'],
  ['stabilityThreshold', 'Stabil ab (Anteil Soll)'], ['maxErrorPercent', 'Max. Fehlerrate (%)'],
];

function tabBenchmark() {
  const d = summaryOf(S.route.id);
  const base = S.detail.benchmarkDefaults;
  const inputs = {};
  const sel = (key, opts) => { const s = h('select', {}, opts.map(([v, l]) => h('option', { value: v, selected: base[key] === v }, l))); inputs[key] = s; return s; };
  const form = h('div', { class: 'form' },
    BENCH_FIELDS.map(([k, label]) => { const i = h('input', { type: 'number', step: 'any', value: base[k] }); inputs[k] = i; return h('div', {}, h('label', {}, label), i); }),
    h('div', {}, h('label', {}, 'Am Ende setzen'), sel('restoreMode', [['Best', 'Beste Einstellung'], ['Original', 'Ursprüngliche Einstellung']])),
    h('div', {}, h('label', {}, 'Beste nach'), sel('restoreRanking', [['Balanced', 'Ausgewogen'], ['MaxHashrate', 'Hashrate'], ['Efficiency', 'Effizienz']])),
    h('div', {}, h('label', {}, 'Lüfter'), sel('fanMode', [['KeepCurrent', 'unverändert'], ['Full', '100 % während des Tests']])));
  const settings = () => {
    const s = { ...base };
    for (const [k, el] of Object.entries(inputs)) s[k] = el.tagName === 'SELECT' ? el.value : Number(el.value);
    return s;
  };
  const start = async resume => {
    const body = { settings: settings(), resume };
    const plan = await run(() => api(`/devices/${S.route.id}/benchmark/prepare`, { method: 'POST', body }));
    if (!plan) return;
    if (!await confirmBox(resume ? 'Benchmark fortsetzen' : 'Benchmark starten', plan.confirmText, 'Starten')) return;
    if (await run(() => api(`/devices/${S.route.id}/benchmark/start`, { method: 'POST', body }), 'Benchmark läuft auf dem Server.')) reloadDetailSoon();
  };
  const session = S.detail.session;
  const running = d.benchmark?.running;
  return h('div', { class: 'stack' },
    h('div', { class: 'card stack', id: 'bench-progress' }, benchProgress(d.benchmark)),
    h('div', { class: 'card stack' },
      h('h3', {}, 'Suchbereich'),
      h('p', { class: 'muted small' }, `Profil ${S.detail.profile.name}: ${S.detail.profile.minFrequencyMhz}–${S.detail.profile.maxFrequencyMhz} MHz, ${S.detail.profile.minVoltageMv}–${S.detail.profile.maxVoltageMv} mV · ${S.detail.estimatedDuration}`),
      form,
      h('div', { class: 'row' },
        h('button', { class: 'btn primary', disabled: running || !d.online, onclick: () => start(false) }, 'Benchmark starten'),
        session && !session.isFinished && session.results.length ? h('button', { class: 'btn', disabled: running, onclick: () => start(true) }, 'Fortsetzen') : null,
        h('span', { class: 'muted small' }, 'Der Lauf geht auf dem Server weiter, auch wenn der Browser geschlossen wird.'))));
}

function benchProgress(b) {
  if (!b) return [h('h3', {}, 'Fortschritt'), h('p', { class: 'muted' }, 'In dieser Sitzung wurde noch kein Benchmark gestartet.')];
  return [
    h('div', { class: 'titlebar' }, h('h3', {}, `Benchmark: ${b.phase}`), h('span', { class: 'spacer' }),
      b.running && isAdmin() ? h('button', { class: 'btn small', onclick: () => run(() => api(`/devices/${S.route.id}/benchmark/pause`, { method: 'POST', body: {} })) }, b.paused ? 'Fortsetzen' : 'Pause') : null,
      b.running && isAdmin() ? h('button', {
        class: 'btn danger small', onclick: async () => {
          if (await confirmBox('Benchmark stoppen', 'Benchmark abbrechen? Die Einstellungen werden wiederhergestellt.', 'Stoppen', true))
            run(() => api(`/devices/${S.route.id}/benchmark/stop`, { method: 'POST', body: {} }));
        },
      }, 'Stoppen') : null),
    h('div', {}, b.step || ''),
    h('div', { class: 'progress' }, h('div', { style: `width:${b.overallProgress}%` })),
    h('div', { class: 'small muted' }, `Gesamt ${n(b.overallProgress, 0)} % · Phase ${n(b.phaseProgress, 0)} % · ${b.eta || ''}`),
  ];
}

function updateBenchmark(b) {
  const box = $('#bench-progress');
  if (box) box.replaceChildren(...benchProgress(b));
  if (!b.running && S.route?.view === 'device') reloadDetailSoon();
}

// ---------- Ergebnisse ----------

function tabResults() {
  const s = S.detail.session;
  if (!s || !s.results.length) return h('div', { class: 'card muted' }, 'Noch keine Benchmark-Ergebnisse.');
  const best = s.ranking.balanced;
  const bestCard = (label, r) => r ? h('div', { class: 'tile' }, h('div', { class: 'label' }, label),
    h('div', { class: 'value' }, `${r.frequencyMhz} MHz / ${r.coreVoltageMv} mV`),
    h('div', { class: 'sub' }, `${hash(r.avgHashRateGh)} · ${r.efficiencyJth ? n(r.efficiencyJth, 2) + ' J/TH' : '–'}`),
    isAdmin() ? h('button', { class: 'btn small', style: 'margin-top:6px', onclick: () => applyChange(r.frequencyMhz, r.coreVoltageMv) }, 'Anwenden …') : null) : null;
  return h('div', { class: 'stack' },
    h('div', { class: 'tiles' }, bestCard('Beste Hashrate', s.ranking.hashrate), bestCard('Beste Effizienz', s.ranking.efficiency), bestCard('Ausgewogen', s.ranking.balanced)),
    h('div', { class: 'card' },
      h('div', { class: 'titlebar' }, h('h3', {}, `Lauf vom ${time(s.startedAt)}`), h('span', { class: 'spacer' }), h('span', { class: 'muted small' }, s.finishReason || (s.isFinished ? 'abgeschlossen' : 'nicht abgeschlossen'))),
      h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, ['MHz', 'mV', 'Ergebnis', 'Hashrate', 'Soll', 'Leistung', 'J/TH', 'Chip max', 'VR max', 'Fehler %', ''].map(x => h('th', {}, x)))),
        h('tbody', {}, s.results.map(r => h('tr', { class: best && r.frequencyMhz === best.frequencyMhz && r.coreVoltageMv === best.coreVoltageMv ? 'best' : null },
          h('td', {}, r.frequencyMhz), h('td', {}, r.coreVoltageMv), h('td', { class: r.isStable ? 'ok' : 'danger' }, r.outcomeText),
          h('td', {}, hash(r.avgHashRateGh)), h('td', {}, hash(r.expectedHashRateGh)), h('td', {}, `${n(r.avgPowerW, 1)} W`),
          h('td', {}, r.efficiencyJth ? n(r.efficiencyJth, 2) : '–'), h('td', {}, r.maxChipTempC != null ? n(r.maxChipTempC, 1) : '–'),
          h('td', {}, r.maxVrTempC != null ? n(r.maxVrTempC, 0) : '–'), h('td', {}, r.avgErrorPercent != null ? n(r.avgErrorPercent, 2) : '–'),
          h('td', {}, isAdmin() && r.isStable ? h('button', { class: 'btn small', onclick: () => applyChange(r.frequencyMhz, r.coreVoltageMv) }, 'Anwenden …') : null))))))));
}

// ---------- Vorher/Nachher ----------

function tabCompare() {
  const box = h('div', { class: 'card' }, h('h3', {}, 'Vorher/Nachher (je 60 min, erste 5 min nach der Änderung ausgelassen)'), h('p', { class: 'muted' }, 'Lade …'));
  api(`/devices/${S.route.id}/comparisons`).then(rows => {
    box.replaceChildren(box.firstChild, rows.length
      ? h('div', { class: 'table-wrap' }, h('table', {},
          h('thead', {}, h('tr', {}, ['Zeit', 'Quelle', 'Änderung', 'Vorher', 'Nachher', 'Differenz'].map(x => h('th', {}, x)))),
          h('tbody', {}, rows.map(r => h('tr', {}, h('td', {}, time(r.time)), h('td', {}, r.source), h('td', {}, r.change), h('td', {}, r.before), h('td', {}, r.after), h('td', {}, r.delta))))))
      : h('p', { class: 'muted' }, 'Noch keine protokollierten Änderungen in den letzten 30 Tagen.'));
  }).catch(e => toast(e.message, 'error'));
  return box;
}

// ---------- Automatik, Dauertest ----------

function numInput(obj, key, step = 1) {
  return h('input', { type: 'number', step, value: obj[key], oninput: e => { obj[key] = Number(e.target.value); } });
}
function checkInput(obj, key, label) {
  return h('label', { class: 'check' }, h('input', { type: 'checkbox', checked: !!obj[key], onchange: e => { obj[key] = e.target.checked; } }), label);
}

function tabAutomation() {
  const c = S.detail.config;
  const d = summaryOf(S.route.id);
  const presets = structuredClone(c.presets);
  const guard = structuredClone(c.thermalGuard);
  const sched = structuredClone(c.schedule);
  const presetList = h('div', { class: 'stack' });
  const presetOptions = () => [h('option', { value: '' }, '—'), ...presets.map(p => h('option', { value: p.name }, `${p.name} (${p.frequencyMhz} MHz / ${p.coreVoltageMv} mV)`))];
  const presetSelect = (obj, key) => { const s = h('select', { onchange: e => { obj[key] = e.target.value; } }, presetOptions()); s.value = obj[key] || ''; return s; };

  const renderPresets = () => presetList.replaceChildren(
    ...presets.map((p, i) => h('div', { class: 'row' }, h('span', { style: 'flex:1' }, `${p.name}: ${p.frequencyMhz} MHz / ${p.coreVoltageMv} mV`),
      h('button', { class: 'btn small ghost', onclick: () => { presets.splice(i, 1); renderPresets(); } }, 'Entfernen'))),
    presets.length ? null : h('p', { class: 'muted small' }, 'Noch keine Voreinstellungen.'));
  renderPresets();
  const pName = h('input', { placeholder: 'Name, z. B. Nacht' });
  const pFreq = h('input', { type: 'number', value: d.frequency ?? '' });
  const pVolt = h('input', { type: 'number', value: d.voltage ?? '' });

  const entries = h('div', { class: 'stack' });
  const renderEntries = () => entries.replaceChildren(...sched.entries.map((e, i) => h('div', { class: 'form' },
    h('div', {}, h('label', {}, 'Tage (z. B. Mo-Fr, Sa,So, täglich)'), h('input', { value: e.daysText, oninput: ev => { e.daysText = ev.target.value; delete e.days; } })),
    h('div', {}, h('label', {}, 'von (Uhr)'), numInput(e, 'fromHour')),
    h('div', {}, h('label', {}, 'bis (Uhr)'), numInput(e, 'toHour')),
    h('div', {}, h('label', {}, 'Voreinstellung'), presetSelect(e, 'preset')),
    h('button', { class: 'btn small ghost', onclick: () => { sched.entries.splice(i, 1); renderEntries(); } }, 'Entfernen'))));
  renderEntries();

  const save = async () => {
    const r = await run(() => api(`/devices/${S.route.id}/automation`, { method: 'PUT', body: { presets, thermalGuard: guard, schedule: sched } }), 'Automatik gespeichert.');
    if (r) { await loadDevice(false); if ((guard.enabled && !r.thermalGuardApproved) || (sched.enabled && !r.scheduleApproved)) toast('Geänderte Regeln brauchen eine neue Freigabe.', 'info'); }
    return r;
  };
  const approve = async rule => {
    if (!await save()) return;
    const t = await run(() => api(`/devices/${S.route.id}/automation/approval-text`, { method: 'POST', body: { rule } }));
    if (!t || !await confirmBox('Regel freigeben', t.text, 'Freigeben')) return;
    if (await run(() => api(`/devices/${S.route.id}/automation/approve`, { method: 'POST', body: { rule } }), 'Regel freigegeben.')) loadDevice(false);
  };
  const soakHours = h('select', {}, [6, 12, 24, 48].map(x => h('option', { value: x, selected: x === 24 }, `${x} h`)));

  return h('div', { class: 'stack' },
    h('div', { class: 'card' }, h('b', {}, 'Status: '), d.automation || '–'),
    h('div', { class: 'card stack' }, h('h3', {}, 'Voreinstellungen'), presetList,
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'Name'), pName), h('div', {}, h('label', {}, 'MHz'), pFreq), h('div', {}, h('label', {}, 'mV'), pVolt),
        h('button', {
          class: 'btn', onclick: () => {
            if (!pName.value.trim()) return toast('Name fehlt.', 'error');
            const i = presets.findIndex(p => p.name.toLowerCase() === pName.value.trim().toLowerCase());
            const p = { name: pName.value.trim(), frequencyMhz: Number(pFreq.value), coreVoltageMv: Number(pVolt.value) };
            if (i >= 0) presets[i] = p; else presets.push(p);
            pName.value = ''; renderPresets();
          },
        }, 'Hinzufügen'))),
    h('div', { class: 'card stack' },
      h('div', { class: 'titlebar' }, h('h3', {}, 'Temperaturschutz'), h('span', { class: `pill ${c.thermalGuardApproved ? '' : 'gray'}` }, c.thermalGuardApproved ? 'freigegeben' : 'nicht freigegeben')),
      checkInput(guard, 'enabled', 'eingeschaltet'),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, 'Max. Chip (°C)'), numInput(guard, 'maxChipTempC', 0.5)), h('div', {}, h('label', {}, 'Max. VR (°C)'), numInput(guard, 'maxVrTempC', 0.5)),
        h('div', {}, h('label', {}, 'durchgehend (min)'), numInput(guard, 'minutes')), h('div', {}, h('label', {}, 'Absenken um (MHz)'), numInput(guard, 'stepMhz')),
        h('div', {}, h('label', {}, 'nie unter (MHz)'), numInput(guard, 'minFrequencyMhz')), h('div', {}, h('label', {}, 'Zurück nach (min kühl)'), numInput(guard, 'recoverMinutes'))),
      checkInput(guard, 'recover', 'schrittweise zurück, wenn wieder kühl'),
      h('div', { class: 'row' }, h('button', { class: 'btn', onclick: save }, 'Speichern'), h('button', { class: 'btn primary', onclick: () => approve('thermal') }, 'Speichern & freigeben …'))),
    h('div', { class: 'card stack' },
      h('div', { class: 'titlebar' }, h('h3', {}, 'Zeitplan / Strompreis'), h('span', { class: `pill ${c.scheduleApproved ? '' : 'gray'}` }, c.scheduleApproved ? 'freigegeben' : 'nicht freigegeben')),
      checkInput(sched, 'enabled', 'eingeschaltet'),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'Art'), (() => { const s = h('select', { onchange: e => { sched.mode = e.target.value; } }, h('option', { value: 'time' }, 'Zeitplan'), h('option', { value: 'price' }, 'Strompreis (Schwelle)')); s.value = sched.mode; return s; })())),
      h('h3', {}, 'Zeitplan'), entries,
      h('div', { class: 'form' }, h('button', { class: 'btn small', onclick: () => { sched.entries.push({ daysText: 'täglich', fromHour: 22, toHour: 6, preset: presets[0]?.name || '' }); renderEntries(); } }, 'Zeitfenster hinzufügen'),
        h('div', {}, h('label', {}, 'sonst'), presetSelect(sched, 'defaultPreset'))),
      h('h3', {}, 'Strompreis'),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'günstig bis (ct/kWh)'), numInput(sched, 'thresholdCt', 0.1)),
        h('div', {}, h('label', {}, 'günstig →'), presetSelect(sched, 'cheapPreset')), h('div', {}, h('label', {}, 'teuer →'), presetSelect(sched, 'expensivePreset'))),
      h('div', { class: 'row' }, h('button', { class: 'btn', onclick: save }, 'Speichern'), h('button', { class: 'btn primary', onclick: () => approve('schedule') }, 'Speichern & freigeben …'))),
    h('div', { class: 'card stack' },
      h('h3', {}, 'Dauertest'),
      d.soak ? h('p', {}, `${d.soak.status} (bis ${time(d.soak.until)})`) : h('p', { class: 'muted' }, 'Beobachtet die aktuelle Einstellung über Stunden, ohne am Miner etwas zu ändern.'),
      d.soak
        ? h('button', { class: 'btn danger', onclick: () => run(() => api(`/devices/${S.route.id}/soak/stop`, { method: 'POST', body: {} }), 'Dauertest abgebrochen.').then(reloadDetailSoon) }, 'Dauertest abbrechen')
        : h('div', { class: 'row' }, soakHours, h('button', {
            class: 'btn primary', onclick: async () => {
              const hours = Number(soakHours.value);
              const t = await run(() => api(`/devices/${S.route.id}/soak/prepare`, { method: 'POST', body: { hours } }));
              if (!t || !await confirmBox('Dauertest', t.text, 'Starten')) return;
              run(() => api(`/devices/${S.route.id}/soak/start`, { method: 'POST', body: { hours } }), 'Dauertest gestartet.').then(reloadDetailSoon);
            },
          }, 'Dauertest starten …'))));
}

// ---------- Sicherungen ----------

function tabBackups() {
  const list = h('div', {}, h('p', { class: 'muted' }, 'Lade …'));
  const load = () => api(`/devices/${S.route.id}/snapshots`).then(snaps => list.replaceChildren(snaps.length
    ? h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Zeit', 'Anlass', 'Firmware', ''].map(x => h('th', {}, x)))),
        h('tbody', {}, snaps.map(s => h('tr', {}, h('td', {}, time(s.takenAt)), h('td', {}, s.reason), h('td', {}, s.firmwareVersion || '–'),
          h('td', {}, h('button', { class: 'btn small', onclick: () => restore(s) }, 'Wiederherstellen …')))))))
    : h('p', { class: 'muted' }, 'Noch keine Sicherung.'))).catch(e => toast(e.message, 'error'));
  const restore = async snap => {
    const diff = await run(() => api(`/devices/${S.route.id}/snapshots/diff`, { method: 'POST', body: { file: snap.file } }));
    if (!diff) return;
    if (!diff.length) return toast('Keine Unterschiede zur aktuellen Einstellung.', 'info');
    const boxes = diff.map(c => ({ c, box: h('input', { type: 'checkbox', checked: c.group !== 'Pool' }) }));
    const body = h('div', { class: 'stack' }, h('p', {}, `Sicherung vom ${time(snap.takenAt)}. Nur ausgewählte Felder werden zurückgespielt; vorher wird der aktuelle Stand gesichert.`),
      h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['', 'Bereich', 'Einstellung', 'Aktuell', 'Gesichert'].map(x => h('th', {}, x)))),
        h('tbody', {}, boxes.map(({ c, box }) => h('tr', {}, h('td', {}, box), h('td', {}, c.group), h('td', {}, c.label), h('td', {}, c.current), h('td', {}, c.saved)))))));
    if (!await confirmBox('Einstellungen wiederherstellen', body, 'Wiederherstellen')) return;
    const fields = boxes.filter(b => b.box.checked).map(b => b.c.field);
    if (await run(() => api(`/devices/${S.route.id}/snapshots/restore`, { method: 'POST', body: { file: snap.file, fields } }), 'Wiederhergestellt.')) load();
  };
  load();
  return h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h3', {}, 'Einstellungen des Miners sichern'), h('span', { class: 'spacer' }),
      h('button', { class: 'btn primary', onclick: () => run(() => api(`/devices/${S.route.id}/snapshots`, { method: 'POST', body: {} }), 'Gesichert.').then(load) }, 'Jetzt sichern')),
    h('p', { class: 'muted small' }, 'Die Sicherung enthält auch Pool-Benutzer (Wallet-Adresse). Pool-Passwörter liefert AxeOS nicht aus.'),
    list);
}

// ---------- Protokolle ----------

function tabLog() {
  const appLog = h('div', { class: 'log', id: 'app-log' });
  const minerLog = h('div', { class: 'log', id: 'miner-log' });
  const state = h('span', { class: 'muted small' }, 'verbinde …');
  const follow = h('input', { type: 'checkbox', checked: true });
  S.logEs?.close();
  const es = new EventSource(`/api/v1/devices/${S.route.id}/minerlog`);
  S.logEs = es;
  es.addEventListener('line', e => {
    const l = JSON.parse(e.data);
    const t = new Date(l.time).toLocaleTimeString('de-DE');
    minerLog.append(h('div', { class: (l.level || 'I')[0] }, `${t} ${l.tag ? l.tag + ': ' : ''}${l.message}`));
    while (minerLog.childElementCount > 2000) minerLog.firstChild.remove();
    if (follow.checked) minerLog.scrollTop = minerLog.scrollHeight;
  });
  es.addEventListener('status', e => { state.textContent = JSON.parse(e.data).text; });
  setTimeout(fillAppLog, 0);
  return h('div', { class: 'grid', style: 'grid-template-columns:repeat(auto-fit,minmax(340px,1fr))' },
    h('div', { class: 'card stack' }, h('h3', {}, 'App-Protokoll (Server)'), appLog),
    h('div', { class: 'card stack' }, h('div', { class: 'titlebar' }, h('h3', {}, 'Miner-Logs live'), h('span', { class: 'spacer' }), state),
      minerLog, h('label', { class: 'check' }, follow, 'automatisch mitscrollen')));
}

function fillAppLog() {
  const box = $('#app-log');
  if (!box || !S.detail?.log) return;
  box.replaceChildren(...S.detail.log.map(l => h('div', {}, l)));
  box.scrollTop = box.scrollHeight;
}

function appendAppLog(line) {
  const box = $('#app-log');
  if (!box) return;
  box.append(h('div', {}, line));
  box.scrollTop = box.scrollHeight;
}

// ---------- Steuer ----------

async function renderTax() {
  mount(h('p', { class: 'muted' }, 'Lade …'));
  const t = await run(() => api('/tax/rewards'));
  if (!t) return;
  mount(h('div', { class: 'stack' },
    h('div', { class: 'card stack' },
      h('div', { class: 'titlebar' }, h('h2', {}, 'Steuer – dokumentierte Zuflüsse'), h('span', { class: 'spacer' }),
        h('a', { class: 'btn', href: '/api/v1/tax/rewards.csv', download: '' }, 'CSV exportieren')),
      h('p', { class: 'muted small' }, `Überwachte Wallets: ${t.wallets.length} · letzte Prüfung ${time(t.status)}. Wallets und Verkäufe bearbeitest du weiterhin in der Desktop-App (Steuer-Modul); die Erfassung läuft hier rund um die Uhr.`)),
    h('div', { class: 'card table-wrap' }, t.rewards.length ? h('table', {},
      h('thead', {}, h('tr', {}, ['Datum', 'Coin', 'Betrag', 'Wert (EUR)', 'Wallet', 'TXID'].map(x => h('th', {}, x)))),
      h('tbody', {}, t.rewards.map(r => h('tr', {}, h('td', {}, time(r.receivedAtUtc)), h('td', {}, r.coin), h('td', { class: 'num' }, r.amount),
        h('td', { class: 'num' }, r.eurValue != null ? n(r.eurValue, 2) : '–'), h('td', {}, r.walletLabel || ''), h('td', { class: 'small muted' }, (r.txId || '').slice(0, 16) + '…')))))
      : h('p', { class: 'muted' }, 'Noch keine Zuflüsse erfasst.'))));
}

// ---------- Einstellungen ----------

async function renderSettings() {
  mount(h('p', { class: 'muted' }, 'Lade …'));
  const [s, tokens, status] = await Promise.all([api('/settings'), api('/tokens'), api('/status')]).catch(e => { toast(e.message, 'error'); return []; });
  if (!s) return;
  const nt = s.notifications, wd = s.watchdog, pw = s.poolWatch, dr = s.dailyReport, ps = s.priceSource, la = s.logAlerts;
  const text = (obj, key, type = 'text') => h('input', { type, value: obj[key] ?? '', oninput: e => { obj[key] = type === 'number' ? Number(e.target.value) : e.target.value; } });
  const select = (obj, key, opts) => { const el = h('select', { onchange: e => { obj[key] = e.target.value; } }, opts.map(([v, l]) => h('option', { value: v }, l))); el.value = obj[key]; return el; };
  const pin = h('input', { type: 'password', inputmode: 'numeric', placeholder: s.viewerPinSet ? 'gesetzt – leer lassen = unverändert' : 'noch keine PIN' });
  const patterns = h('textarea', { rows: 3, value: (la.patterns || []).join('\n'), oninput: e => { la.patterns = e.target.value.split('\n').map(x => x.trim()).filter(Boolean); } });

  const save = async () => {
    s.newViewerPin = pin.value || null;
    if (await run(() => api('/settings', { method: 'PUT', body: s }), 'Einstellungen gespeichert.')) { pin.value = ''; s.viewerPinSet = s.viewerPinSet || !!s.newViewerPin; }
  };

  // Geräte
  const devName = h('input', { placeholder: 'Name' }), devHost = h('input', { placeholder: 'IP-Adresse oder Hostname' });
  const deviceRows = status.devices.map(d => {
    const name = h('input', { value: d.name });
    return h('tr', {}, h('td', {}, name), h('td', {}, d.host), h('td', {},
      h('button', { class: 'btn small', onclick: () => run(() => api(`/devices/${d.id}`, { method: 'PUT', body: { name: name.value } }), 'Gespeichert.') }, 'Speichern'), ' ',
      h('button', {
        class: 'btn small danger', onclick: async () => {
          if (!await confirmBox('Gerät entfernen', `„${d.name}“ entfernen? Verlauf in history.db und Steuerdaten bleiben erhalten.`, 'Entfernen', true)) return;
          if (await run(() => api(`/devices/${d.id}`, { method: 'DELETE' }), 'Entfernt.')) renderSettings();
        },
      }, 'Entfernen')));
  });

  // Token für die Desktop-App
  const tokName = h('input', { placeholder: 'z. B. Desktop Arbeitszimmer' });
  const tokenRows = tokens.map(t => h('tr', {}, h('td', {}, t.name), h('td', {}, time(t.createdUtc)), h('td', {}, t.lastUsedUtc ? time(t.lastUsedUtc) : 'nie'),
    h('td', {}, h('button', {
      class: 'btn small danger', onclick: async () => {
        if (!await confirmBox('Token widerrufen', `Token „${t.name}“ widerrufen? Die Desktop-App damit verliert sofort den Zugriff.`, 'Widerrufen', true)) return;
        if (await run(() => api(`/tokens/${t.id}`, { method: 'DELETE' }), 'Widerrufen.')) renderSettings();
      },
    }, 'Widerrufen'))));

  const curPw = h('input', { type: 'password', autocomplete: 'current-password' }), newPw = h('input', { type: 'password', autocomplete: 'new-password' });

  mount(h('div', { class: 'stack' },
    h('div', { class: 'card stack' }, h('h2', {}, 'Geräte'),
      h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Name', 'Adresse', ''].map(x => h('th', {}, x)))), h('tbody', {}, deviceRows))),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'Name'), devName), h('div', {}, h('label', {}, 'Adresse'), devHost),
        h('button', { class: 'btn primary', onclick: async () => { if (await run(() => api('/devices', { method: 'POST', body: { name: devName.value, host: devHost.value } }), 'Gerät hinzugefügt.')) renderSettings(); } }, 'Hinzufügen'))),
    h('div', { class: 'card stack' }, h('h2', {}, 'Allgemein'),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, 'Abfrage alle (s)'), text(s, 'intervalSeconds', 'number')),
        h('div', {}, h('label', {}, 'Live-Verlauf (min)'), text(s, 'historyMinutes', 'number')),
        h('div', {}, h('label', {}, 'Verlauf aufbewahren (Tage)'), text(s, 'historyDays', 'number')),
        h('div', {}, h('label', {}, 'Strompreis (ct/kWh)'), text(s, 'electricityCtPerKwh', 'number')),
        h('div', {}, h('label', {}, 'Währung'), text(s, 'currency')),
        h('div', {}, h('label', {}, 'Warnung ab ASIC (°C)'), text(s, 'tempWarn', 'number')),
        h('div', {}, h('label', {}, 'Wallets prüfen alle (min)'), text(s, 'walletPollMinutes', 'number')),
        h('div', {}, h('label', {}, 'Steuer-Erfassung alle (min)'), text(s, 'taxPollMinutes', 'number'))),
      checkInput(s, 'restartAfterApply', 'Nach Frequenz-/Spannungsänderung neu starten'),
      checkInput(s, 'checkForUpdates', 'Nach neuen Versionen suchen'),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'PIN für „Nur ansehen“ (mind. 4 Ziffern, „-“ = entfernen)'), pin))),
    h('div', { class: 'card stack' }, h('h2', {}, 'Push-Benachrichtigungen'),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, 'Dienst'), select(nt, 'provider', [['none', 'aus'], ['ntfy', 'ntfy'], ['telegram', 'Telegram']])),
        h('div', {}, h('label', {}, 'ntfy-Server'), text(nt, 'ntfyServer')), h('div', {}, h('label', {}, 'ntfy-Topic'), text(nt, 'ntfyTopic')),
        h('div', {}, h('label', {}, 'Telegram-Bot-Token'), text(nt, 'telegramBotToken', 'password')), h('div', {}, h('label', {}, 'Telegram-Chat-ID'), text(nt, 'telegramChatId'))),
      h('div', { class: 'row' }, checkInput(nt, 'onOffline', 'offline'), checkInput(nt, 'onOverheat', 'Überhitzung'), checkInput(nt, 'onFinds', 'Blockfund/Zufluss'),
        checkInput(nt, 'onMaintenance', 'Watchdog/Automatik/Firmware'), checkInput(nt, 'onRecord', 'Rekorde'), checkInput(nt, 'onLogAlerts', 'Log-Alarme'), checkInput(nt, 'onPool', 'Pool')),
      h('div', { class: 'row' },
        h('button', { class: 'btn', onclick: async () => { await save(); const r = await run(() => api('/notifications/test', { method: 'POST', body: {} })); if (r) toast(r.ok ? 'Testnachricht gesendet.' : r.error, r.ok ? 'ok' : 'error'); } }, 'Speichern & testen'),
        h('button', { class: 'btn', onclick: async () => { const r = await run(() => api('/report/send', { method: 'POST', body: {} })); if (r) toast(r.ok ? 'Tagesbericht gesendet.' : r.error, r.ok ? 'ok' : 'error'); } }, 'Tagesbericht jetzt senden'))),
    h('div', { class: 'card stack' }, h('h2', {}, 'Überwachung'),
      checkInput(wd, 'enabled', 'Watchdog: Miner ohne Hashrate neu starten'),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'ohne Hashrate seit (min)'), text(wd, 'zeroHashMinutes', 'number')), h('div', {}, h('label', {}, 'Sperrzeit (min)'), text(wd, 'cooldownMinutes', 'number'))),
      checkInput(pw, 'enabled', 'Pool/Shares überwachen'),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'Ablehnungen ab (%)'), text(pw, 'rejectPercent', 'number')), h('div', {}, h('label', {}, 'Zeitfenster (min)'), text(pw, 'windowMinutes', 'number')),
        h('div', {}, h('label', {}, 'mind. Shares'), text(pw, 'minShares', 'number')), h('div', {}, h('label', {}, 'Antwortzeit ab (ms)'), text(pw, 'responseMs', 'number'))),
      checkInput(dr, 'enabled', 'Tagesbericht per Push'),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'Uhrzeit (Stunde)'), text(dr, 'hour', 'number'))),
      checkInput(la, 'onErrors', 'Log-Alarm bei Fehlerzeilen (E)'),
      h('div', { class: 'form' }, h('div', { class: 'wide' }, h('label', {}, 'Log-Alarm bei Zeilen mit (je Zeile ein Muster)'), patterns))),
    h('div', { class: 'card stack' }, h('h2', {}, 'Strompreis-Quelle'),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'Quelle'), select(ps, 'source', [['none', 'keine'], ['awattar-de', 'aWATTar Deutschland'], ['awattar-at', 'aWATTar Österreich'], ['tibber', 'Tibber']])),
        h('div', {}, h('label', {}, 'Tibber-Token'), text(ps, 'tibberToken', 'password')))),
    h('div', { class: 'row' }, h('button', { class: 'btn primary', onclick: save }, 'Einstellungen speichern')),
    h('div', { class: 'card stack' }, h('h2', {}, 'Desktop-App verbinden (API-Token)'),
      h('p', { class: 'muted small' }, 'In der Desktop-App unter Einstellungen → Betriebsart „Server“ die Adresse dieses Servers und das Token eintragen. Das Token wird nur einmal angezeigt.'),
      tokens.length ? h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Name', 'erstellt', 'zuletzt benutzt', ''].map(x => h('th', {}, x)))), h('tbody', {}, tokenRows))) : null,
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'Name'), tokName),
        h('button', {
          class: 'btn', onclick: async () => {
            const r = await run(() => api('/tokens', { method: 'POST', body: { name: tokName.value } }));
            if (!r) return;
            await confirmBox('Neues Token', h('div', { class: 'stack' }, h('p', {}, 'Jetzt kopieren – es wird nicht noch einmal angezeigt:'), h('input', { value: r.token, readonly: true, onfocus: e => e.target.select() })), 'Fertig');
            renderSettings();
          },
        }, 'Token erzeugen'))),
    updateCard(),
    h('div', { class: 'card stack' }, h('h2', {}, 'Admin-Passwort ändern'),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'Aktuell'), curPw), h('div', {}, h('label', {}, 'Neu (mind. 10 Zeichen)'), newPw),
        h('button', { class: 'btn', onclick: async () => { if (await run(() => api('/password', { method: 'POST', body: { current: curPw.value, password: newPw.value } }), 'Passwort geändert – bitte neu anmelden.')) { S.role = 'None'; stopEvents(); renderLogin(); } } }, 'Ändern'))),
    h('p', { class: 'muted small' }, `Server ${S.info.version} · ${S.info.os}`)));
}

// ---------- Zusatzlüfter (Pico) ----------

const ROLE_TEXT = { none: 'nicht belegt', miner: 'VR-Lüfter', case: 'Gehäuse' };

function fanRows(fans) {
  if (!fans) return [h('p', { class: 'muted' }, 'Lüftersteuerung ist ausgeschaltet.')];
  const sensors = fans.sensors || [];
  const head = h('div', { class: 'stack' },
    h('p', { class: fans.connected ? 'ok' : 'danger' },
      fans.connected ? `Verbunden${fans.device ? ': ' + fans.device : ''}` : `Nicht verbunden${fans.error ? ': ' + fans.error : ''} – Lüfter laufen dann auf 100 %.`),
    sensors.length
      ? h('div', { class: 'row' }, sensors.map(s => h('span', { class: `pill ${s.hot || s.temp == null ? 'danger' : 'gray'}`, title: `Warnung ab ${n(s.warnTemp, 1)} °C` },
          `${s.name}: ${s.temp == null ? 'fehlt' : n(s.temp, 1) + ' °C'}${s.hot ? ' – zu warm!' : ''}`)))
      : fans.connected ? h('p', { class: 'muted' }, 'Kein Temperaturfühler (DS18B20) erkannt.') : null);
  if (!fans.channels.length) return [head, h('p', { class: 'muted' }, 'Noch kein Kanal belegt.')];
  return [head, h('div', { class: 'table-wrap' }, h('table', {},
    h('thead', {}, h('tr', {}, ['Kanal', 'Lüfter', 'Modus', 'Soll', 'Drehzahl', 'Begründung'].map(x => h('th', {}, x)))),
    h('tbody', {}, fans.channels.map(c => h('tr', {},
      h('td', { class: 'mono' }, `K${c.channel}`),
      h('td', {}, c.name),
      h('td', {}, c.mode === 'manual' ? 'manuell' : 'Automatik'),
      h('td', { class: 'num' }, `${c.percent} %`),
      h('td', { class: `num ${c.stalled ? 'danger' : ''}` }, c.rpm == null ? '–' : c.stalled ? 'steht!' : `${c.rpm} U/min`),
      h('td', { class: 'small muted' }, c.reason))))))];
}

function updateFanTable() {
  const box = $('#fan-status');
  if (box && S.status) box.replaceChildren(...fanRows(S.status.fans));
}

async function renderFans() {
  mount(h('p', { class: 'muted' }, 'Lade …'));
  const data = await run(() => api('/fans'));
  if (!data) return;
  const status = h('div', { id: 'fan-status', class: 'stack' }, fanRows(data.status));
  const display = await run(() => api('/display'));
  const parts = [h('div', { class: 'card stack' }, h('h2', {}, 'Zusatzlüfter'), status)];
  if (isAdmin()) parts.push(quickActions(data.status, display));
  if (display) parts.push(displayCard(display));
  if (isAdmin()) parts.push(fanEditor(data));
  else parts.push(h('p', { class: 'muted small' }, 'Einstellungen ändern kann nur der Admin.'));
  mount(h('div', { class: 'stack' }, parts));
}

/** Die vier Taster am Pico – hier auch per Klick. */
function quickActions(fans, display) {
  const mode = fans?.override || 'None';
  const label = { None: 'nach Einstellung (Automatik)', Off: 'AUS per Taste – Sicherheitsregeln aktiv', Full: 'alle 100 %' }[mode];
  const next = async () => { const r = await run(() => api('/display/next', { method: 'POST', body: {} }), 'Anzeige wechselt in Kürze (frühestens 30 s nach der letzten Aktualisierung).'); if (r) renderFans(); };
  const set = async m => { const r = await run(() => api('/fans/override', { method: 'POST', body: { mode: m } })); if (r) { const box = $('#fan-status'); if (box) box.replaceChildren(...fanRows(r.status)); renderFans(); } };
  return h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h2', {}, 'Schnellaktionen'), h('span', { class: 'spacer' }),
      h('span', { class: `pill ${mode === 'None' ? 'gray' : ''}` }, `Lüfter: ${label}`)),
    h('p', { class: 'muted small' }, 'Dieselben Aktionen wie die Taster am Pico: 1 = Anzeige weiter/quittieren, 2 = Automatik, 3 = alle 100 %, 3 (5 s halten) = Lüfter aus, 4 (3 s halten) = Neustart. „Aus“ und „100 %“ gelten bis zum nächsten Neustart des Servers.'),
    h('div', { class: 'row' },
      h('button', { class: 'btn', onclick: next }, '1 · Anzeige weiter'),
      h('button', { class: 'btn', onclick: () => set('auto') }, '2 · Automatik'),
      h('button', { class: 'btn', onclick: () => set('full') }, '3 · Alle 100 %'),
      h('button', { class: 'btn', onclick: () => set('off') }, '3 lang · Lüfter aus'),
      h('button', {
        class: 'btn danger', onclick: async () => {
          const text = display?.rebootAvailable
            ? 'Pico und Raspberry Pi neu starten?\n\nLaufende Benchmarks werden beendet und ihre Einstellungen wiederhergestellt. Die Oberfläche ist ca. 1–2 Minuten nicht erreichbar, die Miner laufen weiter.'
            : 'Pico neu starten?\n\nEin Neustart des Rechners ist auf dieser Installation nicht eingerichtet.';
          if (!await confirmBox('Neustart', text, 'Neu starten', true)) return;
          const r = await run(() => api('/system/reboot', { method: 'POST', body: {} }));
          if (r) toast(r.message, 'ok', 15000);
        },
      }, '4 · Neustart')));
}

/** E-Paper: Vorschau genau wie auf dem Display, Status, Einstellungen. */
function displayCard(d) {
  const st = d.status;
  const img = h('img', { src: `/api/v1/display/preview.png?t=${Date.now()}`, alt: 'Vorschau der E-Paper-Anzeige', style: 'width:100%;max-width:800px;border:1px solid var(--border);border-radius:6px;background:#fff' });
  const scenes = [['', 'Als Nächstes'], ['Overview', 'Übersicht'], ['Daily', 'Tagesbilanz'], ['Chart', 'Verlauf 24 h'], ['Soak', 'Dauertest'],
    ['Network', 'Pool & Netzwerk'], ['BlockFound', 'Blockfund'], ['Alarm', 'Warnungen'], ['BestDiff', 'Best-Diff-Rekord']];
  const sceneSel = h('select', { onchange: () => { img.src = `/api/v1/display/preview.png?scene=${sceneSel.value}&t=${Date.now()}`; } },
    scenes.map(([v, t]) => h('option', { value: v }, t)));
  const info = !st.enabled ? 'Anzeige ist ausgeschaltet – die Vorschau zeigt, was sie anzeigen würde.'
    : `${st.connected ? 'Pico verbunden' : 'Pico nicht verbunden'} · zuletzt ${st.lastShown ? time(st.lastShown) : 'noch nie'}` +
      (st.nextDue ? ` · nächste Aktualisierung ab ${new Date(st.nextDue).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' })}` : '') +
      (st.refreshing ? ' · baut gerade auf …' : '') + (st.error ? ` · ${st.error}` : '');
  const parts = [
    h('div', { class: 'titlebar' }, h('h2', {}, 'E-Paper-Anzeige'), h('span', { class: 'spacer' }),
      sceneSel,
      h('button', { class: 'btn small', onclick: () => { img.src = `/api/v1/display/preview.png?scene=${sceneSel.value}&t=${Date.now()}`; } }, 'Vorschau neu laden')),
    h('p', { class: `small ${st.error ? 'danger' : 'muted'}` }, info),
    img,
  ];
  if (isAdmin() && d.settings) {
    const s = structuredClone(d.settings);
    parts.push(
      checkInput(s, 'enabled', 'Anzeige einschalten (7,5″ E-Paper am Pico)'),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, 'Titel'), h('input', { value: s.title, oninput: e => { s.title = e.target.value; } })),
        h('div', {}, h('label', {}, 'aktualisieren alle (min, mind. 3)'), numInput(s, 'intervalMinutes')),
        h('div', {}, h('label', {}, 'Ruhe von (Uhr)'), numInput(s, 'quietFromHour')),
        h('div', {}, h('label', {}, 'bis (Uhr)'), numInput(s, 'quietToHour'))),
      checkInput(s, 'quietEnabled', 'Nachts nur bei Warnungen aktualisieren'),
      h('h3', {}, 'Seiten (Taste 1 blättert)'),
      h('div', { class: 'row' },
        checkInput(s.pages, 'overview', 'Übersicht'),
        checkInput(s.pages, 'daily', 'Tagesbilanz'),
        checkInput(s.pages, 'chart', 'Verlauf 24 h'),
        checkInput(s.pages, 'soak', 'Dauertest (wenn aktiv)'),
        checkInput(s.pages, 'network', 'Pool & Netzwerk')),
      checkInput(s, 'rotatePages', 'Bei jeder Aktualisierung zur nächsten Seite wechseln'),
      h('h3', {}, 'Sonderanzeigen'),
      h('div', { class: 'form' },
        h('div', {}, checkInput(s, 'blockFoundScreen', 'Blockfund als Vollbild')),
        h('div', {}, h('label', {}, 'stehen lassen (Stunden, bis Taste 1)'), numInput(s, 'blockFoundHoldHours'))),
      checkInput(s, 'alarmFullscreen', 'Warnungen als Vollbild (Taste 1 quittiert bis zur nächsten neuen Warnung)'),
      checkInput(s, 'bestDiffNotice', 'Neuen Best-Diff-Rekord einmal groß anzeigen'),
      h('h3', {}, 'Taster'),
      checkInput(s, 'buttonsEnabled', 'Taster am Pico auswerten'),
      checkInput(s, 'allowSystemReboot', 'Taste 4 (3 s halten) startet auch den Raspberry Pi neu'),
      h('div', { class: 'row' },
        h('button', { class: 'btn primary', onclick: async () => { if (await run(() => api('/display', { method: 'PUT', body: s }), 'Anzeige-Einstellungen gespeichert.')) renderFans(); } }, 'Speichern'),
        h('button', { class: 'btn', onclick: () => run(() => api('/display/refresh', { method: 'POST', body: {} }), 'Anzeige wird aktualisiert, sobald die Mindestpause von 3 Minuten um ist.') }, 'Jetzt aktualisieren')),
      h('p', { class: 'muted small' }, 'Das E-Paper wird höchstens alle 3 Minuten neu aufgebaut (Herstellerempfehlung), ein Bildaufbau dauert etwa 16 Sekunden.'));
  }
  return h('div', { class: 'card stack' }, parts);
}

function curveInputs(curve) {
  return [
    h('div', {}, h('label', {}, 'ab °C (Start)'), numInput(curve, 'startTemp', 0.5)),
    h('div', {}, h('label', {}, 'dort %'), numInput(curve, 'startPercent')),
    h('div', {}, h('label', {}, '100 % ab °C'), numInput(curve, 'fullTemp', 0.5)),
    h('div', {}, h('label', {}, 'darunter %'), numInput(curve, 'minPercent')),
    h('div', {}, h('label', {}, 'Hysterese °C'), numInput(curve, 'hysteresis', 0.5)),
  ];
}

function selectInput(obj, key, options, onchange) {
  const s = h('select', { onchange: e => { obj[key] = e.target.value; onchange?.(); } }, options.map(([v, l]) => h('option', { value: v }, l)));
  s.value = obj[key] ?? '';
  return s;
}

function fanEditor(data) {
  const f = structuredClone(data.settings);
  const miners = data.miners;
  const channelsBox = h('div', { class: 'stack' });
  const renderChannels = () => channelsBox.replaceChildren(...f.channels.map(c => {
    const row = h('div', { class: 'panel' },
      h('div', { class: 'titlebar' }, h('h3', {}, `K${c.channel}`),
        h('span', { class: 'muted small' }, `PWM GP${(c.channel - 1) * 2} · Tacho GP${15 + c.channel}`)),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, 'Verwendung'), selectInput(c, 'role', [['none', 'nicht belegt'], ['miner', 'VR-Lüfter eines Miners'], ['case', 'Gehäuse (Gruppe)']], renderChannels)),
        c.role === 'miner' ? h('div', {}, h('label', {}, 'Miner'), selectInput(c, 'minerHost', [['', '— wählen —'], ...miners.map(m => [m.host, m.name])])) : null,
        c.role !== 'none' ? h('div', {}, h('label', {}, 'Name (optional)'), h('input', { value: c.name, oninput: e => { c.name = e.target.value; } })) : null,
        c.role === 'miner' ? h('div', {}, h('label', {}, 'Modus'), selectInput(c, 'mode', [['auto', 'Automatik (VR-Temperatur)'], ['manual', 'Manuell']], renderChannels)) : null,
        c.role === 'miner' && c.mode === 'manual' ? h('div', {}, h('label', {}, 'Drehzahl %'), numInput(c, 'manualPercent')) : null),
      c.role === 'miner' && c.mode === 'auto' ? h('div', { class: 'form' }, curveInputs(c.curve)) : null,
      c.role !== 'none' ? checkInput(c, 'hasTach', 'Lüfter hat Drehzahlsignal (Meldung, wenn er steht)') : null);
    return row;
  }));
  renderChannels();

  const cs = f.case;
  const minerChecks = h('div', { class: 'row' }, miners.map(m => h('label', { class: 'check' },
    h('input', {
      type: 'checkbox', checked: cs.miners.length === 0 || cs.miners.includes(m.host),
      onchange: e => {
        const all = miners.map(x => x.host);
        let sel = cs.miners.length === 0 ? all.slice() : cs.miners.slice();
        sel = e.target.checked ? [...new Set([...sel, m.host])] : sel.filter(x => x !== m.host);
        cs.miners = sel.length === all.length ? [] : sel;
      },
    }), m.name)));
  const caseBox = h('div', { class: 'stack' });
  const renderCase = () => caseBox.replaceChildren(
    h('div', { class: 'form' },
      h('div', {}, h('label', {}, 'Modus'), selectInput(cs, 'mode', [['auto', 'Automatik'], ['manual', 'Manuell']], renderCase)),
      cs.mode === 'manual' ? h('div', {}, h('label', {}, 'Drehzahl %'), numInput(cs, 'manualPercent')) : null,
      cs.mode === 'auto' ? h('div', {}, h('label', {}, 'Messgröße'), selectInput(cs, 'sensor', [['vr', 'VR-Temperatur der Miner'], ['asic', 'ASIC-Temperatur der Miner'], ['case', 'Temperaturfühler (DS18B20)']], renderCase)) : null,
      cs.mode === 'auto' ? h('div', {}, h('label', {}, 'Miner ohne Daten → mind. %'), numInput(cs, 'unknownPercent')) : null),
    cs.mode === 'auto' ? h('div', { class: 'stack' },
      cs.sensor === 'case'
        ? h('p', { class: 'muted small' }, 'Es zählt der wärmste Fühler mit Haken „Gehäuselüfter“ (Karte „Temperaturfühler“). Fehlt einer davon, gilt „Miner ohne Daten → mind. %“.')
        : h('p', { class: 'muted small' }, 'Es zählt die höchste Temperatur der ausgewählten Miner (keine Auswahl = alle).'),
      cs.sensor === 'case' ? null : minerChecks,
      h('div', { class: 'form' }, curveInputs(cs.curve)),
      checkInput(cs, 'nightEnabled', 'Nachtbetrieb (leiser)'),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, 'von Uhr'), numInput(cs, 'nightFromHour')),
        h('div', {}, h('label', {}, 'bis Uhr'), numInput(cs, 'nightToHour')),
        h('div', {}, h('label', {}, 'höchstens %'), numInput(cs, 'nightMaxPercent'))),
      h('p', { class: 'muted small' }, 'Nachts wird gedrosselt, außer eine Temperatur erreicht den 100-%-Punkt der Kurve.')) : null);
  renderCase();

  // Temperaturfühler: Pico meldet jeden mit fester Kennung; neue trägt der Server selbst ein
  f.sensors = f.sensors || [];
  const sensorBox = h('div', { class: 'stack' });
  const live = id => ((S.status && S.status.fans && S.status.fans.sensors) || []).find(s => s.id === id);
  const renderSensors = () => sensorBox.replaceChildren(
    f.sensors.length === 0
      ? h('p', { class: 'muted' }, 'Noch kein Fühler erkannt. DS18B20 an GP26 anschließen (alle parallel, ein 4,7-kΩ-Widerstand nach 3,3 V) – der Server trägt jeden neuen Fühler hier ein.')
      : h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, ['Jetzt', 'Name', 'Warnung ab °C', 'Gehäuselüfter', 'Anzeige', 'Kennung', ''].map(x => h('th', {}, x)))),
        h('tbody', {}, f.sensors.map(s => {
          const l = live(s.id);
          return h('tr', {},
            h('td', { class: `num ${!l || l.temp == null || l.hot ? 'danger' : ''}` }, !l || l.temp == null ? 'fehlt' : `${n(l.temp, 1)} °C`),
            h('td', {}, h('input', { value: s.name, maxlength: 24, style: 'min-width:9em', oninput: e => { s.name = e.target.value; } })),
            h('td', {}, numInput(s, 'warnTemp', 0.5)),
            h('td', {}, h('input', { type: 'checkbox', checked: s.caseFans, onchange: e => { s.caseFans = e.target.checked; } })),
            h('td', {}, h('input', { type: 'checkbox', checked: s.showOnDisplay, onchange: e => { s.showOnDisplay = e.target.checked; } })),
            h('td', { class: 'mono small muted' }, s.id),
            h('td', {}, h('button', {
              class: 'btn small', title: 'Aus der Liste entfernen (z. B. abgebauter Fühler). Wird er wieder gemeldet, erscheint er neu.',
              onclick: () => { f.sensors = f.sensors.filter(x => x !== s); renderSensors(); },
            }, 'Entfernen')));
        })))),
    h('p', { class: 'muted small' }, 'Welcher ist welcher? Einen Fühler kurz in der Hand anwärmen und „Jetzt“ beobachten (aktualisiert beim Neuladen der Seite). ' +
      '„Gehäuselüfter“: zählt für die Messgröße „Temperaturfühler“ der Gehäuselüfter; fehlt so ein Fühler, laufen sie auf dem Wert für „unbekannt“. ' +
      'Über der Warnschwelle gibt es eine Push-Meldung und eine rote Zeile auf dem Display.'),
    h('div', { class: 'form' }, h('div', {}, h('label', {}, 'Warnschwelle für neu erkannte Fühler °C'), numInput(f, 'caseTempWarn', 0.5))));
  renderSensors();

  const save = async () => {
    const r = await run(() => api('/fans', { method: 'PUT', body: f }), 'Lüfter-Einstellungen gespeichert.');
    if (r) { const box = $('#fan-status'); if (box) box.replaceChildren(...fanRows(r.status)); }
  };
  return h('div', { class: 'stack' },
    h('div', { class: 'card stack' }, h('h2', {}, 'Verbindung'),
      checkInput(f, 'enabled', 'Lüfter regeln (Pico per USB am Server; der Port gilt auch für Anzeige und Taster)'),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, 'Port („auto“ = Pico automatisch finden)'), h('input', { value: f.port, oninput: e => { f.port = e.target.value; } }))),
      h('div', { class: 'row' },
        h('button', { class: 'btn primary', onclick: save }, 'Speichern'),
        h('button', {
          class: 'btn', onclick: async () => {
            if (!await confirmBox('Programm neu aufspielen', 'Das Lüfterprogramm wird neu auf den Pico geschrieben. Die Lüfter laufen dabei kurz mit 100 %.', 'Aufspielen')) return;
            const r = await run(() => api('/fans/firmware', { method: 'POST', body: {} }));
            if (r) { toast(r.ok ? 'Programm aufgespielt, Pico verbunden.' : 'Pico nicht verbunden – siehe Status.', r.ok ? 'ok' : 'error'); const box = $('#fan-status'); if (box) box.replaceChildren(...fanRows(r.status)); }
          },
        }, 'Programm neu aufspielen'))),
    h('div', { class: 'card stack' }, h('h2', {}, 'Kanäle'), channelsBox),
    h('div', { class: 'card stack' }, h('h2', {}, 'Gehäuselüfter (alle Kanäle mit „Gehäuse“)'), caseBox),
    h('div', { class: 'card stack' }, h('h2', {}, 'Temperaturfühler'), sensorBox),
    h('div', { class: 'row' }, h('button', { class: 'btn primary', onclick: save }, 'Speichern')),
    h('p', { class: 'muted small' }, 'Immer aktiv: Miner offline oder Daten älter als 30 s → sein Lüfter auf 100 %. Bekommt der Pico 5 s lang keinen Befehl, schaltet er selbst alle Lüfter auf 100 %.'));
}

/** Server-Update: prüfen, Hinweise je Installationsart, Installation per Klick (Pi/Linux-Paket, Windows-Dienst). */
function updateCard() {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, 'Lade …'));
  const kinds = {
    Docker: 'Docker: Update mit „docker compose pull && docker compose up -d“.',
    LinuxPackage: 'Raspberry Pi / Linux-Paket: Installation per Klick, die bisherige Version bleibt als Rückfall erhalten.',
    WindowsService: 'Windows-Dienst: Installation per Klick (stilles Setup, der Dienst startet neu).',
    Manual: 'Von Hand gestartet: neues Paket selbst installieren.',
  };
  const render = u => body.replaceChildren(
    h('p', {}, `Installiert: ${u.current}` + (u.latest ? ` · verfügbar: ${u.latest}` : '') + (u.message ? ` · ${u.message}` : '')),
    h('p', { class: 'muted small' }, kinds[u.kind] || ''),
    u.notes ? h('div', { class: 'log', style: 'height:auto;max-height:180px' }, u.notes) : null,
    h('div', { class: 'row' },
      h('button', { class: 'btn', onclick: async () => { const r = await run(() => api('/admin/update/check', { method: 'POST', body: {} })); if (r) load(); } }, 'Nach Updates suchen'),
      u.latest && u.canInstall ? h('button', {
        class: 'btn primary', onclick: async () => {
          if (!await confirmBox('Server aktualisieren', `BitaxeTuner-Server ${u.latest} installieren?\n\nDie Datei wird gegen die veröffentlichte SHA-256-Prüfsumme geprüft. Laufende Benchmarks werden gestoppt (Einstellungen wiederhergestellt), danach startet der Server neu. Die Seite verbindet sich anschließend von selbst wieder.`, 'Installieren')) return;
          const r = await run(() => api('/admin/update/install', { method: 'POST', body: {} }));
          if (r) toast(r.message, 'ok', 15000);
        },
      }, `Update ${u.latest} installieren`) : null));
  const load = () => api('/admin/update').then(render).catch(e => toast(e.message, 'error'));
  load();
  return h('div', { class: 'card stack' }, h('h2', {}, 'Server-Update'), body);
}

boot().catch(e =>mount(h('div', { class: 'card danger' }, 'Server nicht erreichbar: ', e.message)));
