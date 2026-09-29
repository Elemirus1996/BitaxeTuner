/* BitaxeTuner – Browser-Oberfläche des Servers.
   Kein Build-Schritt, keine externen Bibliotheken (läuft offline im Heimnetz, CSP "script-src 'self'").
   Texte aus Geräten/Konfiguration werden nie als HTML eingefügt (nur textContent). */
'use strict';

const S = { role: 'None', csrf: null, status: null, info: null, es: null, logEs: null, detail: null, route: null, chartRange: '1h' };
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
/** Inhalt ersetzen; leere Teile (null/false) und verschachtelte Listen wie bei h(). */
const fill = (el, ...kids) => el.replaceChildren(...kids.flat(Infinity).filter(k => k != null && k !== false).map(k => k instanceof Node ? k : String(k)));

// ---------- Sprache ----------
// Der deutsche Text ist der Schlüssel; die Tabelle der gewählten Sprache kommt vom Server (/api/v1/i18n/{lang}).
// Fehlt eine Übersetzung, erscheint der deutsche Text. Platzhalter {0}, {1} …; {{ und }} für geschweifte Klammern.

const LANGS = ['de', 'en'];
let LANG = 'de', LOCALE = 'de-DE', I18N = {};

function t(s, ...args) {
  const text = I18N[s] || s;
  return text.replace(/\{\{|\}\}|\{(\d+)\}/g, (m, i) => i === undefined ? m[0] : String(args[+i] ?? ''));
}

/** Nutzerwahl (gespeichert) → Browser-Sprache → Standard des Servers. */
function pickLanguage(serverDefault) {
  const chosen = localStorageGet('lang');
  if (LANGS.includes(chosen)) return chosen;
  for (const l of navigator.languages || [navigator.language]) {
    const two = String(l || '').slice(0, 2).toLowerCase();
    if (LANGS.includes(two)) return two;
  }
  return LANGS.includes(serverDefault) ? serverDefault : 'en';
}

/** Zahlen/Datum im Format des Browsers, solange dessen Sprache passt – sonst das übliche Format der Sprache. */
function pickLocale(lang) {
  const own = (navigator.languages || [navigator.language]).find(l => String(l || '').slice(0, 2).toLowerCase() === lang);
  try { if (own) return Intl.getCanonicalLocales(own)[0]; } catch { /* ungültig */ }
  return lang === 'de' ? 'de-DE' : 'en-GB';
}

async function loadLanguage(serverDefault) {
  LANG = pickLanguage(serverDefault);
  LOCALE = pickLocale(LANG);
  document.documentElement.lang = LANG;
  if (LANG !== 'de') {
    try {
      const r = await fetch(`/api/v1/i18n/${LANG}`, { credentials: 'same-origin' });
      if (r.ok) I18N = await r.json();
    } catch { /* ohne Tabelle: deutsche Texte */ }
  }
  applyStaticTexts();
}

/** Feste Texte aus index.html. */
function applyStaticTexts() {
  const nav = { overview: t('Übersicht'), compare: t('Vergleich'), fans: t('Lüfter & Anzeige'), tax: t('Steuer'), reports: t('Berichte'), settings: t('Einstellungen') };
  for (const [k, v] of Object.entries(nav)) { const a = $(`[data-nav="${k}"]`); if (a) a.textContent = v; }
  $('#live').title = t('Live-Verbindung');
  $('#theme').title = t('Hell/Dunkel');
  $('#theme').setAttribute('aria-label', t('Hell/Dunkel umschalten'));
  $('#logout').textContent = t('Abmelden');
  $('#dialog [value="cancel"]').textContent = t('Abbrechen');
  const lang = $('#lang');
  const other = LANG === 'de' ? 'en' : 'de';
  lang.textContent = other.toUpperCase();
  lang.title = other === 'en' ? 'English' : 'Deutsch';
  lang.setAttribute('aria-label', t('Sprache wechseln'));
}

function switchLanguage() {
  localStorageSet('lang', LANG === 'de' ? 'en' : 'de');
  location.reload();
}

// ---------- Formatierung ----------

// Wochentage als Bitmaske (Bit 0 = Sonntag … Bit 6 = Samstag) wie ScheduleEntry.Days auf dem Server
const DAY_NAMES = { de: ['So', 'Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa'], en: ['Su', 'Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa'] };
function daysText(mask) {
  mask &= 127;
  if (mask === 127) return t('täglich');
  const names = DAY_NAMES[LANG] || DAY_NAMES.de;
  if (mask === 0b0111110) return `${names[1]}-${names[5]}`;
  if (mask === 0b1000001) return `${names[6]},${names[0]}`;
  return [1, 2, 3, 4, 5, 6, 0].filter(d => mask & (1 << d)).map(d => names[d]).join(',');
}
/** „Mo-Fr“, „Sa,So“, „täglich“ – deutsche und englische Kürzel; null = nicht erkannt. */
function parseDays(text) {
  const s = String(text || '').trim().toLowerCase().replace(/\s+/g, '');
  if (['', 'täglich', 'taeglich', 'alle', 'daily', 'everyday', 'all', 'mo-so', 'mo-su'].includes(s)) return 127;
  const day = x => { for (const names of Object.values(DAY_NAMES)) { const i = names.findIndex(n => n.toLowerCase() === x.slice(0, 2)); if (i >= 0) return i; } return -1; };
  let mask = 0;
  for (const part of s.split(/[,;]/)) {
    const [a, b = a] = part.split('-').map(day);
    if (a < 0 || b < 0) return null;
    for (let d = a; ; d = (d + 1) % 7) { mask |= 1 << d; if (d === b) break; }
  }
  return mask;
}

const n = (v, d = 1) => v == null || Number.isNaN(v) ? '–' : new Intl.NumberFormat(LOCALE, { minimumFractionDigits: d, maximumFractionDigits: d }).format(v);
function hash(gh) {
  if (gh == null) return '–';
  return gh >= 1000 ? t('{0} TH/s', n(gh / 1000, 2)) : t('{0} GH/s', n(gh, 0));
}
function dur(sec) {
  if (sec == null) return '–';
  const d = Math.floor(sec / 86400), hh = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60);
  return d > 0 ? t('{0} T {1} h', d, hh) : t('{0} h {1} min', hh, m);
}
const time = tv => tv ? new Date(tv).toLocaleString(LOCALE, { dateStyle: 'short', timeStyle: 'short' }) : '–';

// ---------- API ----------

class ApiError extends Error { constructor(m, s) { super(m); this.status = s; } }

async function api(path, { method = 'GET', body } = {}) {
  const headers = { Accept: 'application/json', 'Accept-Language': LANG };
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
    throw new ApiError(t('Bitte anmelden.'), 401);
  }
  if (!r.ok) throw new ApiError(data?.error || t('Fehler {0}', r.status), r.status);
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
  const tv = h('div', { class: `toast ${kind}` }, text);
  $('#toasts').append(tv);
  setTimeout(() => tv.remove(), ms);
}

/** Bestätigung mit Text (z. B. alter → neuer Wert vom Server). */
function confirmBox(title, body, okLabel = t('Bestätigen'), danger = false) {
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
  $('#lang').addEventListener('click', switchLanguage);
  window.addEventListener('hashchange', route);

  S.info = await api('/info');
  await loadLanguage(S.info.language);
  $('#version').textContent = 'v' + S.info.version;
  if (S.info.setupRequired) return renderSetup();
  const session = await api('/session');
  S.role = session.role; S.csrf = session.csrf;
  if (S.role === 'None') return renderLogin();
  started();
}

function localStorageGet(k) { try { return localStorage.getItem('bt.' + k); } catch { return null; } }
function localStorageSet(k, v) { try { localStorage.setItem('bt.' + k, v); } catch { /* egal */ } }
function applyTheme(tv) {
  if (tv === 'light' || tv === 'dark') document.documentElement.dataset.theme = tv;
  else delete document.documentElement.dataset.theme;
}

function renderSetup() {
  $('#nav').hidden = true;
  const code = h('input', { autocomplete: 'one-time-code', placeholder: '1234-5678-9012' });
  const pw = h('input', { type: 'password', autocomplete: 'new-password' });
  const pw2 = h('input', { type: 'password', autocomplete: 'new-password' });
  const submit = async e => {
    e.preventDefault();
    if (pw.value !== pw2.value) return toast(t('Die Passwörter stimmen nicht überein.'), 'error');
    const r = await run(() => api('/setup', { method: 'POST', body: { code: code.value, password: pw.value } }));
    if (r) { S.role = r.role; S.csrf = r.csrf; S.info.setupRequired = false; toast(t('Server eingerichtet.'), 'ok'); started(); }
  };
  mount(h('div', { class: 'login' }, h('form', { class: 'card stack', onsubmit: submit },
    h('h2', {}, t('Server einrichten')),
    h('p', { class: 'muted' }, t('Den Einrichtungs-Code findest du im Protokoll des Dienstes (Raspberry Pi: „journalctl -u bitaxetuner“, Docker: „docker logs bitaxetuner“, Windows: Ereignisanzeige bzw. Konsole).')),
    h('div', {}, h('label', {}, t('Einrichtungs-Code')), code),
    h('div', {}, h('label', {}, t('Admin-Passwort (mind. 10 Zeichen)')), pw),
    h('div', {}, h('label', {}, t('Passwort wiederholen')), pw2),
    h('button', { class: 'btn primary', type: 'submit' }, t('Einrichten')))));
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
    h('h2', {}, t('Anmelden')),
    h('p', { class: 'muted' }, t('Admin-Passwort oder PIN der Ansicht („Nur ansehen“).')),
    h('div', {}, h('label', {}, t('Passwort oder PIN')), pw),
    h('button', { class: 'btn primary', type: 'submit' }, t('Anmelden')))));
  pw.focus();
}

function started() {
  $('#nav').hidden = false;
  $('#logout').hidden = false;
  $('#role').textContent = isAdmin() ? 'Admin' : t('Nur ansehen');
  document.querySelectorAll('[data-admin]').forEach(e => { e.hidden = !isAdmin(); });
  startEvents();
  route();
  if (isAdmin()) refreshUpdateInfo();
}

/** Update-Stand für den Hinweis in der Übersicht – höchstens alle 30 min vom Server holen (Übersicht zeichnet oft neu). */
let updateTimer = null;
async function refreshUpdateInfo() {
  clearTimeout(updateTimer);
  try {
    const u = await api('/admin/update');
    const changed = (S.update?.latest ?? null) !== (u.latest ?? null);
    S.update = u;
    if (changed && S.route?.view === 'overview' && S.status) renderOverview();
  } catch { /* Hinweis ist nur Komfort */ }
  updateTimer = setTimeout(refreshUpdateInfo, 30 * 60 * 1000);
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
    toast(t('Tuning ({0}): {1}', d.source, d.change));
    if (S.route?.view === 'device' && S.route.id === d.id) reloadDetailSoon();
  });
  es.addEventListener('notification', e => { const d = JSON.parse(e.data); toast(`${d.title}: ${d.text}`, d.priority === 'Urgent' || d.priority === 'High' ? 'error' : 'info', 10000); });
  es.addEventListener('reload', () => { toast(t('Daten wurden übernommen – lade neu …')); setTimeout(() => location.reload(), 1500); });
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
  $('#statusline').textContent = t('{0}/{1} online · {2} · {3} W · Stand {4}', s.totals.online, s.totals.count, hash(s.totals.hashrate), n(s.totals.power, 1), new Date(s.time).toLocaleTimeString(LOCALE));
  const v = S.route?.view;
  if (v === 'overview') renderOverview();
  else if (v === 'compare') updateCompare();
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
  if (view === 'plug' && parts[1]) {
    S.route = { view, id: parts[1] };
    return renderPlug();
  }
  S.route = { view };
  if (view === 'compare') return renderCompare();
  if (view === 'fans') return renderFans();
  if (view === 'tax' && isAdmin()) return renderTax();
  if (view === 'reports' && isAdmin()) return renderReports();
  if (view === 'settings' && isAdmin()) return renderSettings();
  S.route = { view: 'overview' };
  if (S.status) renderOverview(); else api('/status').then(s => { S.status = s; renderOverview(); });
}

// ---------- Übersicht ----------

function tile(label, value, sub, cls) {
  return h('div', { class: 'tile' }, h('div', { class: 'label' }, label), h('div', { class: `value ${cls || ''}` }, value), sub ? h('div', { class: 'sub' }, sub) : null);
}

function dotClass(d) { return d.online ? 'on' : d.maintenance ? 'maint' : 'off'; }

/** Pool-Symbol mit Quick-Link zur Nutzerseite des Pools (wie in AxeOS). Die Karte ist selbst ein Link, daher kein <a>. */
function poolLinkIcon(d) {
  const link = d.poolLink;
  if (!link) return null;
  const open = e => {
    e.preventDefault(); e.stopPropagation();
    window.open(link.url, '_blank', 'noopener,noreferrer');
  };
  return h('span', {
    class: 'pool-link', role: 'link', tabindex: '0', title: t('{0}: Pool-Statistik öffnen', link.name), 'aria-label': t('{0}: Pool-Statistik öffnen', link.name),
    onclick: open, onkeydown: e => { if (e.key === 'Enter' || e.key === ' ') open(e); },
  }, h('img', { src: 'pool.svg', alt: '', width: '18', height: '18' }));
}

function renderOverview() {
  const s = S.status;
  const tv = s.totals;
  const chart = h('canvas');
  const devs = s.devices.map(d => h('a', { class: 'card device', href: `#/device/${d.id}` },
    h('div', { class: 'head' }, h('span', { class: `dot ${dotClass(d)}` }), h('b', {}, d.name),
      d.benchmark?.running ? h('span', { class: 'pill' }, t('Benchmark')) : null,
      d.soak ? h('span', { class: 'pill' }, t('Dauertest')) : null,
      d.simulated ? h('span', { class: 'pill gray' }, t('Simulation')) : null,
      poolLinkIcon(d)),
    d.online
      ? h('div', { class: 'kv num' },
          h('div', {}, h('span', {}, t('Hashrate')), hash(d.hashrate)),
          h('div', {}, h('span', {}, t('ASIC / VR')), t('{0} / {1} °C', n(d.temp, 1), n(d.vrTemp, 0))),
          h('div', {}, h('span', {}, t('Leistung')), t('{0} W', n(d.power, 1))),
          d.wallPower != null ? h('div', {}, h('span', {}, t('Steckdose')), t('{0} W', n(d.wallPower, 1))) : null,
          h('div', {}, h('span', {}, t('Effizienz')), d.efficiency ? t('{0} J/TH', n(d.efficiency, 2)) : '–'),
          h('div', {}, h('span', {}, t('Takt')), t('{0} MHz / {1} mV', d.frequency ?? '–', d.voltage ?? '–')),
          h('div', {}, h('span', {}, t('Laufzeit')), dur(d.uptimeSeconds)))
      : h('div', { class: d.maintenance ? 'warn' : 'danger' }, d.maintenance ? t('Neustart/Tuning …') : (d.error || 'offline')),
    d.automation ? h('div', { class: 'small muted', style: 'margin-top:6px' }, d.automation) : null,
    d.benchmark?.running ? h('div', { class: 'progress', style: 'margin-top:8px' }, h('div', { style: `width:${d.benchmark.overallProgress}%` })) : null,
    d.fan ? h('div', { class: `small ${d.fan.stalled ? 'danger' : 'muted'}`, style: 'margin-top:6px' },
      t('VR-Lüfter K{0}: {1} %{2}{3}', d.fan.channel, d.fan.percent, d.fan.rpm != null ? t(' · {0} U/min', d.fan.rpm) : '', d.fan.stalled ? t(' · steht!') : '')) : null,
    d.suggestion ? h('div', { class: 'small warn', style: 'margin-top:6px' }, t('Vorschlag offen: {0} MHz / {1} mV', d.suggestion.frequencyMhz, d.suggestion.coreVoltageMv)) : null));

  mount(h('div', { class: 'stack' },
    h('div', { class: 'tiles' },
      tile(t('Hashrate gesamt'), hash(tv.hashrate), t('{0}/{1} Miner online', tv.online, tv.count), 'ok'),
      tv.wallPower != null
        ? tile(t('Leistung (Steckdose)'), t('{0} W', n(tv.wallPower, 1)),
            t('AxeOS {0} W · Netzteil/Neben {1} W', n(tv.power, 1), n(tv.overhead, 1)) + (tv.costPerDay != null ? ' · ' + t('{0} {1} pro Tag', n(tv.costPerDay, 2), tv.currency) : ''))
        : tile(t('Leistung'), t('{0} W', n(tv.power, 1)), tv.costPerDay != null ? t('{0} {1} pro Tag', n(tv.costPerDay, 2), tv.currency) : ''),
      tile(t('Effizienz'), tv.efficiency ? t('{0} J/TH', n(tv.efficiency, 2)) : '–', tv.wallEfficiency ? t('Steckdose {0} J/TH', n(tv.wallEfficiency, 2)) : t('gesamt')),
      tile(t('Max. Temperatur'), tv.maxTemp != null ? t('{0} °C', n(tv.maxTemp, 1)) : '–', 'ASIC'),
      s.price ? tile(t('Strompreis'), t('{0} ct/kWh', n(s.price.ct, 2)), s.price.source) : null),
    h('div', { class: 'card' }, h('div', { class: 'chart-head' }, h('h3', {}, t('Hashrate gesamt')), h('span', { class: 'muted small' }, 'live')), h('div', { class: 'chart' }, chart)),
    s.devices.length ? h('div', { class: 'devices' }, devs) : h('div', { class: 'card muted' }, t('Noch keine Miner eingetragen.'), isAdmin() ? t(' Unter Einstellungen → Geräte hinzufügen.') : ''),
    s.plugs?.length ? plugOverviewCard(s.plugs) : null,
    isAdmin() && s.devices.length ? soakBatchCard(s.devices) : null,
    isAdmin() && S.update?.latest ? h('div', { class: 'banner row' },
      h('span', { style: 'flex:1' }, t('Server-Update {0} verfügbar (installiert: {1}).', S.update.latest, S.update.current)),
      h('a', { class: 'btn primary small', href: '#/settings' }, t('Zum Update'))) : null,
    !s.running ? h('div', { class: 'banner row' },
      h('span', { style: 'flex:1' }, t('Der Motor ist pausiert – der Server fragt keine Miner ab (z. B. weil die Desktop-App im Modus „Lokal“ läuft oder Daten übertragen werden).')),
      isAdmin() ? h('button', {
        class: 'btn primary small', onclick: async () => {
          if (!await confirmBox(t('Motor fortsetzen'), t('Server-Abfragen wieder starten?\n\nLäuft die Desktop-App noch im Modus „Lokal“, würden die Miner doppelt abgefragt. Stelle sie vorher auf „Server“ um oder beende sie.'), t('Fortsetzen'))) return;
          run(() => api('/admin/pause', { method: 'POST', body: { paused: false } }), t('Motor läuft wieder.'));
        },
      }, t('Fortsetzen …')) : null) : null));
  drawChart(chart, [{ points: s.history.map(p => [p[0], p[1]]), color: cssVar('--ok'), format: hash }], []);
}

/** Monats- und Jahresberichte: Zusammenfassung, druckbare Seite (PDF über „Drucken“), CSV, Push. */
async function renderReports() {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  mount(h('div', { class: 'stack' }, h('div', { class: 'card stack' }, h('h2', {}, t('Berichte')), body)));
  let list;
  try { list = await api('/reports'); } catch (e) { fill(body, h('p', { class: 'danger' }, e.message)); return; }
  if (!list.periods.length) { fill(body, h('p', { class: 'muted' }, t('Noch keine Messwerte für einen Bericht.'))); return; }
  const label = p => p.length === 4 ? t('Jahr {0}', p) : new Date(`${p}-01T00:00:00`).toLocaleDateString(LOCALE, { month: 'long', year: 'numeric' });
  const sel = h('select', {}, list.periods.map(p => h('option', { value: p }, label(p))));
  sel.value = S.reportPeriod && list.periods.includes(S.reportPeriod) ? S.reportPeriod : list.periods[Math.min(1, list.periods.length - 1)];
  const view = h('div', { class: 'stack' });
  const url = fmt => `/api/v1/reports/${sel.value}?format=${fmt}`;
  const load = async () => {
    S.reportPeriod = sel.value;
    fill(view, h('p', { class: 'muted' }, t('Lade …')));
    let r;
    try { r = await api(`/reports/${sel.value}`); } catch (e) { fill(view, h('p', { class: 'danger' }, e.message)); return; }
    const pct = v => v == null ? '–' : t('{0} %', n(v * 100, 1));
    fill(view,
      r.partial ? h('p', { class: 'muted small' }, t('Zeitraum läuft noch – Werte bis jetzt.')) : null,
      r.dataFrom ? h('p', { class: 'warn small' }, t('Messwerte liegen erst ab {0} vor – der Zeitraum davor fehlt im Bericht.', new Date(r.dataFrom).toLocaleString(LOCALE))) : null,
      h('div', { class: 'tiles' },
        tile(t('Ø Hashrate gesamt'), r.totalAvgHashGh != null ? hash(r.totalAvgHashGh) : '–', ''),
        tile(t('Energie'), t('{0} kWh', n(r.energy.kwh, 2)), ''),
        tile(t('Stromkosten'), `${n(r.energy.cost, 2)} ${r.currency}`, r.energy.avgCt != null ? t('Ø {0} ct/kWh', n(r.energy.avgCt, 1)) : ''),
        r.income.length ? tile(t('Zuflüsse'), `${n(r.incomeEur, 2)} €`, r.income.map(i => `${i.count}× ${i.coin}`).join(', ')) : null),
      h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, [t('Miner'), t('Verfügbarkeit'), t('Ø Hashrate'), t('Ø Temperatur'), t('Ø Leistung'), 'J/TH', 'kWh', t('Tuning')].map(x => h('th', {}, x)))),
        h('tbody', {}, r.miners.map(m => h('tr', {},
          h('td', {}, m.name), h('td', {}, pct(m.availability)), h('td', {}, m.avgHashGh != null ? hash(m.avgHashGh) : '–'),
          h('td', {}, m.avgTemp != null ? t('{0} °C', n(m.avgTemp, 1)) : '–'), h('td', {}, m.avgPowerW != null ? t('{0} W', n(m.avgPowerW, 1)) : '–'),
          h('td', {}, m.jth != null ? n(m.jth, 2) : '–'), h('td', {}, n(m.kwh, 2)), h('td', {}, m.tuningChanges)))))),
      r.plugs.length ? h('p', { class: 'small' }, t('Smart Plugs: {0}', r.plugs.map(p => `${p.name} ${n(p.kwh, 2)} kWh`).join(' · '))) : null);
  };
  sel.addEventListener('change', load);
  fill(body,
    h('div', { class: 'row', style: 'flex-wrap:wrap' },
      h('label', {}, t('Zeitraum')), sel,
      h('button', { class: 'btn', onclick: () => window.open(url('html'), '_blank', 'noopener') }, t('Ansehen / Drucken')),
      h('a', { class: 'btn', href: '#', onclick: e => { e.preventDefault(); location.href = url('csv'); } }, 'CSV'),
      h('button', {
        class: 'btn', onclick: async () => {
          const r = await run(() => api(`/reports/${sel.value}/send`, { method: 'POST', body: {} }));
          if (r) toast(r.ok ? t('Bericht per Push gesendet.') : r.error, r.ok ? 'ok' : 'error');
        },
      }, t('Per Push senden'))),
    h('p', { class: 'muted small' }, t('Druckbare Seite: im Browser „Drucken“ → „Als PDF speichern“. Abgeschlossene Monate werden gespeichert und bleiben auch erhalten, wenn ältere Minutenwerte bereinigt werden.')),
    view);
  load();
}

/** Smart Plugs in der Übersicht: Leistung an der Steckdose, bei Miner-Plugs die Differenz zu AxeOS. */
function plugOverviewCard(plugs) {
  const roleText = { miners: t('Miner'), other: t('Nebenverbraucher'), total: t('Gesamtmessung') };
  return h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h3', {}, t('Smart Plugs')), h('span', { class: 'spacer' }),
      isAdmin() ? h('a', { class: 'btn small', href: '#/settings' }, t('Einstellungen')) : null),
    h('div', { class: 'kv num' }, plugs.map(p => h('a', { href: `#/plug/${p.id}`, title: t('Verlauf anzeigen'), style: 'display:block;color:inherit;text-decoration:none' },
      h('span', {}, `${p.name} · ${roleText[p.role] ?? p.role}`),
      p.online
        ? t('{0} W', n(p.powerW, 1)) + (p.overheadW != null ? t(' (AxeOS {0} W, {1} W mehr)', n(p.minerPowerW, 1), n(p.overheadW, 1)) : '')
        : h('span', { class: 'danger' }, p.error || 'offline')))));
}

/** Verlauf eines Smart Plugs: Leistung an der Steckdose und zum Vergleich AxeOS (zugeordnete Miner bzw. alle). */
function renderPlug() {
  const id = S.route.id;
  const p = S.status?.plugs?.find(x => x.id === id);
  const range = S.plugRange || '24h';
  const chart = h('canvas');
  const legend = h('span', { class: 'muted small' });
  const ranges = ['1h', '24h', '7d', '30d'];
  const rangeBar = h('span', { class: 'range' }, ranges.map(r => h('a', {
    href: '#', class: r === range ? 'active' : null,
    onclick: e => { e.preventDefault(); S.plugRange = r; renderPlug(); },
  }, r === '1h' ? t('1 h') : r === '24h' ? t('24 h') : r === '7d' ? t('7 Tage') : t('30 Tage'))));
  const summary = h('p', { class: 'muted' });
  mount(h('div', { class: 'stack' },
    h('div', { class: 'row' }, h('a', { class: 'btn small', href: '#/' }, t('← Übersicht')), h('h2', { style: 'margin:0' }, p?.name ?? t('Smart Plug'))),
    summary,
    h('div', { class: 'card' }, h('div', { class: 'chart-head' }, h('h3', {}, t('Leistung (W)')), rangeBar), h('div', { class: 'chart' }, chart), legend)));
  api(`/plugs/${id}/history?range=${range}`).then(hist => {
    if (S.route?.view !== 'plug' || S.route.id !== id) return;
    const avg = pts => pts?.length ? pts.reduce((a, x) => a + x[1], 0) / pts.length : null;
    const pa = avg(hist.plug), aa = avg(hist.axeos);
    fill(summary, pa == null ? t('Für diesen Zeitraum liegen noch keine Messwerte vor.')
      : t('Ø Steckdose {0} W', n(pa, 1)) + (aa != null ? t(' · Ø AxeOS {0} W · Differenz {1} W ({2} %)', n(aa, 1), n(pa - aa, 1), n(aa > 0 ? (pa - aa) / aa * 100 : 0, 0)) : ''));
    const series = [{ points: hist.plug, color: cssVar('--accent'), format: v => t('{0} W', n(v, 1)) }];
    if (hist.axeos?.length) series.push({ points: hist.axeos, color: cssVar('--muted'), format: v => t('{0} W', n(v, 1)) });
    fill(legend, t('Orange: Steckdose'), hist.axeos?.length ? t(' · Grau: AxeOS der Miner dahinter') : '');
    drawChart(chart, series, []);
  }).catch(e => fill(summary, h('span', { class: 'danger' }, e.message)));
}

/** Dauertest für mehrere Miner: Auswahl, eine Dauer, eine Bestätigung; laufende Tests mit Restzeit. */
function soakBatchCard(devices) {
  const running = devices.filter(d => d.soak);
  const left = u => { const m = Math.max(0, (new Date(u) - Date.now()) / 60000); return m >= 120 ? t('{0} h', n(m / 60, 0)) : t('{0} min', n(m, 0)); };
  const start = async () => {
    const p = await run(() => api('/soak/prepare', { method: 'POST', body: {} }));
    if (!p) return;
    const sel = new Set(p.miners.filter(m => m.eligible).map(m => m.id));
    if (!sel.size) { toast(t('Kein Miner ist gerade bereit (offline, Benchmark oder Dauertest läuft).'), 'error'); return; }
    const hours = h('select', {}, [6, 12, 24, 48, 72].map(x => h('option', { value: x, selected: x === 24 }, t('{0} h', x))));
    const body = h('div', { class: 'stack' },
      h('p', {}, t('Beobachtet wird jeweils die aktuelle Einstellung. Am Miner wird nichts geändert; Zeitplan/Strompreis-Regeln pausieren so lange. Schlägt ein Test fehl, gibt es einen Vorschlag (nur nach Bestätigung).')),
      h('div', { class: 'row' }, h('label', {}, t('Dauer')), hours),
      h('div', { class: 'stack' }, p.miners.map(m => h('label', { class: `row ${m.eligible ? '' : 'muted'}` },
        h('input', { type: 'checkbox', checked: sel.has(m.id), disabled: !m.eligible, onchange: e => { e.target.checked ? sel.add(m.id) : sel.delete(m.id); } }),
        h('span', {}, `${m.name}: ${m.eligible ? t('{0} MHz / {1} mV', m.frequencyMhz, m.coreVoltageMv) : m.reason}`)))));
    if (!await confirmBox(t('Dauertest für mehrere Miner'), body, t('Starten'))) return;
    if (!sel.size) { toast(t('Kein Miner ausgewählt.'), 'error'); return; }
    const r = await run(() => api('/soak/start', { method: 'POST', body: { hours: Number(hours.value), ids: [...sel] } }));
    if (!r) return;
    const skipped = r.results.filter(x => !x.started);
    toast(t('Dauertest gestartet für {0} Miner.', r.started) + (skipped.length ? t(' Übersprungen: {0}', skipped.map(x => `${x.name} (${x.message})`).join(', ')) : ''), skipped.length ? 'info' : 'ok', 10000);
  };
  const stopAll = async () => {
    if (!await confirmBox(t('Alle Dauertests abbrechen'), t('{0} laufende(n) Dauertest(s) abbrechen? Die Einstellungen der Miner bleiben, wie sie sind.', running.length), t('Abbrechen'), true)) return;
    run(() => api('/soak/stop-all', { method: 'POST', body: {} }), t('Dauertests abgebrochen.'));
  };
  return h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h3', {}, t('Dauertest')), h('span', { class: 'spacer' }),
      running.length ? h('button', { class: 'btn small danger', onclick: stopAll }, t('Alle abbrechen')) : null,
      h('button', { class: 'btn small', onclick: start }, t('Dauertest für mehrere Miner …'))),
    running.length
      ? h('div', { class: 'stack small' }, running.map(d => h('div', {}, h('b', {}, d.name), t(' · {0} · noch {1}', d.soak.status, left(d.soak.until)))))
      : h('p', { class: 'muted small' }, t('Kein Dauertest aktiv. Prüft die aktuelle Einstellung mehrerer Miner über Stunden, ohne etwas zu ändern.')));
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
    if (all.length < 2) { c.fillText(t('noch keine Daten'), 8, hgt / 2); return; }
    const x0 = Math.min(...all.map(p => p[0])), x1 = Math.max(...all.map(p => p[0]));
    const pad = { l: 6, r: 64, t: 8, b: 16 };
    const X = tv => pad.l + (tv - x0) / Math.max(1, x1 - x0) * (w - pad.l - pad.r);
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
    c.fillText(span < 2 ? t('{0} min', Math.round(span * 60)) : span < 48 ? t('{0} h', Math.round(span)) : t('{0} Tage', Math.round(span / 24)), pad.l, hgt - 3);
  });
}

// ---------- Vergleich ----------

/** Vergleichsseite: Tabelle wird bei neuen Messwerten nur ausgetauscht (kein Neuaufbau – die Scrollposition bleibt). */
function renderCompare() {
  const s = S.status;
  if (!s) { api('/status').then(x => { S.status = x; renderCompare(); }); return; }
  S.compareTable = h('div', { class: 'table-wrap' });
  mount(h('div', { class: 'stack' },
    h('div', { class: 'card' }, h('h2', {}, t('Vergleich')), S.compareTable),
    isAdmin() ? advisorCard() : null));
  updateCompare();
}

function updateCompare() {
  const s = S.status;
  if (!s || !S.compareTable || !document.body.contains(S.compareTable)) return;
  const rows = [
    [t('Status'), d => d.online ? 'online' : d.error || 'offline'],
    [t('Modell / Profil'), d => `${d.model || '–'} / ${d.profile}`],
    [t('Hashrate'), d => hash(d.hashrate)],
    [t('Soll-Hashrate'), d => hash(d.expectedHashrate)],
    [t('ASIC-Temperatur'), d => d.temp != null ? t('{0} °C', n(d.temp, 1)) : '–'],
    [t('VR-Temperatur'), d => d.vrTemp != null ? t('{0} °C', n(d.vrTemp, 0)) : '–'],
    [t('Leistung'), d => d.power != null ? t('{0} W', n(d.power, 1)) : '–'],
    [t('Effizienz'), d => d.efficiency ? t('{0} J/TH', n(d.efficiency, 2)) : '–'],
    [t('Frequenz / Spannung'), d => t('{0} MHz / {1} mV', d.frequency ?? '–', d.voltage ?? '–')],
    [t('Fehlerrate'), d => d.errorPercent != null ? `${n(d.errorPercent, 2)} %` : '–'],
    [t('Lüfter'), d => d.fanRpm != null ? t('{0} rpm ({1} %)', d.fanRpm, d.fanPercent ?? '–') : '–'],
    [t('Shares'), d => d.sharesAccepted != null ? t('{0} / {1} abgelehnt', d.sharesAccepted, d.sharesRejected) : '–'],
    [t('Best Diff'), d => d.bestDiff || '–'],
    [t('Laufzeit'), d => dur(d.uptimeSeconds)],
    [t('Firmware'), d => d.firmwareText || '–'],
    [t('Pool'), d => d.pool || '–'],
    [t('Automatik'), d => d.automation || t('keine Automatik')],
  ];
  fill(S.compareTable, h('table', {},
    h('thead', {}, h('tr', {}, h('th', {}), s.devices.map(d => h('th', {}, h('a', { href: `#/device/${d.id}` }, d.name))))),
    h('tbody', {}, rows.map(([label, f]) => h('tr', {}, h('th', {}, label), s.devices.map(d => h('td', { class: 'num' }, f(d))))))));
}

/** Effizienz-Ratgeber: beste geprüfte Einstellung je Ziel, Vergleich mit dem aktuellen Betrieb. Ändert nie selbst etwas. */
function advisorCard() {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  const goals = [['Balanced', t('Ausgewogen')], ['Efficiency', t('Effizienz (J/TH)')], ['Hashrate', t('Hashrate')]];
  const goalSel = h('select', { style: 'width:auto', onchange: () => load() }, goals.map(([v, tv]) => h('option', { value: v }, tv)));
  try { goalSel.value = localStorageGet('advisorGoal') || 'Balanced'; } catch { /* egal */ }
  const signed = (v, digits, unit) => `${v > 0 ? '+' : v < 0 ? '−' : '±'}${n(Math.abs(v), digits)} ${unit}`;
  const apply = async (m, c, soak) => {
    const preview = await run(() => api(`/devices/${m.id}/change/preview`, { method: 'POST', body: { frequency: c.frequencyMhz, voltage: c.coreVoltageMv } }));
    if (!preview) return;
    const text = preview.confirmText + (soak ? t('\n\nDanach startet automatisch ein Dauertest (24 h), sobald der Miner wieder läuft.') : '');
    if (!await confirmBox(soak ? t('Anwenden und Dauertest') : t('Einstellung anwenden'), text, t('Anwenden'))) return;
    const body = { frequency: c.frequencyMhz, voltage: c.coreVoltageMv };
    if (soak) body.soakHours = 24;
    if (await run(() => api(`/devices/${m.id}/change`, { method: 'POST', body }), soak ? t('Angewendet – Dauertest folgt.') : t('Einstellung angewendet.'))) setTimeout(load, 3000);
  };
  const load = async () => {
    localStorageSet('advisorGoal', goalSel.value);
    const d = await api(`/advisor?goal=${goalSel.value}`).catch(e => { fill(body, h('p', { class: 'danger' }, e.message)); return null; });
    if (!d) return;
    fill(body, 
      h('p', { class: 'muted small' }, t('Grundlage: stabile Benchmark-Ergebnisse innerhalb der Profilgrenzen und bestandene Dauertests; im Dauertest durchgefallene Einstellungen werden nicht vorgeschlagen. Kosten mit {0} ct/kWh, 30 Tage. Angewendet wird nur nach Bestätigung.', n(d.ctPerKwh, 1))),
      d.miners.length ? d.miners.map(m => {
        const c = m.recommended;
        return h('div', { class: 'card stack', style: 'margin:0' },
          h('div', { class: 'titlebar' }, h('h3', {}, m.name), h('span', { class: 'spacer' }),
            m.frequencyMhz ? h('span', { class: 'pill gray' }, t('jetzt {0} MHz / {1} mV', m.frequencyMhz, m.coreVoltageMv)) : null),
          m.hashrateGh ? h('p', { class: 'small muted' }, t('{0}: {1} · {2} W · {3}', m.basis, hash(m.hashrateGh), n(m.powerW, 1), m.jth ? n(m.jth, 2) + ' J/TH' : '–')) : null,
          c ? h('div', { class: 'stack' },
                h('p', {}, h('b', {}, t('Vorschlag: {0} MHz / {1} mV', c.frequencyMhz, c.coreVoltageMv)), t(' · {0} · {1} W · {2} J/TH', hash(c.hashrateGh), n(c.powerW, 1), n(c.jth, 2))),
                h('p', { class: 'small' },
                  `${signed(c.deltaGh, 0, 'GH/s')} · ${signed(c.deltaW, 1, 'W')} · `,
                  h('b', { class: c.monthlyCostDelta < 0 ? 'ok' : '' }, t('{0} pro Monat', signed(c.monthlyCostDelta, 2, d.currency))),
                  ` · ${c.confidence}`),
                h('div', { class: 'row' },
                  h('button', { class: 'btn small', onclick: () => apply(m, c, false) }, t('Anwenden …')),
                  c.soakPassed ? null : h('button', { class: 'btn small primary', onclick: () => apply(m, c, true) }, t('Anwenden + Dauertest 24 h …'))))
            : null,
          h('p', { class: 'small muted' }, m.note),
          m.candidates.length ? h('details', {}, h('summary', { class: 'small' }, t('Alle Ziele')),
            h('div', { class: 'table-wrap' }, h('table', {},
              h('thead', {}, h('tr', {}, [t('Ziel'), t('Einstellung'), t('Hashrate'), t('Leistung'), 'J/TH', t('pro Monat'), t('geprüft')].map(x => h('th', {}, x)))),
              h('tbody', {}, m.candidates.map(x => h('tr', {},
                h('td', {}, { Efficiency: t('Effizienz'), Balanced: t('Ausgewogen'), Hashrate: t('Hashrate') }[x.goal]),
                h('td', { class: 'num' }, `${x.frequencyMhz} / ${x.coreVoltageMv}`),
                h('td', { class: 'num' }, hash(x.hashrateGh)), h('td', { class: 'num' }, t('{0} W', n(x.powerW, 1))), h('td', { class: 'num' }, n(x.jth, 2)),
                h('td', { class: 'num' }, signed(x.monthlyCostDelta, 2, d.currency)), h('td', { class: 'small' }, x.confidence))))))) : null);
      }) : h('p', { class: 'muted' }, t('Keine Miner.')));
  };
  load();
  return h('div', { class: 'card stack' }, h('div', { class: 'titlebar' }, h('h2', {}, t('Empfehlungen')), h('span', { class: 'spacer' }), goalSel), body);
}

// ---------- Gerät ----------

const TABS = () => [
  ['live', t('Live')], ['benchmark', t('Benchmark'), true], ['results', t('Ergebnisse')], ['compare', t('Vorher/Nachher')],
  ['automation', t('Automatik'), true], ['backups', t('Sicherungen'), true], ['log', t('Protokolle'), true],
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
  const tab = TABS().some(tv => tv[0] === S.route.tab && (!tv[2] || isAdmin())) ? S.route.tab : 'live';
  S.route.tab = tab;
  mount(h('div', { id: 'device-page', class: 'stack' },
    h('div', { class: 'titlebar' },
      h('span', { id: 'dev-dot', class: `dot ${dotClass(d)}` }),
      h('h2', {}, d.name),
      h('span', { class: 'muted' }, [d.model, d.firmware, isAdmin() ? d.host : null].filter(Boolean).join(' · ')),
      h('span', { class: 'spacer' }),
      h('span', { class: 'pill' }, S.detail.profile.name)),
    d.suggestion && isAdmin() ? suggestionBanner(d) : null,
    h('div', { class: 'tabs' }, TABS().filter(tv => !tv[2] || isAdmin()).map(([k, label]) =>
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
  fill(tab, render());
}

function suggestionBanner(d) {
  const s = d.suggestion;
  return h('div', { class: 'banner danger row' },
    h('span', { style: 'flex:1' }, t('Dauertest fehlgeschlagen – Vorschlag: {0} MHz / {1} mV (nächstniedrigere stabile Einstellung).', s.frequencyMhz, s.coreVoltageMv)),
    h('button', { class: 'btn primary small', onclick: () => applyChange(s.frequencyMhz, s.coreVoltageMv) }, t('Prüfen & anwenden')),
    h('button', { class: 'btn ghost small', onclick: () => run(() => api(`/devices/${d.id}/suggestion/dismiss`, { method: 'POST', body: {} })).then(reloadDetailSoon) }, t('Verwerfen')));
}

/** Frequenz/Spannung: Server liefert den Bestätigungstext (alt → neu, Grenzen), erst danach ausführen. */
async function applyChange(frequency, voltage) {
  const id = S.route.id;
  const preview = await run(() => api(`/devices/${id}/change/preview`, { method: 'POST', body: { frequency, voltage } }));
  if (!preview) return;
  if (!await confirmBox(t('Einstellung anwenden'), preview.confirmText, t('Anwenden'))) return;
  if (await run(() => api(`/devices/${id}/change`, { method: 'POST', body: { frequency, voltage } }), t('Einstellung angewendet.'))) reloadDetailSoon();
}

function tabLive() {
  const d = summaryOf(S.route.id);
  const p = S.detail.profile;
  const chartHash = h('canvas'), chartTemp = h('canvas'), chartPower = h('canvas');
  const markers = h('div', { class: 'small muted' });
  const ranges = ['1h', '24h', '7d', '30d'];
  const rangeBar = h('span', { class: 'range' }, ranges.map(r => h('a', { href: '#', class: r === S.chartRange ? 'active' : null, onclick: e => { e.preventDefault(); S.chartRange = r; refreshDeviceTab(); } }, r === '1h' ? t('1 h') : r === '24h' ? t('24 h') : r === '7d' ? t('7 Tage') : t('30 Tage'))));

  api(`/devices/${S.route.id}/history?range=${S.chartRange}`).then(hist => {
    const tm = hist.tuning.map(tv => tv.time);
    drawChart(chartHash, [{ points: hist.samples.map(s => [s[0], s[1]]), color: cssVar('--ok'), format: hash }], tm);
    drawChart(chartTemp, [{ points: hist.samples.map(s => [s[0], s[2]]), color: cssVar('--warn'), format: v => `${n(v, 1)} °C` }], tm);
    drawChart(chartPower, [{ points: hist.samples.map(s => [s[0], s[3]]), color: cssVar('--info'), format: v => `${n(v, 1)} W` }], tm);
    fill(markers, ...hist.tuning.slice(-8).reverse().map(tv => h('div', {}, `┊ ${time(tv.time)} ${tv.source}: ${tv.change}${tv.note ? ' – ' + tv.note : ''}`)));
  }).catch(e => toast(e.message, 'error'));

  const freq = h('input', { type: 'number', value: d.frequency ?? p.defaultFrequencyMhz, min: p.minFrequencyMhz, max: p.maxFrequencyMhz, step: 5 });
  const volt = h('input', { type: 'number', value: d.voltage ?? p.defaultVoltageMv, min: p.minVoltageMv, max: p.maxVoltageMv, step: 5 });
  const profileSel = isAdmin() && S.detail.profiles ? h('select', {
    onchange: async e => {
      if (!await confirmBox(t('Profil wählen'), t('Profil „{0}“ für dieses Gerät dauerhaft verwenden? Grenzen für Frequenz und Spannung richten sich danach.', e.target.selectedOptions[0].textContent))) { e.target.value = p.id; return; }
      if (await run(() => api(`/devices/${S.route.id}/profile`, { method: 'POST', body: { id: e.target.value } }), t('Profil gespeichert.'))) loadDevice(true);
    },
  }, S.detail.profiles.map(x => h('option', { value: x.id, selected: x.id === p.id }, x.name))) : null;

  return h('div', { class: 'stack' },
    h('div', { class: 'tiles', id: 'live-tiles' }, liveTiles(d)),
    h('div', { class: 'card' }, h('div', { class: 'chart-head' }, h('h3', {}, t('Hashrate')), rangeBar), h('div', { class: 'chart' }, chartHash), markers),
    h('div', { class: 'grid', style: 'grid-template-columns:repeat(auto-fit,minmax(300px,1fr))' },
      h('div', { class: 'card' }, h('h3', {}, t('ASIC-Temperatur')), h('div', { class: 'chart' }, chartTemp)),
      h('div', { class: 'card' }, h('h3', {}, t('Leistung')), h('div', { class: 'chart' }, chartPower))),
    isAdmin() ? h('div', { class: 'card stack' },
      h('h3', {}, t('Manuell einstellen')),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, t('Frequenz (MHz, {0}–{1})', p.minFrequencyMhz, p.maxFrequencyMhz)), freq),
        h('div', {}, h('label', {}, t('Kernspannung (mV, {0}–{1})', p.minVoltageMv, p.maxVoltageMv)), volt),
        h('button', { class: 'btn primary', onclick: () => applyChange(+freq.value, +volt.value), disabled: d.benchmark?.running }, t('Prüfen & anwenden')),
        h('button', {
          class: 'btn', onclick: async () => {
            if (!await confirmBox(t('Neustart'), t('{0} neu starten? Watchdog und Offline-Meldung pausieren dabei.', d.name), t('Neu starten'))) return;
            run(() => api(`/devices/${S.route.id}/restart`, { method: 'POST', body: {} }), t('Neustart ausgelöst.'));
          },
        }, t('Miner neu starten'))),
      profileSel ? h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Profil')), profileSel)) : null,
      S.detail.profile.notes ? h('p', { class: 'muted small' }, S.detail.profile.notes) : null) : null);
}

function liveTiles(d) {
  return [
    tile(t('Hashrate'), hash(d.hashrate), d.expectedHashrate ? t('Soll {0}', hash(d.expectedHashrate)) : '', d.online ? 'ok' : 'danger'),
    tile(t('ASIC / VR'), d.temp != null ? t('{0} °C', n(d.temp, 1)) : '–', d.vrTemp != null ? t('VR {0} °C', n(d.vrTemp, 0)) : ''),
    tile(t('Leistung'), d.power != null ? t('{0} W', n(d.power, 1)) : '–', d.efficiency ? t('{0} J/TH', n(d.efficiency, 2)) : ''),
    tile(t('Frequenz'), d.frequency != null ? t('{0} MHz', d.frequency) : '–', d.voltage != null ? t('{0} mV', d.voltage) : ''),
    tile(t('Lüfter'), d.fanRpm != null ? t('{0} rpm', d.fanRpm) : '–', d.fanPercent != null ? `${d.fanPercent} %` : ''),
    tile(t('Shares'), d.sharesAccepted ?? '–', d.errorPercent != null ? t('Fehlerrate {0} %', n(d.errorPercent, 2)) : ''),
    tile(t('Laufzeit'), dur(d.uptimeSeconds), d.bestDiff ? t('Best {0}', d.bestDiff) : ''),
    tile(t('Status'), d.online ? 'online' : d.maintenance ? t('Neustart …') : 'offline', d.pool || d.error || '', d.online ? 'ok' : 'warn'),
    d.fan ? tile(t('VR-Lüfter K{0}', d.fan.channel), `${d.fan.percent} %`, d.fan.stalled ? t('Lüfter steht!') : d.fan.rpm != null ? t('{0} U/min', d.fan.rpm) : d.fan.reason, d.fan.stalled ? 'danger' : '') : null,
  ].filter(Boolean);
}

function updateDeviceLive() {
  const d = S.status?.devices.find(x => x.id === S.route.id);
  if (!d) return;
  const dot = $('#dev-dot');
  if (dot) dot.className = `dot ${dotClass(d)}`;
  const tiles = $('#live-tiles');
  if (tiles) fill(tiles, ...liveTiles(d));
  if (d.benchmark) updateBenchmark(d.benchmark);
}

// ---------- Benchmark ----------

const BENCH_FIELDS = () => [
  ['startFrequencyMhz', t('Start-Frequenz (MHz)')], ['maxFrequencyMhz', t('Max. Frequenz (MHz)')], ['frequencyStepMhz', t('Schritt (MHz)')],
  ['startVoltageMv', t('Start-Spannung (mV)')], ['minVoltageMv', t('Min. Spannung (mV)')], ['maxVoltageMv', t('Max. Spannung (mV)')], ['voltageStepMv', t('Schritt (mV)')],
  ['warmupSeconds', t('Aufwärmen (s)')], ['measureSeconds', t('Messdauer (s)')], ['sampleIntervalSeconds', t('Messintervall (s)')],
  ['maxChipTempC', t('Max. Chip (°C)')], ['maxVrTempC', t('Max. VR (°C)')], ['maxPowerW', t('Max. Leistung (W)')],
  ['stabilityThreshold', t('Stabil ab (Anteil Soll)')], ['maxErrorPercent', t('Max. Fehlerrate (%)')],
];

function tabBenchmark() {
  const d = summaryOf(S.route.id);
  const base = S.detail.benchmarkDefaults;
  const inputs = {};
  const sel = (key, opts) => { const s = h('select', {}, opts.map(([v, l]) => h('option', { value: v, selected: base[key] === v }, l))); inputs[key] = s; return s; };
  const form = h('div', { class: 'form' },
    BENCH_FIELDS().map(([k, label]) => { const i = h('input', { type: 'number', step: 'any', value: base[k] }); inputs[k] = i; return h('div', {}, h('label', {}, label), i); }),
    h('div', {}, h('label', {}, t('Am Ende setzen')), sel('restoreMode', [['Best', t('Beste Einstellung')], ['Original', t('Ursprüngliche Einstellung')]])),
    h('div', {}, h('label', {}, t('Beste nach')), sel('restoreRanking', [['Balanced', t('Ausgewogen')], ['MaxHashrate', t('Hashrate')], ['Efficiency', t('Effizienz')]])),
    h('div', {}, h('label', {}, t('Lüfter')), sel('fanMode', [['KeepCurrent', t('unverändert')], ['Full', t('100 % während des Tests')]])));
  const settings = () => {
    const s = { ...base };
    for (const [k, el] of Object.entries(inputs)) s[k] = el.tagName === 'SELECT' ? el.value : Number(el.value);
    return s;
  };
  const start = async resume => {
    const body = { settings: settings(), resume };
    const plan = await run(() => api(`/devices/${S.route.id}/benchmark/prepare`, { method: 'POST', body }));
    if (!plan) return;
    if (!await confirmBox(resume ? t('Benchmark fortsetzen') : t('Benchmark starten'), plan.confirmText, t('Starten'))) return;
    if (await run(() => api(`/devices/${S.route.id}/benchmark/start`, { method: 'POST', body }), t('Benchmark läuft auf dem Server.'))) reloadDetailSoon();
  };
  const session = S.detail.session;
  const running = d.benchmark?.running;
  return h('div', { class: 'stack' },
    h('div', { class: 'card stack', id: 'bench-progress' }, benchProgress(d.benchmark)),
    h('div', { class: 'card stack' },
      h('h3', {}, t('Suchbereich')),
      h('p', { class: 'muted small' }, t('Profil {0}: {1}–{2} MHz, {3}–{4} mV · {5}', S.detail.profile.name, S.detail.profile.minFrequencyMhz, S.detail.profile.maxFrequencyMhz, S.detail.profile.minVoltageMv, S.detail.profile.maxVoltageMv, S.detail.estimatedDuration)),
      form,
      h('div', { class: 'row' },
        h('button', { class: 'btn primary', disabled: running || !d.online, onclick: () => start(false) }, t('Benchmark starten')),
        session && !session.isFinished && session.results.length ? h('button', { class: 'btn', disabled: running, onclick: () => start(true) }, t('Fortsetzen')) : null,
        h('span', { class: 'muted small' }, t('Der Lauf geht auf dem Server weiter, auch wenn der Browser geschlossen wird.')))));
}

function benchProgress(b) {
  if (!b) return [h('h3', {}, t('Fortschritt')), h('p', { class: 'muted' }, t('In dieser Sitzung wurde noch kein Benchmark gestartet.'))];
  return [
    h('div', { class: 'titlebar' }, h('h3', {}, t('Benchmark: {0}', b.phase)), h('span', { class: 'spacer' }),
      b.running && isAdmin() ? h('button', { class: 'btn small', onclick: () => run(() => api(`/devices/${S.route.id}/benchmark/pause`, { method: 'POST', body: {} })) }, b.paused ? t('Fortsetzen') : t('Pause')) : null,
      b.running && isAdmin() ? h('button', {
        class: 'btn danger small', onclick: async () => {
          if (await confirmBox(t('Benchmark stoppen'), t('Benchmark abbrechen? Die Einstellungen werden wiederhergestellt.'), t('Stoppen'), true))
            run(() => api(`/devices/${S.route.id}/benchmark/stop`, { method: 'POST', body: {} }));
        },
      }, t('Stoppen')) : null),
    h('div', {}, b.step || ''),
    h('div', { class: 'progress' }, h('div', { style: `width:${b.overallProgress}%` })),
    h('div', { class: 'small muted' }, t('Gesamt {0} % · Phase {1} % · {2}', n(b.overallProgress, 0), n(b.phaseProgress, 0), b.eta || '')),
  ];
}

function updateBenchmark(b) {
  const box = $('#bench-progress');
  if (box) fill(box, ...benchProgress(b));
  if (!b.running && S.route?.view === 'device') reloadDetailSoon();
}

// ---------- Ergebnisse ----------

function tabResults() {
  const s = S.detail.session;
  if (!s || !s.results.length) return h('div', { class: 'card muted' }, t('Noch keine Benchmark-Ergebnisse.'));
  const best = s.ranking.balanced;
  const bestCard = (label, r) => r ? h('div', { class: 'tile' }, h('div', { class: 'label' }, label),
    h('div', { class: 'value' }, t('{0} MHz / {1} mV', r.frequencyMhz, r.coreVoltageMv)),
    h('div', { class: 'sub' }, `${hash(r.avgHashRateGh)} · ${r.efficiencyJth ? n(r.efficiencyJth, 2) + t(' J/TH') : '–'}`),
    isAdmin() ? h('button', { class: 'btn small', style: 'margin-top:6px', onclick: () => applyChange(r.frequencyMhz, r.coreVoltageMv) }, t('Anwenden …')) : null) : null;
  return h('div', { class: 'stack' },
    h('div', { class: 'tiles' }, bestCard(t('Beste Hashrate'), s.ranking.hashrate), bestCard(t('Beste Effizienz'), s.ranking.efficiency), bestCard(t('Ausgewogen'), s.ranking.balanced)),
    h('div', { class: 'card' },
      h('div', { class: 'titlebar' }, h('h3', {}, t('Lauf vom {0}', time(s.startedAt))), h('span', { class: 'spacer' }), h('span', { class: 'muted small' }, s.finishReason || (s.isFinished ? t('abgeschlossen') : t('nicht abgeschlossen')))),
      h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, ['MHz', 'mV', t('Ergebnis'), t('Hashrate'), t('Soll'), t('Leistung'), 'J/TH', t('Chip max'), t('VR max'), t('Fehler %'), ''].map(x => h('th', {}, x)))),
        h('tbody', {}, s.results.map(r => h('tr', { class: best && r.frequencyMhz === best.frequencyMhz && r.coreVoltageMv === best.coreVoltageMv ? 'best' : null },
          h('td', {}, r.frequencyMhz), h('td', {}, r.coreVoltageMv), h('td', { class: r.isStable ? 'ok' : 'danger' }, r.outcomeText),
          h('td', {}, hash(r.avgHashRateGh)), h('td', {}, hash(r.expectedHashRateGh)), h('td', {}, t('{0} W', n(r.avgPowerW, 1))),
          h('td', {}, r.efficiencyJth ? n(r.efficiencyJth, 2) : '–'), h('td', {}, r.maxChipTempC != null ? n(r.maxChipTempC, 1) : '–'),
          h('td', {}, r.maxVrTempC != null ? n(r.maxVrTempC, 0) : '–'), h('td', {}, r.avgErrorPercent != null ? n(r.avgErrorPercent, 2) : '–'),
          h('td', {}, isAdmin() && r.isStable ? h('button', { class: 'btn small', onclick: () => applyChange(r.frequencyMhz, r.coreVoltageMv) }, t('Anwenden …')) : null))))))));
}

// ---------- Vorher/Nachher ----------

function tabCompare() {
  const box = h('div', { class: 'card' }, h('h3', {}, t('Vorher/Nachher (je 60 min, erste 5 min nach der Änderung ausgelassen)')), h('p', { class: 'muted' }, t('Lade …')));
  api(`/devices/${S.route.id}/comparisons`).then(rows => {
    fill(box, box.firstChild, rows.length
      ? h('div', { class: 'table-wrap' }, h('table', {},
          h('thead', {}, h('tr', {}, [t('Zeit'), t('Quelle'), t('Änderung'), t('Vorher'), t('Nachher'), t('Differenz')].map(x => h('th', {}, x)))),
          h('tbody', {}, rows.map(r => h('tr', {}, h('td', {}, time(r.time)), h('td', {}, r.source), h('td', {}, r.change), h('td', {}, r.before), h('td', {}, r.after), h('td', {}, r.delta))))))
      : h('p', { class: 'muted' }, t('Noch keine protokollierten Änderungen in den letzten 30 Tagen.')));
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
  const presetOptions = () => [h('option', { value: '' }, '—'), ...presets.map(p => h('option', { value: p.name }, t('{0} ({1} MHz / {2} mV)', p.name, p.frequencyMhz, p.coreVoltageMv)))];
  const presetSelect = (obj, key) => { const s = h('select', { onchange: e => { obj[key] = e.target.value; } }, presetOptions()); s.value = obj[key] || ''; return s; };

  const renderPresets = () => fill(presetList, 
    ...presets.map((p, i) => h('div', { class: 'row' }, h('span', { style: 'flex:1' }, t('{0}: {1} MHz / {2} mV', p.name, p.frequencyMhz, p.coreVoltageMv)),
      h('button', { class: 'btn small ghost', onclick: () => { presets.splice(i, 1); renderPresets(); } }, t('Entfernen')))),
    presets.length ? null : h('p', { class: 'muted small' }, t('Noch keine Voreinstellungen.')));
  renderPresets();
  const pName = h('input', { placeholder: t('Name, z. B. Nacht') });
  const pFreq = h('input', { type: 'number', value: d.frequency ?? '' });
  const pVolt = h('input', { type: 'number', value: d.voltage ?? '' });

  const entries = h('div', { class: 'stack' });
  const daysInput = e => {
    const i = h('input', { value: daysText(e.days ?? 127) });
    i.addEventListener('change', () => {
      const m = parseDays(i.value);
      if (m == null) { i.classList.add('invalid'); return toast(t('Tage nicht erkannt – Beispiele: Mo-Fr, Sa,So, täglich'), 'error'); }
      i.classList.remove('invalid'); e.days = m; i.value = daysText(m);
    });
    return i;
  };
  const renderEntries = () => fill(entries, ...sched.entries.map((e, i) => h('div', { class: 'form' },
    h('div', {}, h('label', {}, t('Tage (z. B. Mo-Fr, Sa,So, täglich)')), daysInput(e)),
    h('div', {}, h('label', {}, t('von (Uhr)')), numInput(e, 'fromHour')),
    h('div', {}, h('label', {}, t('bis (Uhr)')), numInput(e, 'toHour')),
    h('div', {}, h('label', {}, t('Voreinstellung')), presetSelect(e, 'preset')),
    h('button', { class: 'btn small ghost', onclick: () => { sched.entries.splice(i, 1); renderEntries(); } }, t('Entfernen')))));
  renderEntries();

  const save = async () => {
    const r = await run(() => api(`/devices/${S.route.id}/automation`, { method: 'PUT', body: { presets, thermalGuard: guard, schedule: sched } }), t('Automatik gespeichert.'));
    if (r) { await loadDevice(false); if ((guard.enabled && !r.thermalGuardApproved) || (sched.enabled && !r.scheduleApproved)) toast(t('Geänderte Regeln brauchen eine neue Freigabe.'), 'info'); }
    return r;
  };
  const approve = async rule => {
    if (!await save()) return;
    const tv = await run(() => api(`/devices/${S.route.id}/automation/approval-text`, { method: 'POST', body: { rule } }));
    if (!tv || !await confirmBox(t('Regel freigeben'), tv.text, t('Freigeben'))) return;
    if (await run(() => api(`/devices/${S.route.id}/automation/approve`, { method: 'POST', body: { rule } }), t('Regel freigegeben.'))) loadDevice(false);
  };
  const soakHours = h('select', {}, [6, 12, 24, 48].map(x => h('option', { value: x, selected: x === 24 }, t('{0} h', x))));

  return h('div', { class: 'stack' },
    h('div', { class: 'card' }, h('b', {}, t('Status: ')), d.automation || t('keine Automatik')),
    h('div', { class: 'card stack' }, h('h3', {}, t('Voreinstellungen')), presetList,
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Name')), pName), h('div', {}, h('label', {}, t('MHz')), pFreq), h('div', {}, h('label', {}, t('mV')), pVolt),
        h('button', {
          class: 'btn', onclick: () => {
            if (!pName.value.trim()) return toast(t('Name fehlt.'), 'error');
            const i = presets.findIndex(p => p.name.toLowerCase() === pName.value.trim().toLowerCase());
            const p = { name: pName.value.trim(), frequencyMhz: Number(pFreq.value), coreVoltageMv: Number(pVolt.value) };
            if (i >= 0) presets[i] = p; else presets.push(p);
            pName.value = ''; renderPresets();
          },
        }, t('Hinzufügen')))),
    h('div', { class: 'card stack' },
      h('div', { class: 'titlebar' }, h('h3', {}, t('Temperaturschutz')), h('span', { class: `pill ${c.thermalGuardApproved ? '' : 'gray'}` }, c.thermalGuardApproved ? t('freigegeben') : t('nicht freigegeben'))),
      checkInput(guard, 'enabled', 'eingeschaltet'),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, t('Max. Chip (°C)')), numInput(guard, 'maxChipTempC', 0.5)), h('div', {}, h('label', {}, t('Max. VR (°C)')), numInput(guard, 'maxVrTempC', 0.5)),
        h('div', {}, h('label', {}, t('durchgehend (min)')), numInput(guard, 'minutes')), h('div', {}, h('label', {}, t('Absenken um (MHz)')), numInput(guard, 'stepMhz')),
        h('div', {}, h('label', {}, t('nie unter (MHz)')), numInput(guard, 'minFrequencyMhz')), h('div', {}, h('label', {}, t('Zurück nach (min kühl)')), numInput(guard, 'recoverMinutes'))),
      checkInput(guard, 'recover', t('schrittweise zurück, wenn wieder kühl')),
      h('div', { class: 'row' }, h('button', { class: 'btn', onclick: save }, t('Speichern')), h('button', { class: 'btn primary', onclick: () => approve('thermal') }, t('Speichern & freigeben …')))),
    h('div', { class: 'card stack' },
      h('div', { class: 'titlebar' }, h('h3', {}, t('Zeitplan / Strompreis')), h('span', { class: `pill ${c.scheduleApproved ? '' : 'gray'}` }, c.scheduleApproved ? t('freigegeben') : t('nicht freigegeben'))),
      checkInput(sched, 'enabled', 'eingeschaltet'),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Art')), (() => { const s = h('select', { onchange: e => { sched.mode = e.target.value; } }, h('option', { value: 'time' }, t('Zeitplan')), h('option', { value: 'price' }, t('Strompreis (Schwelle)'))); s.value = sched.mode; return s; })())),
      h('h3', {}, t('Zeitplan')), entries,
      h('div', { class: 'form' }, h('button', { class: 'btn small', onclick: () => { sched.entries.push({ days: 127, fromHour: 22, toHour: 6, preset: presets[0]?.name || '' }); renderEntries(); } }, t('Zeitfenster hinzufügen')),
        h('div', {}, h('label', {}, t('sonst')), presetSelect(sched, 'defaultPreset'))),
      h('h3', {}, t('Strompreis')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('günstig bis (ct/kWh)')), numInput(sched, 'thresholdCt', 0.1)),
        h('div', {}, h('label', {}, t('günstig →')), presetSelect(sched, 'cheapPreset')), h('div', {}, h('label', {}, t('teuer →')), presetSelect(sched, 'expensivePreset'))),
      h('div', { class: 'row' }, h('button', { class: 'btn', onclick: save }, t('Speichern')), h('button', { class: 'btn primary', onclick: () => approve('schedule') }, t('Speichern & freigeben …')))),
    h('div', { class: 'card stack' },
      h('h3', {}, t('Dauertest')),
      d.soak ? h('p', {}, t('{0} (bis {1})', d.soak.status, time(d.soak.until))) : h('p', { class: 'muted' }, t('Beobachtet die aktuelle Einstellung über Stunden, ohne am Miner etwas zu ändern.')),
      d.soak
        ? h('button', { class: 'btn danger', onclick: () => run(() => api(`/devices/${S.route.id}/soak/stop`, { method: 'POST', body: {} }), t('Dauertest abgebrochen.')).then(reloadDetailSoon) }, t('Dauertest abbrechen'))
        : h('div', { class: 'row' }, soakHours, h('button', {
            class: 'btn primary', onclick: async () => {
              const hours = Number(soakHours.value);
              const tv = await run(() => api(`/devices/${S.route.id}/soak/prepare`, { method: 'POST', body: { hours } }));
              if (!tv || !await confirmBox(t('Dauertest'), tv.text, t('Starten'))) return;
              run(() => api(`/devices/${S.route.id}/soak/start`, { method: 'POST', body: { hours } }), t('Dauertest gestartet.')).then(reloadDetailSoon);
            },
          }, t('Dauertest starten …')))));
}

// ---------- Sicherungen ----------

function tabBackups() {
  const list = h('div', {}, h('p', { class: 'muted' }, t('Lade …')));
  const load = () => api(`/devices/${S.route.id}/snapshots`).then(snaps => fill(list, snaps.length
    ? h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, [t('Zeit'), t('Anlass'), t('Firmware'), ''].map(x => h('th', {}, x)))),
        h('tbody', {}, snaps.map(s => h('tr', {}, h('td', {}, time(s.takenAt)), h('td', {}, s.reason), h('td', {}, s.firmwareVersion || '–'),
          h('td', {}, h('button', { class: 'btn small', onclick: () => restore(s) }, t('Wiederherstellen …'))))))))
    : h('p', { class: 'muted' }, t('Noch keine Sicherung.')))).catch(e => toast(e.message, 'error'));
  const restore = async snap => {
    const diff = await run(() => api(`/devices/${S.route.id}/snapshots/diff`, { method: 'POST', body: { file: snap.file } }));
    if (!diff) return;
    if (!diff.length) return toast(t('Keine Unterschiede zur aktuellen Einstellung.'), 'info');
    const boxes = diff.map(c => ({ c, box: h('input', { type: 'checkbox', checked: c.group !== 'Pool' }) }));
    const body = h('div', { class: 'stack' }, h('p', {}, t('Sicherung vom {0}. Nur ausgewählte Felder werden zurückgespielt; vorher wird der aktuelle Stand gesichert.', time(snap.takenAt))),
      h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['', t('Bereich'), t('Einstellung'), t('Aktuell'), t('Gesichert')].map(x => h('th', {}, x)))),
        h('tbody', {}, boxes.map(({ c, box }) => h('tr', {}, h('td', {}, box), h('td', {}, c.group), h('td', {}, c.label), h('td', {}, c.current), h('td', {}, c.saved)))))));
    if (!await confirmBox(t('Einstellungen wiederherstellen'), body, t('Wiederherstellen'))) return;
    const fields = boxes.filter(b => b.box.checked).map(b => b.c.field);
    if (await run(() => api(`/devices/${S.route.id}/snapshots/restore`, { method: 'POST', body: { file: snap.file, fields } }), t('Wiederhergestellt.'))) load();
  };
  load();
  return h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h3', {}, t('Einstellungen des Miners sichern')), h('span', { class: 'spacer' }),
      h('button', { class: 'btn primary', onclick: () => run(() => api(`/devices/${S.route.id}/snapshots`, { method: 'POST', body: {} }), t('Gesichert.')).then(load) }, t('Jetzt sichern'))),
    h('p', { class: 'muted small' }, t('Die Sicherung enthält auch Pool-Benutzer (Wallet-Adresse). Pool-Passwörter liefert AxeOS nicht aus.')),
    list);
}

// ---------- Protokolle ----------

function tabLog() {
  const appLog = h('div', { class: 'log', id: 'app-log' });
  const minerLog = h('div', { class: 'log', id: 'miner-log' });
  const state = h('span', { class: 'muted small' }, t('verbinde …'));
  const follow = h('input', { type: 'checkbox', checked: true });
  S.logEs?.close();
  const es = new EventSource(`/api/v1/devices/${S.route.id}/minerlog`);
  S.logEs = es;
  es.addEventListener('line', e => {
    const l = JSON.parse(e.data);
    const tv = new Date(l.time).toLocaleTimeString(LOCALE);
    minerLog.append(h('div', { class: (l.level || 'I')[0] }, `${tv} ${l.tag ? l.tag + ': ' : ''}${l.message}`));
    while (minerLog.childElementCount > 2000) minerLog.firstChild.remove();
    if (follow.checked) minerLog.scrollTop = minerLog.scrollHeight;
  });
  es.addEventListener('status', e => { state.textContent = JSON.parse(e.data).text; });
  setTimeout(fillAppLog, 0);
  return h('div', { class: 'grid', style: 'grid-template-columns:repeat(auto-fit,minmax(340px,1fr))' },
    h('div', { class: 'card stack' }, h('h3', {}, t('App-Protokoll (Server)')), appLog),
    h('div', { class: 'card stack' }, h('div', { class: 'titlebar' }, h('h3', {}, t('Miner-Logs live')), h('span', { class: 'spacer' }), state),
      minerLog, h('label', { class: 'check' }, follow, t('automatisch mitscrollen'))));
}

function fillAppLog() {
  const box = $('#app-log');
  if (!box || !S.detail?.log) return;
  fill(box, ...S.detail.log.map(l => h('div', {}, l)));
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
  mount(h('p', { class: 'muted' }, t('Lade …')));
  const tv = await run(() => api('/tax/rewards'));
  if (!tv) return;
  mount(h('div', { class: 'stack' },
    h('div', { class: 'card stack' },
      h('div', { class: 'titlebar' }, h('h2', {}, t('Steuer – dokumentierte Zuflüsse')), h('span', { class: 'spacer' }),
        h('a', { class: 'btn', href: '/api/v1/tax/rewards.csv', download: '' }, t('CSV exportieren'))),
      h('p', { class: 'muted small' }, t('Überwachte Wallets: {0} · letzte Prüfung {1}. Wallets und Verkäufe bearbeitest du weiterhin in der Desktop-App (Steuer-Modul); die Erfassung läuft hier rund um die Uhr.', tv.wallets.length, time(tv.status)))),
    h('div', { class: 'card table-wrap' }, tv.rewards.length ? h('table', {},
      h('thead', {}, h('tr', {}, [t('Datum'), 'Coin', t('Betrag'), t('Wert (EUR)'), t('Wallet'), 'TXID'].map(x => h('th', {}, x)))),
      h('tbody', {}, tv.rewards.map(r => h('tr', {}, h('td', {}, time(r.receivedAtUtc)), h('td', {}, r.coin), h('td', { class: 'num' }, r.amount),
        h('td', { class: 'num' }, r.eurValue != null ? n(r.eurValue, 2) : '–'), h('td', {}, r.walletLabel || ''), h('td', { class: 'small muted' }, (r.txId || '').slice(0, 16) + '…')))))
      : h('p', { class: 'muted' }, t('Noch keine Zuflüsse erfasst.')))));
}

// ---------- Einstellungen ----------

async function renderSettings() {
  mount(h('p', { class: 'muted' }, t('Lade …')));
  const [s, tokens, status] = await Promise.all([api('/settings'), api('/tokens'), api('/status')]).catch(e => { toast(e.message, 'error'); return []; });
  if (!s) return;
  const nt = s.notifications, wd = s.watchdog, pw = s.poolWatch, dr = s.dailyReport, ps = s.priceSource, la = s.logAlerts;
  const text = (obj, key, type = 'text') => h('input', { type, value: obj[key] ?? '', oninput: e => { obj[key] = type === 'number' ? Number(e.target.value) : e.target.value; } });
  const select = (obj, key, opts) => { const el = h('select', { onchange: e => { obj[key] = e.target.value; } }, opts.map(([v, l]) => h('option', { value: v }, l))); el.value = obj[key]; return el; };
  const pin = h('input', { type: 'password', inputmode: 'numeric', placeholder: s.viewerPinSet ? t('gesetzt – leer lassen = unverändert') : t('noch keine PIN') });
  const patterns = h('textarea', { rows: 3, value: (la.patterns || []).join('\n'), oninput: e => { la.patterns = e.target.value.split('\n').map(x => x.trim()).filter(Boolean); } });

  const save = async () => {
    s.newViewerPin = pin.value || null;
    if (await run(() => api('/settings', { method: 'PUT', body: s }), t('Einstellungen gespeichert.'))) { pin.value = ''; s.viewerPinSet = s.viewerPinSet || !!s.newViewerPin; }
  };

  // Geräte
  const devName = h('input', { placeholder: t('Name') }), devHost = h('input', { placeholder: t('IP-Adresse oder Hostname') });

  // Netzwerksuche: Treffer mit „Hinzufügen“, bereits eingetragene Miner markiert
  const scanResult = h('div', { class: 'stack' });
  const scanBtn = h('button', {
    class: 'btn', onclick: async () => {
      scanBtn.disabled = true;
      fill(scanResult, h('p', { class: 'muted small' }, t('Suche im Heimnetz läuft … (wenige Sekunden je Netz)')));
      const r = await run(() => api('/devices/scan', { method: 'POST', body: {} }));
      scanBtn.disabled = false;
      if (!r) { fill(scanResult); return; }
      const nets = r.networks.join(', ') || '–';
      fill(scanResult, r.miners.length === 0
        ? h('p', { class: 'muted small' }, t('Keine Miner gefunden (durchsucht: {0}). Docker: nur mit „network_mode: host“ sichtbar – sonst IP-Adresse unten eintragen.', nets))
        : [h('p', { class: 'muted small' }, t('{0} Miner gefunden (durchsucht: {1}).', r.miners.length, nets)),
           h('div', { class: 'table-wrap' }, h('table', {},
             h('thead', {}, h('tr', {}, [t('Name'), t('Adresse'), t('Modell'), t('Hashrate'), ''].map(x => h('th', {}, x)))),
             h('tbody', {}, r.miners.map(m => h('tr', {},
               h('td', {}, m.name || '–'), h('td', { class: 'mono' }, m.address), h('td', {}, m.model || '–'),
               h('td', { class: 'num' }, m.hashrate ? hash(m.hashrate) : '–'),
               h('td', {}, m.known ? h('span', { class: 'pill gray' }, t('eingetragen')) : h('button', {
                 class: 'btn small primary', onclick: async () => {
                   if (await run(() => api('/devices', { method: 'POST', body: { name: m.name || m.address, host: m.address } }), t('Gerät hinzugefügt.'))) renderSettings();
                 },
               }, t('Hinzufügen'))))))))]);
    },
  }, t('Im Netz suchen'));
  const scanBox = h('div', { class: 'stack' }, h('div', { class: 'row' }, scanBtn), scanResult);
  const copyCard = copySettingsCard(status.devices);
  const deviceRows = status.devices.map(d => {
    const name = h('input', { value: d.name });
    return h('tr', {}, h('td', {}, name), h('td', {}, d.host), h('td', {},
      h('button', { class: 'btn small', onclick: () => run(() => api(`/devices/${d.id}`, { method: 'PUT', body: { name: name.value } }), t('Gespeichert.')) }, t('Speichern')), ' ',
      h('button', {
        class: 'btn small danger', onclick: async () => {
          if (!await confirmBox(t('Gerät entfernen'), t('„{0}“ entfernen? Verlauf in history.db und Steuerdaten bleiben erhalten.', d.name), t('Entfernen'), true)) return;
          if (await run(() => api(`/devices/${d.id}`, { method: 'DELETE' }), t('Entfernt.'))) renderSettings();
        },
      }, t('Entfernen'))));
  });

  // Token für die Desktop-App
  const tokName = h('input', { placeholder: t('z. B. Desktop Arbeitszimmer') });
  const tokenRows = tokens.map(tv => h('tr', {}, h('td', {}, tv.name), h('td', {}, time(tv.createdUtc)), h('td', {}, tv.lastUsedUtc ? time(tv.lastUsedUtc) : t('nie')),
    h('td', {}, h('button', {
      class: 'btn small danger', onclick: async () => {
        if (!await confirmBox(t('Token widerrufen'), t('Token „{0}“ widerrufen? Die Desktop-App damit verliert sofort den Zugriff.', tv.name), t('Widerrufen'), true)) return;
        if (await run(() => api(`/tokens/${tv.id}`, { method: 'DELETE' }), t('Widerrufen.'))) renderSettings();
      },
    }, t('Widerrufen')))));

  const curPw = h('input', { type: 'password', autocomplete: 'current-password' }), newPw = h('input', { type: 'password', autocomplete: 'new-password' });

  mount(h('div', { class: 'stack' },
    h('div', { class: 'card stack' }, h('h2', {}, t('Geräte')),
      h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, [t('Name'), t('Adresse'), ''].map(x => h('th', {}, x)))), h('tbody', {}, deviceRows))),
      scanBox,
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Name')), devName), h('div', {}, h('label', {}, t('Adresse')), devHost),
        h('button', { class: 'btn primary', onclick: async () => { if (await run(() => api('/devices', { method: 'POST', body: { name: devName.value, host: devHost.value } }), t('Gerät hinzugefügt.'))) renderSettings(); } }, t('Hinzufügen')))),
    copyCard,
    h('div', { class: 'card stack' }, h('h2', {}, t('Allgemein')),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, t('Abfrage alle (s)')), text(s, 'intervalSeconds', 'number')),
        h('div', {}, h('label', {}, t('Live-Verlauf (min)')), text(s, 'historyMinutes', 'number')),
        h('div', {}, h('label', {}, t('Verlauf aufbewahren (Tage)')), text(s, 'historyDays', 'number')),
        h('div', {}, h('label', {}, t('Strompreis (ct/kWh)')), text(s, 'electricityCtPerKwh', 'number')),
        h('div', {}, h('label', {}, t('Währung')), text(s, 'currency')),
        h('div', {}, h('label', {}, t('Warnung ab ASIC (°C)')), text(s, 'tempWarn', 'number')),
        h('div', {}, h('label', {}, t('Wallets prüfen alle (min)')), text(s, 'walletPollMinutes', 'number')),
        h('div', {}, h('label', {}, t('Steuer-Erfassung alle (min)')), text(s, 'taxPollMinutes', 'number'))),
      checkInput(s, 'restartAfterApply', t('Nach Frequenz-/Spannungsänderung neu starten')),
      checkInput(s, 'checkForUpdates', t('Nach neuen Versionen suchen')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Sprache des Servers (Push, Tagesbericht, E-Paper)')),
        select(s, 'language', [['auto', t('Automatisch (Systemsprache)')], ['de', 'Deutsch'], ['en', 'English']]))),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('PIN für „Nur ansehen“ (mind. 4 Ziffern, „-“ = entfernen)')), pin))),
    h('div', { class: 'card stack' }, h('h2', {}, t('Push-Benachrichtigungen')),
      notifyForm(nt, text, select),
      h('div', { class: 'row' }, checkInput(nt, 'onOffline', 'offline'), checkInput(nt, 'onOverheat', t('Überhitzung')), checkInput(nt, 'onFinds', t('Blockfund/Zufluss')),
        checkInput(nt, 'onMaintenance', t('Watchdog/Automatik/Firmware')), checkInput(nt, 'onRecord', t('Rekorde')), checkInput(nt, 'onLogAlerts', t('Log-Alarme')), checkInput(nt, 'onPool', t('Pool')),
        checkInput(nt, 'onPlugs', t('Smart Plugs'))),
      h('div', { class: 'row' },
        h('button', { class: 'btn', onclick: async () => { await save(); const r = await run(() => api('/notifications/test', { method: 'POST', body: {} })); if (r) toast(r.ok ? t('Testnachricht gesendet.') : r.error, r.ok ? 'ok' : 'error'); } }, t('Speichern & testen')),
        h('button', { class: 'btn', onclick: async () => { const r = await run(() => api('/report/send', { method: 'POST', body: {} })); if (r) toast(r.ok ? t('Tagesbericht gesendet.') : r.error, r.ok ? 'ok' : 'error'); } }, t('Tagesbericht jetzt senden')))),
    h('div', { class: 'card stack' }, h('h2', {}, t('Überwachung')),
      checkInput(wd, 'enabled', t('Watchdog: Miner ohne Hashrate neu starten')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('ohne Hashrate seit (min)')), text(wd, 'zeroHashMinutes', 'number')), h('div', {}, h('label', {}, t('Sperrzeit (min)')), text(wd, 'cooldownMinutes', 'number'))),
      checkInput(pw, 'enabled', t('Pool/Shares überwachen')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Ablehnungen ab (%)')), text(pw, 'rejectPercent', 'number')), h('div', {}, h('label', {}, t('Zeitfenster (min)')), text(pw, 'windowMinutes', 'number')),
        h('div', {}, h('label', {}, t('mind. Shares')), text(pw, 'minShares', 'number')), h('div', {}, h('label', {}, t('Antwortzeit ab (ms)')), text(pw, 'responseMs', 'number'))),
      checkInput(dr, 'enabled', t('Tagesbericht per Push')),
      checkInput(dr, 'monthly', t('Monatsbericht am Monatsersten per Push')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Uhrzeit (Stunde)')), text(dr, 'hour', 'number'))),
      checkInput(la, 'onErrors', t('Log-Alarm bei Fehlerzeilen (E)')),
      h('div', { class: 'form' }, h('div', { class: 'wide' }, h('label', {}, t('Log-Alarm bei Zeilen mit (je Zeile ein Muster)')), patterns))),
    h('div', { class: 'card stack' }, h('h2', {}, t('Strompreis-Quelle')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Quelle')), select(ps, 'source', [['none', t('keine')], ['awattar-de', t('aWATTar Deutschland')], ['awattar-at', t('aWATTar Österreich')], ['tibber', 'Tibber']])),
        h('div', {}, h('label', {}, t('Tibber-Token')), text(ps, 'tibberToken', 'password')),
        h('div', {}, h('label', {}, t('Aufschlag (ct/kWh)')), numInput(ps, 'surchargeCt', 0.1))),
      checkInput(ps, 'dynamicCosts', t('Stromkosten mit Stundenpreisen rechnen')),
      h('p', { class: 'muted small' }, t('Aufschlag bei aWATTar: Netzentgelte, Steuern und Umlagen (typisch 15–25 ct/kWh), bei Tibber 0. Ohne Preis für eine Stunde gilt der feste Strompreis.'))),
    h('div', { class: 'row' }, h('button', { class: 'btn primary', onclick: save }, t('Einstellungen speichern'))),
    h('div', { class: 'card stack' }, h('h2', {}, t('Desktop-App verbinden (API-Token)')),
      h('p', { class: 'muted small' }, t('In der Desktop-App unter Einstellungen → Betriebsart „Server“ die Adresse dieses Servers und das Token eintragen. Das Token wird nur einmal angezeigt.')),
      tokens.length ? h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, [t('Name'), t('erstellt'), t('zuletzt benutzt'), ''].map(x => h('th', {}, x)))), h('tbody', {}, tokenRows))) : null,
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Name')), tokName),
        h('button', {
          class: 'btn', onclick: async () => {
            const r = await run(() => api('/tokens', { method: 'POST', body: { name: tokName.value } }));
            if (!r) return;
            await confirmBox(t('Neues Token'), h('div', { class: 'stack' }, h('p', {}, t('Jetzt kopieren – es wird nicht noch einmal angezeigt:')), h('input', { value: r.token, readonly: true, onfocus: e => e.target.select() })), t('Fertig'));
            renderSettings();
          },
        }, t('Token erzeugen')))),
    backupCard(),
    mqttCard(),
    plugsCard(),
    updateCard(),
    h('div', { class: 'card stack' }, h('h2', {}, t('Admin-Passwort ändern')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Aktuell')), curPw), h('div', {}, h('label', {}, t('Neu (mind. 10 Zeichen)')), newPw),
        h('button', { class: 'btn', onclick: async () => { if (await run(() => api('/password', { method: 'POST', body: { current: curPw.value, password: newPw.value } }), t('Passwort geändert – bitte neu anmelden.'))) { S.role = 'None'; stopEvents(); renderLogin(); } } }, t('Ändern')))),
    h('p', { class: 'muted small' }, t('Server {0} · {1}', S.info.version, S.info.os)),
    h('p', { class: 'muted small' }, t('BitaxeTuner – Copyright © 2026 BitaxeTuner contributors. Freie Software unter der GNU GPL v3.0, '),
      h('b', {}, t('ohne jede Gewähr')), t('. Quelltext, Lizenz und Hinweise zu enthaltenen Komponenten: '),
      h('a', { href: 'https://github.com/Elemirus1996/BitaxeTuner', target: '_blank', rel: 'noopener' }, 'github.com/Elemirus1996/BitaxeTuner'), '.')));
}

// ---------- Zusatzlüfter (Pico) ----------


function fanRows(fans) {
  if (!fans) return [h('p', { class: 'muted' }, t('Lüftersteuerung ist ausgeschaltet.'))];
  const sensors = fans.sensors || [];
  const head = h('div', { class: 'stack' },
    h('p', { class: fans.connected ? 'ok' : 'danger' },
      fans.connected ? t('Verbunden{0}', fans.device ? ': ' + fans.device : '') : t('Nicht verbunden{0} – Lüfter laufen dann auf 100 %.', fans.error ? ': ' + fans.error : '')),
    sensors.length
      ? h('div', { class: 'row' }, sensors.map(s => h('span', { class: `pill ${s.hot || s.temp == null ? 'danger' : 'gray'}`, title: t('Warnung ab {0} °C', n(s.warnTemp, 1)) },
          `${s.name}: ${s.temp == null ? t('fehlt') : n(s.temp, 1) + t(' °C')}${s.hot ? t(' – zu warm!') : ''}`)))
      : fans.connected ? h('p', { class: 'muted' }, t('Kein Temperaturfühler (DS18B20) erkannt.')) : null);
  if (!fans.channels.length) return [head, h('p', { class: 'muted' }, t('Noch kein Kanal belegt.'))];
  return [head, h('div', { class: 'table-wrap' }, h('table', {},
    h('thead', {}, h('tr', {}, [t('Kanal'), t('Lüfter'), t('Modus'), t('Soll'), t('Drehzahl'), t('Begründung')].map(x => h('th', {}, x)))),
    h('tbody', {}, fans.channels.map(c => h('tr', {},
      h('td', { class: 'mono' }, t('K{0}', c.channel)),
      h('td', {}, c.name),
      h('td', {}, c.mode === 'manual' ? t('manuell') : t('Automatik')),
      h('td', { class: 'num' }, `${c.percent} %`),
      h('td', { class: `num ${c.stalled ? 'danger' : ''}` }, c.rpm == null ? '–' : c.stalled ? t('steht!') : t('{0} U/min', c.rpm)),
      h('td', { class: 'small muted' }, c.reason))))))];
}

function updateFanTable() {
  const box = $('#fan-status');
  if (box && S.status) fill(box, ...fanRows(S.status.fans));
}

async function renderFans() {
  mount(h('p', { class: 'muted' }, t('Lade …')));
  const data = await run(() => api('/fans'));
  if (!data) return;
  const status = h('div', { id: 'fan-status', class: 'stack' }, fanRows(data.status));
  const display = await run(() => api('/display'));
  const parts = [h('div', { class: 'card stack' }, h('h2', {}, t('Zusatzlüfter')), status)];
  if (isAdmin()) parts.push(quickActions(data.status, display));
  if (display) parts.push(displayCard(display));
  if (isAdmin()) parts.push(fanEditor(data));
  else parts.push(h('p', { class: 'muted small' }, t('Einstellungen ändern kann nur der Admin.')));
  mount(h('div', { class: 'stack' }, parts));
}

/** Die vier Taster am Pico – hier auch per Klick. */
function quickActions(fans, display) {
  const mode = fans?.override || 'None';
  const label = { None: t('nach Einstellung (Automatik)'), Off: t('AUS per Taste – Sicherheitsregeln aktiv'), Full: t('alle 100 %') }[mode];
  const next = async () => { const r = await run(() => api('/display/next', { method: 'POST', body: {} }), t('Anzeige wechselt in Kürze (frühestens 30 s nach der letzten Aktualisierung).')); if (r) renderFans(); };
  const set = async m => { const r = await run(() => api('/fans/override', { method: 'POST', body: { mode: m } })); if (r) { const box = $('#fan-status'); if (box) fill(box, ...fanRows(r.status)); renderFans(); } };
  return h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h2', {}, t('Schnellaktionen')), h('span', { class: 'spacer' }),
      h('span', { class: `pill ${mode === 'None' ? 'gray' : ''}` }, t('Lüfter: {0}', label))),
    h('p', { class: 'muted small' }, t('Dieselben Aktionen wie die Taster am Pico: 1 = Anzeige weiter/quittieren, 2 = Automatik, 3 = alle 100 %, 3 (5 s halten) = Lüfter aus, 4 (3 s halten) = Neustart. „Aus“ und „100 %“ gelten bis zum nächsten Neustart des Servers.')),
    h('div', { class: 'row' },
      h('button', { class: 'btn', onclick: next }, t('1 · Anzeige weiter')),
      h('button', { class: 'btn', onclick: () => set('auto') }, t('2 · Automatik')),
      h('button', { class: 'btn', onclick: () => set('full') }, t('3 · Alle 100 %')),
      h('button', { class: 'btn', onclick: () => set('off') }, t('3 lang · Lüfter aus')),
      h('button', {
        class: 'btn danger', onclick: async () => {
          const text = display?.rebootAvailable
            ? t('Pico und Raspberry Pi neu starten?\n\nLaufende Benchmarks werden beendet und ihre Einstellungen wiederhergestellt. Die Oberfläche ist ca. 1–2 Minuten nicht erreichbar, die Miner laufen weiter.')
            : t('Pico neu starten?\n\nEin Neustart des Rechners ist auf dieser Installation nicht eingerichtet.');
          if (!await confirmBox(t('Neustart'), text, t('Neu starten'), true)) return;
          const r = await run(() => api('/system/reboot', { method: 'POST', body: {} }));
          if (r) toast(r.message, 'ok', 15000);
        },
      }, t('4 · Neustart'))));
}

/** E-Paper: Vorschau genau wie auf dem Display, Status, Einstellungen. */
function displayCard(d) {
  const st = d.status;
  const img = h('img', { src: `/api/v1/display/preview.png?t=${Date.now()}`, alt: t('Vorschau der E-Paper-Anzeige'), style: 'width:100%;max-width:800px;border:1px solid var(--border);border-radius:6px;background:#fff' });
  const scenes = [['', t('Als Nächstes')], ['Overview', t('Übersicht')], ['Daily', t('Tagesbilanz')], ['Chart', t('Verlauf 24 h')], ['Soak', t('Dauertest')],
    ['Network', t('Pool & Netzwerk')], ['BlockFound', t('Blockfund')], ['Alarm', t('Warnungen')], ['BestDiff', t('Best-Diff-Rekord')]];
  const sceneSel = h('select', { style: 'width:auto', onchange: () => { img.src = `/api/v1/display/preview.png?scene=${sceneSel.value}&t=${Date.now()}`; } },
    scenes.map(([v, tv]) => h('option', { value: v }, tv)));
  const info = !st.enabled ? t('Anzeige ist ausgeschaltet – die Vorschau zeigt, was sie anzeigen würde.')
    : t('{0} · zuletzt {1}', st.connected ? t('Pico verbunden') : t('Pico nicht verbunden'), st.lastShown ? time(st.lastShown) : t('noch nie')) +
      (st.nextDue ? t(' · nächste Aktualisierung ab {0}', new Date(st.nextDue).toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' })) : '') +
      (st.refreshing ? t(' · baut gerade auf …') : '') + (st.error ? ` · ${st.error}` : '');
  const parts = [
    h('div', { class: 'titlebar' }, h('h2', {}, t('E-Paper-Anzeige')), h('span', { class: 'spacer' }),
      sceneSel,
      h('button', { class: 'btn small', onclick: () => { img.src = `/api/v1/display/preview.png?scene=${sceneSel.value}&t=${Date.now()}`; } }, t('Vorschau neu laden'))),
    h('p', { class: `small ${st.error ? 'danger' : 'muted'}` }, info),
    img,
  ];
  if (isAdmin() && d.settings) {
    const s = structuredClone(d.settings);
    parts.push(
      checkInput(s, 'enabled', t('Anzeige einschalten (7,5″ E-Paper am Pico)')),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, t('Titel')), h('input', { value: s.title, oninput: e => { s.title = e.target.value; } })),
        h('div', {}, h('label', {}, t('aktualisieren alle (min, mind. 3)')), numInput(s, 'intervalMinutes')),
        h('div', {}, h('label', {}, t('Ruhe von (Uhr)')), numInput(s, 'quietFromHour')),
        h('div', {}, h('label', {}, t('bis (Uhr)')), numInput(s, 'quietToHour'))),
      checkInput(s, 'quietEnabled', t('Nachts nur bei Warnungen aktualisieren')),
      h('h3', {}, t('Seiten (Taste 1 blättert)')),
      h('div', { class: 'row' },
        checkInput(s.pages, 'overview', t('Übersicht')),
        checkInput(s.pages, 'daily', t('Tagesbilanz')),
        checkInput(s.pages, 'chart', t('Verlauf 24 h')),
        checkInput(s.pages, 'soak', t('Dauertest (wenn aktiv)')),
        checkInput(s.pages, 'network', t('Pool & Netzwerk'))),
      checkInput(s, 'rotatePages', t('Bei jeder Aktualisierung zur nächsten Seite wechseln')),
      h('h3', {}, t('Sonderanzeigen')),
      h('div', { class: 'form' },
        h('div', {}, checkInput(s, 'blockFoundScreen', t('Blockfund als Vollbild'))),
        h('div', {}, h('label', {}, t('stehen lassen (Stunden, bis Taste 1)')), numInput(s, 'blockFoundHoldHours'))),
      checkInput(s, 'alarmFullscreen', t('Warnungen als Vollbild (Taste 1 quittiert bis zur nächsten neuen Warnung)')),
      checkInput(s, 'bestDiffNotice', t('Neuen Best-Diff-Rekord einmal groß anzeigen')),
      h('h3', {}, t('Taster')),
      checkInput(s, 'buttonsEnabled', t('Taster am Pico auswerten')),
      checkInput(s, 'allowSystemReboot', t('Taste 4 (3 s halten) startet auch den Raspberry Pi neu')),
      h('div', { class: 'row' },
        h('button', { class: 'btn primary', onclick: async () => { if (await run(() => api('/display', { method: 'PUT', body: s }), t('Anzeige-Einstellungen gespeichert.'))) renderFans(); } }, t('Speichern')),
        h('button', { class: 'btn', onclick: () => run(() => api('/display/refresh', { method: 'POST', body: {} }), t('Anzeige wird aktualisiert, sobald die Mindestpause von 3 Minuten um ist.')) }, t('Jetzt aktualisieren'))),
      h('p', { class: 'muted small' }, t('Das E-Paper wird höchstens alle 3 Minuten neu aufgebaut (Herstellerempfehlung), ein Bildaufbau dauert etwa 16 Sekunden.')));
  }
  return h('div', { class: 'card stack' }, parts);
}

function curveInputs(curve) {
  return [
    h('div', {}, h('label', {}, t('ab °C (Start)')), numInput(curve, 'startTemp', 0.5)),
    h('div', {}, h('label', {}, t('dort %')), numInput(curve, 'startPercent')),
    h('div', {}, h('label', {}, t('100 % ab °C')), numInput(curve, 'fullTemp', 0.5)),
    h('div', {}, h('label', {}, t('darunter %')), numInput(curve, 'minPercent')),
    h('div', {}, h('label', {}, t('Hysterese °C')), numInput(curve, 'hysteresis', 0.5)),
  ];
}

/** Dienst-Auswahl für Push: nur die Felder des gewählten Dienstes sind sichtbar. */
function notifyForm(nt, text, select) {
  const field = (provider, label, el) => { const d = h('div', { 'data-provider': provider }, h('label', {}, label), el); return d; };
  const fields = [
    field('ntfy', t('ntfy-Server'), text(nt, 'ntfyServer')), field('ntfy', t('ntfy-Topic'), text(nt, 'ntfyTopic')),
    field('telegram', t('Telegram-Bot-Token'), text(nt, 'telegramBotToken', 'password')), field('telegram', t('Telegram-Chat-ID'), text(nt, 'telegramChatId')),
    field('discord', t('Discord-Webhook-URL'), text(nt, 'discordWebhookUrl', 'password')),
    field('pushover', t('Pushover-User-Key'), text(nt, 'pushoverUserKey', 'password')), field('pushover', t('Pushover-App-Token'), text(nt, 'pushoverAppToken', 'password')),
    field('webhook', t('Webhook-URL (JSON-POST: title, message, priority)'), text(nt, 'webhookUrl')),
  ];
  const show = () => fields.forEach(f => { f.style.display = f.dataset.provider === nt.provider ? '' : 'none'; });
  const sel = select(nt, 'provider', [['none', t('aus')], ['ntfy', 'ntfy'], ['telegram', 'Telegram'], ['discord', 'Discord'], ['pushover', 'Pushover'], ['webhook', t('Eigener Webhook')]]);
  sel.addEventListener('change', show);
  show();
  return h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Dienst')), sel), ...fields);
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
  const renderChannels = () => fill(channelsBox, ...f.channels.map(c => {
    const row = h('div', { class: 'panel' },
      h('div', { class: 'titlebar' }, h('h3', {}, t('K{0}', c.channel)),
        h('span', { class: 'muted small' }, t('PWM GP{0} · Tacho GP{1}', (c.channel - 1) * 2, 15 + c.channel))),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, t('Verwendung')), selectInput(c, 'role', [['none', t('nicht belegt')], ['miner', t('VR-Lüfter eines Miners')], ['case', t('Gehäuse (Gruppe)')]], renderChannels)),
        c.role === 'miner' ? h('div', {}, h('label', {}, t('Miner')), selectInput(c, 'minerHost', [['', t('— wählen —')], ...miners.map(m => [m.host, m.name])])) : null,
        c.role !== 'none' ? h('div', {}, h('label', {}, t('Name (optional)')), h('input', { value: c.name, oninput: e => { c.name = e.target.value; } })) : null,
        c.role === 'miner' ? h('div', {}, h('label', {}, t('Modus')), selectInput(c, 'mode', [['auto', t('Automatik (VR-Temperatur)')], ['manual', t('Manuell')]], renderChannels)) : null,
        c.role === 'miner' && c.mode === 'manual' ? h('div', {}, h('label', {}, t('Drehzahl %')), numInput(c, 'manualPercent')) : null),
      c.role === 'miner' && c.mode === 'auto' ? h('div', { class: 'form' }, curveInputs(c.curve)) : null,
      c.role !== 'none' ? checkInput(c, 'hasTach', t('Lüfter hat Drehzahlsignal (Meldung, wenn er steht)')) : null);
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
  const renderCase = () => fill(caseBox, 
    h('div', { class: 'form' },
      h('div', {}, h('label', {}, t('Modus')), selectInput(cs, 'mode', [['auto', t('Automatik')], ['manual', t('Manuell')]], renderCase)),
      cs.mode === 'manual' ? h('div', {}, h('label', {}, t('Drehzahl %')), numInput(cs, 'manualPercent')) : null,
      cs.mode === 'auto' ? h('div', {}, h('label', {}, t('Messgröße')), selectInput(cs, 'sensor', [['vr', t('VR-Temperatur der Miner')], ['asic', t('ASIC-Temperatur der Miner')], ['case', t('Temperaturfühler (DS18B20)')]], renderCase)) : null,
      cs.mode === 'auto' ? h('div', {}, h('label', {}, t('Miner ohne Daten → mind. %')), numInput(cs, 'unknownPercent')) : null),
    cs.mode === 'auto' ? h('div', { class: 'stack' },
      cs.sensor === 'case'
        ? h('p', { class: 'muted small' }, t('Es zählt der wärmste Fühler mit Haken „Gehäuselüfter“ (Karte „Temperaturfühler“). Fehlt einer davon, gilt „Miner ohne Daten → mind. %“.'))
        : h('p', { class: 'muted small' }, t('Es zählt die höchste Temperatur der ausgewählten Miner (keine Auswahl = alle).')),
      cs.sensor === 'case' ? null : minerChecks,
      h('div', { class: 'form' }, curveInputs(cs.curve)),
      checkInput(cs, 'nightEnabled', t('Nachtbetrieb (leiser)')),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, t('von Uhr')), numInput(cs, 'nightFromHour')),
        h('div', {}, h('label', {}, t('bis Uhr')), numInput(cs, 'nightToHour')),
        h('div', {}, h('label', {}, t('höchstens %')), numInput(cs, 'nightMaxPercent'))),
      h('p', { class: 'muted small' }, t('Nachts wird gedrosselt, außer eine Temperatur erreicht den 100-%-Punkt der Kurve.'))) : null);
  renderCase();

  // Temperaturfühler: Pico meldet jeden mit fester Kennung; neue trägt der Server selbst ein
  f.sensors = f.sensors || [];
  const sensorBox = h('div', { class: 'stack' });
  const live = id => ((S.status && S.status.fans && S.status.fans.sensors) || []).find(s => s.id === id);
  const renderSensors = () => fill(sensorBox, 
    f.sensors.length === 0
      ? h('p', { class: 'muted' }, t('Noch kein Fühler erkannt. DS18B20 an GP26 anschließen (alle parallel, ein 4,7-kΩ-Widerstand nach 3,3 V) – der Server trägt jeden neuen Fühler hier ein.'))
      : h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, [t('Jetzt'), t('Name'), t('Warnung ab °C'), t('Gehäuselüfter'), t('Anzeige'), t('Kennung'), ''].map(x => h('th', {}, x)))),
        h('tbody', {}, f.sensors.map(s => {
          const l = live(s.id);
          return h('tr', {},
            h('td', { class: `num ${!l || l.temp == null || l.hot ? 'danger' : ''}` }, !l || l.temp == null ? t('fehlt') : t('{0} °C', n(l.temp, 1))),
            h('td', {}, h('input', { value: s.name, maxlength: 24, style: 'min-width:9em', oninput: e => { s.name = e.target.value; } })),
            h('td', {}, numInput(s, 'warnTemp', 0.5)),
            h('td', {}, h('input', { type: 'checkbox', checked: s.caseFans, onchange: e => { s.caseFans = e.target.checked; } })),
            h('td', {}, h('input', { type: 'checkbox', checked: s.showOnDisplay, onchange: e => { s.showOnDisplay = e.target.checked; } })),
            h('td', { class: 'mono small muted' }, s.id),
            h('td', {}, h('button', {
              class: 'btn small', title: t('Aus der Liste entfernen (z. B. abgebauter Fühler). Wird er wieder gemeldet, erscheint er neu.'),
              onclick: () => { f.sensors = f.sensors.filter(x => x !== s); renderSensors(); },
            }, t('Entfernen'))));
        })))),
    h('p', { class: 'muted small' }, t('Welcher ist welcher? Einen Fühler kurz in der Hand anwärmen und „Jetzt“ beobachten (aktualisiert beim Neuladen der Seite). ') +
      t('„Gehäuselüfter“: zählt für die Messgröße „Temperaturfühler“ der Gehäuselüfter; fehlt so ein Fühler, laufen sie auf dem Wert für „unbekannt“. ') +
      t('Über der Warnschwelle gibt es eine Push-Meldung und eine rote Zeile auf dem Display.')),
    h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Warnschwelle für neu erkannte Fühler °C')), numInput(f, 'caseTempWarn', 0.5))));
  renderSensors();

  const save = async () => {
    const r = await run(() => api('/fans', { method: 'PUT', body: f }), t('Lüfter-Einstellungen gespeichert.'));
    if (r) { const box = $('#fan-status'); if (box) fill(box, ...fanRows(r.status)); }
  };
  return h('div', { class: 'stack' },
    h('div', { class: 'card stack' }, h('h2', {}, t('Verbindung')),
      checkInput(f, 'enabled', t('Lüfter regeln (Pico per USB am Server; der Port gilt auch für Anzeige und Taster)')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Port („auto“ = Pico automatisch finden)')), h('input', { value: f.port, oninput: e => { f.port = e.target.value; } }))),
      h('div', { class: 'row' },
        h('button', { class: 'btn primary', onclick: save }, t('Speichern')),
        h('button', {
          class: 'btn', onclick: async () => {
            if (!await confirmBox(t('Programm neu aufspielen'), t('Das Lüfterprogramm wird neu auf den Pico geschrieben. Die Lüfter laufen dabei kurz mit 100 %.'), t('Aufspielen'))) return;
            const r = await run(() => api('/fans/firmware', { method: 'POST', body: {} }));
            if (r) { toast(r.ok ? t('Programm aufgespielt, Pico verbunden.') : t('Pico nicht verbunden – siehe Status.'), r.ok ? 'ok' : 'error'); const box = $('#fan-status'); if (box) fill(box, ...fanRows(r.status)); }
          },
        }, t('Programm neu aufspielen')))),
    h('div', { class: 'card stack' }, h('h2', {}, t('Kanäle')), channelsBox),
    h('div', { class: 'card stack' }, h('h2', {}, t('Gehäuselüfter (alle Kanäle mit „Gehäuse“)')), caseBox),
    h('div', { class: 'card stack' }, h('h2', {}, t('Temperaturfühler')), sensorBox),
    h('div', { class: 'row' }, h('button', { class: 'btn primary', onclick: save }, t('Speichern'))),
    h('p', { class: 'muted small' }, t('Immer aktiv: Miner offline oder Daten älter als 30 s → sein Lüfter auf 100 %. Bekommt der Pico 5 s lang keinen Befehl, schaltet er selbst alle Lüfter auf 100 %.')));
}

/** Smart Plugs (Shelly): anlegen, Rolle und Miner zuordnen, testen. Es wird nie geschaltet, nur gemessen. */
function plugsCard() {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  const render = d => {
    const s = d.settings;
    const passwords = {};
    const clear = new Set();
    const list = h('div', { class: 'stack' });
    const roles = [['miners', t('speist Miner')], ['other', t('Nebenverbraucher (Zusatzlüfter, Pi …)')], ['total', t('Gesamtmessung (alles dahinter)')]];
    const statusOf = id => d.status?.find(x => x.id === id);
    const draw = () => fill(list, s.items.length ? s.items.map((p, i) => {
      const st = statusOf(p.id);
      const minerBox = h('div', { class: 'row', style: 'flex-wrap:wrap' }, d.miners.map(m => h('label', { class: 'row' },
        h('input', { type: 'checkbox', checked: p.miners.includes(m.host), onchange: e => { p.miners = e.target.checked ? [...p.miners, m.host] : p.miners.filter(x => x !== m.host); } }),
        h('span', {}, m.name))));
      minerBox.style.display = p.role === 'miners' ? '' : 'none';
      const role = h('select', { onchange: e => { p.role = e.target.value; minerBox.style.display = p.role === 'miners' ? '' : 'none'; } },
        roles.map(([v, l]) => h('option', { value: v }, l)));
      role.value = p.role;
      const pw = h('input', {
        type: 'password', autocomplete: 'new-password',
        placeholder: d.passwordSet?.[p.id] ? t('gespeichert – leer lassen = unverändert') : t('Passwort (falls nötig)'),
        oninput: e => { passwords[p.id] = e.target.value; },
      });
      const probe = async () => {
        const r = await run(() => api('/plugs/probe', { method: 'POST', body: { host: p.host, user: p.user, password: passwords[p.id] || null, id: p.id, channel: p.channel } }));
        if (r) toast(t('{0} (Gen {1}): {2} W', r.model, r.generation, n(r.powerW, 1)), 'ok', 8000);
      };
      return h('div', { class: 'card stack' },
        h('div', { class: 'row' }, h('b', { style: 'flex:1' }, p.name || t('Smart Plug')),
          st ? h('span', { class: `small ${st.online ? 'ok' : 'danger'}` }, st.online ? t('{0} W', n(st.powerW, 1)) + (st.model ? ` · ${st.model}` : '') : (st.error || 'offline')) : null,
          h('button', { class: 'btn small', onclick: probe }, t('Testen')),
          h('button', { class: 'btn small danger', onclick: () => { s.items.splice(i, 1); if (p.id) clear.add(p.id); draw(); } }, t('Entfernen'))),
        h('div', { class: 'form' },
          h('div', {}, h('label', {}, t('Name')), h('input', { value: p.name, oninput: e => { p.name = e.target.value; } })),
          h('div', {}, h('label', {}, t('Adresse (IP oder Name, „sim“ = Simulation)')), h('input', { value: p.host, placeholder: '192.168.1.60', oninput: e => { p.host = e.target.value.trim(); } })),
          h('div', {}, h('label', {}, t('Kanal')), numInput(p, 'channel')),
          h('div', {}, h('label', {}, t('Benutzer (bei Gen2+ immer admin)')), h('input', { value: p.user, oninput: e => { p.user = e.target.value; } })),
          h('div', {}, h('label', {}, t('Passwort')), pw),
          h('div', {}, h('label', {}, t('Rolle')), role)),
        minerBox);
    }) : h('p', { class: 'muted small' }, t('Noch kein Smart Plug eingerichtet.')));
    draw();
    fill(body,
      h('p', { class: 'muted small' }, t('Shelly-Steckdosen mit Leistungsmessung (Gen1, Plus/Pro/Gen3) messen den echten Verbrauch inklusive Netzteil und Zusatzlüftern. BitaxeTuner liest nur – geschaltet wird nie.')),
      list,
      h('div', { class: 'row' },
        h('button', { class: 'btn', onclick: () => { s.items.push({ id: '', name: t('Smart Plug'), host: '', channel: 0, user: 'admin', role: 'miners', miners: [] }); draw(); } }, t('Plug hinzufügen'))),
      checkInput(s, 'useForCosts', t('Kosten, Tagesbericht und Gesamteffizienz mit den Werten an der Steckdose rechnen')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('abfragen alle (s)')), numInput(s, 'intervalSeconds'))),
      h('div', { class: 'row' },
        h('button', {
          class: 'btn primary', onclick: async () => {
            const r = await run(() => api('/plugs', { method: 'PUT', body: { settings: s, passwords, clearPasswords: [...clear] } }));
            if (r) { render(r); toast(t('Gespeichert.'), 'ok'); }
          },
        }, t('Speichern'))));
  };
  api('/plugs').then(render).catch(e => fill(body, h('p', { class: 'danger' }, e.message)));
  return h('div', { class: 'card stack' }, h('h2', {}, t('Smart Plugs')), body);
}

/** Home Assistant / MQTT: Broker, Geräteerkennung, optional Lüfter-Modus aus Home Assistant. */
function mqttCard() {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  const render = d => {
    const s = d.settings;
    const pw = h('input', { type: 'password', autocomplete: 'new-password', placeholder: d.passwordSet ? t('gespeichert – leer lassen = unverändert') : t('Passwort (falls nötig)') });
    const text = (key, ph = '') => h('input', { value: s[key] ?? '', placeholder: ph, oninput: e => { s[key] = e.target.value; } });
    fill(body, 
      h('p', { class: 'muted small' }, t('Sendet Hashrate, Leistung, Temperaturen, Lüfter und Temperaturfühler an einen MQTT-Broker (z. B. das Mosquitto-Add-on von Home Assistant). Home Assistant legt die Geräte automatisch an. Frequenz und Spannung lassen sich über MQTT nicht ändern.')),
      h('p', { class: `small ${d.connected ? 'ok' : s.enabled ? 'danger' : 'muted'}` },
        d.connected ? t('Verbunden.') : s.enabled ? t('Nicht verbunden{0}', d.error ? ': ' + d.error : '') : t('Ausgeschaltet.')),
      checkInput(s, 'enabled', t('MQTT einschalten')),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, t('Broker (IP oder Name)')), text('host', t('z. B. homeassistant.local'))),
        h('div', {}, h('label', {}, t('Port')), numInput(s, 'port')),
        h('div', {}, h('label', {}, t('Benutzer')), text('user')),
        h('div', {}, h('label', {}, t('Passwort')), pw),
        h('div', {}, h('label', {}, t('Topic')), text('baseTopic', 'bitaxetuner')),
        h('div', {}, h('label', {}, t('senden alle (s)')), numInput(s, 'intervalSeconds'))),
      checkInput(s, 'tls', t('TLS (verschlüsselt, meist Port 8883)')),
      checkInput(s, 'discovery', t('Home-Assistant-Geräteerkennung')),
      checkInput(s, 'allowFanControl', t('Zusatzlüfter-Modus (Automatik / 100 % / Aus) aus Home Assistant schalten erlauben')),
      h('div', { class: 'row' },
        h('button', {
          class: 'btn primary', onclick: async () => {
            const r = await run(() => api('/mqtt', { method: 'PUT', body: { settings: s, password: pw.value || null, clearPassword: false } }));
            if (r) { render(r); toast(r.connected ? t('Verbunden – die Geräte erscheinen in Home Assistant unter „MQTT“.') : r.settings.enabled ? t('Gespeichert, aber nicht verbunden: {0}', r.error || t('unbekannt')) : t('Gespeichert.'), r.connected || !r.settings.enabled ? 'ok' : 'error', 10000); }
          },
        }, t('Speichern und verbinden'))));
  };
  api('/mqtt').then(render).catch(e => fill(body, h('p', { class: 'danger' }, e.message)));
  return h('div', { class: 'card stack' }, h('h2', {}, t('Home Assistant / MQTT')), body);
}

/** Tägliche Sicherung: Datenordner (immer), Ordner/USB-Stick, Netzlaufwerk; Status, Jetzt sichern, Download. */
function backupCard() {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  const mb = b => t('{0} MB', n(b / 1024 / 1024, 1));
  const load = async () => {
    const d = await api('/backup').catch(e => { fill(body, h('p', { class: 'danger' }, e.message)); return null; });
    if (!d) return;
    const s = d.settings, u = d.usb;
    const smbPw = h('input', { type: 'password', autocomplete: 'new-password', placeholder: d.smbPasswordSet ? t('gespeichert – leer lassen = unverändert') : t('Passwort') });
    const text = (obj, key, ph = '') => h('input', { value: obj[key] ?? '', placeholder: ph, oninput: e => { obj[key] = e.target.value; } });
    const st = d.status;
    const statusBox = h('div', { class: 'stack small' },
      st.lastRun
        ? h('p', { class: st.lastOk ? 'ok' : 'danger' }, t('Letzte Sicherung: {0}{1}{2}', time(st.lastRun), st.lastOk ? '' : t(' – mit Fehlern'), st.running ? t(' · läuft gerade …') : ''))
        : h('p', { class: 'muted' }, t('Noch keine Sicherung erstellt.')),
      (st.targets || []).map(tv => h('div', { class: tv.ok ? '' : 'danger' }, `${tv.ok ? '✓' : '✗'} ${tv.target}: ${tv.message}`)));
    const save = async () => {
      const r = await run(() => api('/backup', { method: 'PUT', body: { settings: s, smbPassword: smbPw.value || null, clearSmbPassword: false } }), t('Sicherungs-Einstellungen gespeichert.'));
      if (r) load();
    };
    const test = async target => {
      const r = await run(() => api('/backup/test', { method: 'POST', body: { target } }));
      if (r) toast(r.message, r.ok ? 'ok' : 'error', 10000);
    };
    const usbInfo = !u.available ? null
      : !u.ruleInstalled
        ? h('p', { class: 'warn small' }, t('USB-Stick: die automatische Einbindung ist auf diesem Pi noch nicht eingerichtet. Einmalig per SSH ausführen: '), h('code', {}, u.setupCommand))
        : h('p', { class: `small ${u.mounted ? 'ok' : 'muted'}` }, u.mounted ? t('USB-Stick erkannt und eingebunden.') : t('Kein USB-Stick eingesteckt (FAT32, exFAT oder ext4).'));
    fill(body, 
      h('p', { class: 'muted small' }, t('Einmal täglich: Einstellungen, Verlauf (history.db), Steuerdaten, Benchmark-Ergebnisse und Sicherungen der Miner-Einstellungen als geprüftes Archiv (SHA-256, integrity_check). Passwörter werden nie mitgesichert.')),
      checkInput(s, 'enabled', t('Tägliche Sicherung')),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, t('ab Uhrzeit (Stunde)')), numInput(s, 'hour')),
        h('div', {}, h('label', {}, t('im Datenordner behalten')), numInput(s, 'localKeep')),
        h('div', {}, h('label', {}, t('auf Ordner/USB/NAS behalten')), numInput(s, 'keep'))),
      h('h3', {}, t('Ordner oder USB-Stick')),
      checkInput(s.folder, 'enabled', t('Zusätzlich in einen Ordner sichern')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Zielordner (vollständiger Pfad)')), text(s.folder, 'path', u.available ? u.folder : t('D:\\Sicherungen\\BitaxeTuner')))),
      u.available ? h('div', { class: 'row' }, h('button', { class: 'btn small', onclick: () => { s.folder.enabled = true; s.folder.path = u.folder; save(); } }, t('USB-Stick am Pi verwenden'))) : null,
      usbInfo,
      h('h3', {}, t('Netzlaufwerk / NAS (SMB)')),
      checkInput(s.smb, 'enabled', t('Zusätzlich auf eine Freigabe sichern')),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, t('Server (Name oder IP)')), text(s.smb, 'server', t('nas oder 192.168.1.10'))),
        h('div', {}, h('label', {}, t('Freigabe')), text(s.smb, 'share', 'backup')),
        h('div', {}, h('label', {}, t('Unterordner')), text(s.smb, 'folder', 'BitaxeTuner')),
        h('div', {}, h('label', {}, t('Benutzer')), text(s.smb, 'user')),
        h('div', {}, h('label', {}, t('Passwort')), smbPw),
        h('div', {}, h('label', {}, t('Domäne (meist leer)')), text(s.smb, 'domain'))),
      h('div', { class: 'row' },
        h('button', { class: 'btn primary', onclick: save }, t('Speichern')),
        h('button', { class: 'btn', onclick: () => test('folder') }, t('Ordner testen')),
        h('button', { class: 'btn', onclick: () => test('smb') }, t('Netzlaufwerk testen')),
        h('button', {
          class: 'btn', onclick: async () => {
            toast(t('Sicherung läuft …'), 'info');
            const r = await run(() => api('/backup/run', { method: 'POST', body: {} }));
            if (r) { toast(r.lastOk ? t('Sicherung erstellt.') : t('Sicherung mit Fehlern – siehe Status.'), r.lastOk ? 'ok' : 'error'); load(); }
          },
        }, t('Jetzt sichern'))),
      h('h3', {}, t('Status')),
      statusBox,
      d.files.length
        ? h('div', { class: 'table-wrap' }, h('table', {},
            h('thead', {}, h('tr', {}, [t('Sicherung im Datenordner'), t('Größe'), ''].map(x => h('th', {}, x)))),
            h('tbody', {}, d.files.map(f => h('tr', {},
              h('td', { class: 'mono small' }, f.name), h('td', { class: 'num' }, mb(f.size)),
              h('td', {}, h('a', { class: 'btn small', href: `/api/v1/backup/files/${encodeURIComponent(f.name)}`, download: f.name }, t('Herunterladen'))))))))
        : null);
  };
  load();
  return h('div', { class: 'card stack' }, h('h2', {}, t('Sicherung')), body);
}

/**
 * Einstellungen eines Miners auf andere übertragen: Pool (Wallet, Worker-Name bleibt) und Lüfter/Temperatur.
 * Frequenz/Spannung nie. Erst Vorschau alt → neu je Miner, dann Bestätigung; vorher wird jeder Ziel-Miner gesichert.
 */
function copySettingsCard(devices) {
  const card = h('div', { class: 'card stack' }, h('h2', {}, t('Einstellungen übertragen')));
  if (devices.length < 2) return null;
  const src = h('select', {}, devices.map(d => h('option', { value: d.id }, d.name)));
  const pool = h('input', { type: 'checkbox', checked: true }), fan = h('input', { type: 'checkbox' });
  const targetBoxes = h('div', { class: 'row', style: 'flex-wrap:wrap;gap:6px 16px' });
  const result = h('div', { class: 'stack' });
  const renderTargets = () => fill(targetBoxes, devices.filter(d => d.id !== src.value).map(d =>
    h('label', { class: 'check' }, h('input', { type: 'checkbox', value: d.id, checked: true }), ' ', d.name)));
  src.addEventListener('change', () => { renderTargets(); fill(result); });
  renderTargets();
  const request = () => ({
    source: src.value,
    targets: [...targetBoxes.querySelectorAll('input:checked')].map(i => i.value),
    groups: [pool.checked ? 'pool' : null, fan.checked ? 'fan' : null].filter(Boolean),
  });
  const table = rows => rows.map(r => h('div', { class: 'stack' },
    h('b', {}, r.name),
    r.error ? h('p', { class: 'danger small' }, r.error)
      : r.changes.length === 0 ? h('p', { class: 'muted small' }, t('Keine Unterschiede.'))
      : h('div', { class: 'table-wrap' }, h('table', {},
          h('thead', {}, h('tr', {}, [t('Bereich'), t('Einstellung'), t('Aktuell'), t('Neu')].map(x => h('th', {}, x)))),
          h('tbody', {}, r.changes.map(c => h('tr', {}, h('td', {}, c.group), h('td', {}, c.label),
            h('td', { class: 'mono' }, c.current), h('td', { class: 'mono' }, c.next))))))));
  const apply = h('button', {
    class: 'btn primary', disabled: true, onclick: async () => {
      const body = request();
      const count = result.querySelectorAll('tbody tr').length;
      if (!await confirmBox(t('Einstellungen übertragen'),
        t('{0} Änderung(en) an {1} Miner(n) setzen?\n\nVorher wird jeder Miner gesichert. Bei Pool-Änderungen startet der Miner neu. Frequenz und Spannung bleiben unverändert.', count, body.targets.length),
        t('Übertragen'))) return;
      const r = await run(() => api('/devices/copy', { method: 'POST', body }));
      if (!r) return;
      fill(result, table(r));
      apply.disabled = true;
      const failed = r.filter(x => x.error).length;
      toast(failed ? t('Übertragen, {0} Miner mit Fehler – siehe Liste.', failed) : t('Einstellungen übertragen.'), failed ? 'error' : 'ok', 8000);
    },
  }, t('Übertragen …'));
  const preview = h('button', {
    class: 'btn', onclick: async () => {
      const r = await run(() => api('/devices/copy/preview', { method: 'POST', body: request() }));
      if (!r) return;
      fill(result, table(r));
      apply.disabled = !r.some(x => !x.error && x.changes.length);
    },
  }, t('Vorschau'));
  fill(card, h('h2', {}, t('Einstellungen übertragen')),
    h('p', { class: 'muted small' }, t('Pool und Lüfter eines Miners auf andere übernehmen. Beim Pool-Benutzer wird nur das Wallet übernommen, der Worker-Name jedes Miners bleibt. Frequenz und Spannung werden nie übertragen.')),
    h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Von')), src)),
    h('div', { class: 'row', style: 'gap:16px' },
      h('label', { class: 'check' }, pool, ' ', t('Pool und Fallback-Pool')),
      h('label', { class: 'check' }, fan, ' ', t('Lüfter und Zieltemperatur'))),
    h('div', {}, h('label', {}, t('Auf')), targetBoxes),
    h('div', { class: 'row' }, preview, apply),
    result);
  return card;
}

/** Server-Update: prüfen, Hinweise je Installationsart, Installation per Klick (Pi/Linux-Paket, Windows-Dienst). */
function updateCard() {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  const kinds = {
    Docker: t('Docker: Update mit „docker compose pull && docker compose up -d“.'),
    LinuxPackage: t('Raspberry Pi / Linux-Paket: Installation per Klick, die bisherige Version bleibt als Rückfall erhalten.'),
    WindowsService: t('Windows-Dienst: Installation per Klick (stilles Setup, der Dienst startet neu).'),
    Manual: t('Von Hand gestartet: neues Paket selbst installieren.'),
  };
  const render = u => fill(body, 
    h('p', {}, t('Installiert: {0}', u.current) + (u.latest ? t(' · verfügbar: {0}', u.latest) : '') + (u.message ? ` · ${u.message}` : '')),
    h('p', { class: 'muted small' }, t('Zuletzt geprüft: {0} · nächste automatische Prüfung: {1}', u.lastCheck ? time(u.lastCheck) : t('noch nie'),
      u.nextCheck ? time(u.nextCheck) : t('ausgeschaltet'))),
    h('p', { class: 'muted small' }, kinds[u.kind] || ''),
    u.notes ? h('div', { class: 'log', style: 'height:auto;max-height:180px' }, u.notes) : null,
    h('div', { class: 'row' },
      h('button', { class: 'btn', onclick: async () => { const r = await run(() => api('/admin/update/check', { method: 'POST', body: {} })); if (r) load(); } }, t('Nach Updates suchen')),
      u.latest && u.canInstall ? h('button', {
        class: 'btn primary', onclick: async () => {
          if (!await confirmBox(t('Server aktualisieren'), t('BitaxeTuner-Server {0} installieren?\n\nDie Datei wird gegen die veröffentlichte SHA-256-Prüfsumme geprüft. Laufende Benchmarks werden gestoppt (Einstellungen wiederhergestellt), danach startet der Server neu. Die Seite verbindet sich anschließend von selbst wieder.', u.latest), t('Installieren'))) return;
          const r = await run(() => api('/admin/update/install', { method: 'POST', body: {} }));
          if (r) toast(r.message, 'ok', 15000);
        },
      }, t('Update {0} installieren', u.latest)) : null));
  const load = () => api('/admin/update').then(u => { S.update = u; render(u); }).catch(e => toast(e.message, 'error'));
  load();
  return h('div', { class: 'card stack' }, h('h2', {}, t('Server-Update')), body);
}

boot().catch(e =>mount(h('div', { class: 'card danger' }, t('Server nicht erreichbar: '), e.message)));
