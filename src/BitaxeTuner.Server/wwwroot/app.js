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
  const nav = { overview: t('Übersicht'), compare: t('Vergleich'), fans: t('Lüfter & Anzeige'), tax: t('Steuer'), reports: t('Berichte'), journal: t('Protokoll'), settings: t('Einstellungen') };
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
  S.role = session.role; S.csrf = session.csrf; S.viewGroups = session.groups || null;
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
    if (!r) return;
    const session = await api('/session').catch(() => ({}));
    S.role = r.role; S.csrf = r.csrf; S.viewGroups = session.groups || null; started();
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
  // Ansicht-Zugang mit Gruppen: in der Kopfzeile sichtbar, welche Gruppen freigegeben sind
  $('#role').textContent = isAdmin() ? 'Admin' : S.viewGroups?.length ? t('Nur ansehen: {0}', S.viewGroups.join(', ')) : t('Nur ansehen');
  document.querySelectorAll('[data-admin]').forEach(e => { e.hidden = !isAdmin(); });
  startEvents();
  route();
  if (isAdmin()) refreshUpdateInfo();
  if (isAdmin()) api('/admin/https').then(r => { S.https = r; }).catch(() => {});
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
  if (view === 'journal' && isAdmin()) return renderJournal();
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

/** Link zur Weboberfläche des Miners (AxeOS) – auf Karten, die selbst Links sind, als <span> wie poolLinkIcon. */
function webUiIcon(d) {
  if (!d.webUrl) return null;
  const open = e => { e.preventDefault(); e.stopPropagation(); window.open(d.webUrl, '_blank', 'noopener,noreferrer'); };
  return h('span', {
    class: 'pill gray web-link', role: 'link', tabindex: '0', title: t('AxeOS von {0} öffnen', d.name), 'aria-label': t('AxeOS von {0} öffnen', d.name),
    onclick: open, onkeydown: e => { if (e.key === 'Enter' || e.key === ' ') open(e); },
  }, 'AxeOS ↗');
}

/** Gehört der Miner zur Gruppe? (Groß-/Kleinschreibung egal; leere Gruppe = alle) */
const inGroup = (d, g) => !g || (d.groups || []).some(x => x.toLowerCase() === g.toLowerCase());

/** Gruppen-Chips (Übersicht): Auswahl je Browser gemerkt. */
function groupChips(groups, devices, onPick) {
  if (!groups?.length) return null;
  // Die Übersicht wird bei jedem Messwert neu aufgebaut – schon beim Drücken reagieren, sonst geht der Klick verloren
  const pick = g => { if ((S.group || '') === g) return; S.group = g; localStorageSet('group', g); onPick(); };
  const chip = (g, label) => h('button', { type: 'button', class: 'seg-btn chip' + ((S.group || '') === g ? ' on' : ''), 'aria-pressed': String((S.group || '') === g),
    onpointerdown: () => pick(g), onclick: () => pick(g) }, label);
  return h('div', { class: 'row', role: 'group', 'aria-label': t('Gruppen') },
    chip('', t('Alle ({0})', devices.length)), groups.map(g => chip(g, `${g} (${devices.filter(d => inGroup(d, g)).length})`)));
}

/** Einmalige Frage: aus dem Pool-Benutzer erkannte Wallet-Adressen bei mempool.space/Blockchair abfragen? */
function walletConsentBanner() {
  const answer = async allow => {
    if (await run(() => api('/wallet-consent', { method: 'POST', body: { allow } }), allow ? t('Wallet-Abfrage erlaubt.') : t('Keine Wallet-Abfrage.'))) {
      if (S.status) S.status.walletConsentNeeded = false;
      route();
    }
  };
  return h('div', { class: 'banner stack' },
    h('b', {}, t('Wallet-Guthaben abfragen?')),
    h('p', { class: 'small' }, t('Deine Miner melden eine Wallet-Adresse im Pool-Benutzer. Soll BitaxeTuner Guthaben und Eingänge dieser Adressen bei mempool.space (BTC) bzw. Blockchair (BCH) abfragen? Dabei sehen diese Dienste die Adresse und deine IP-Adresse. Ohne Zustimmung wird nichts abgefragt; ändern kannst du das jederzeit in den Einstellungen.')),
    h('div', { class: 'row' },
      h('button', { class: 'btn primary', onclick: () => answer(true) }, t('Erlauben')),
      h('button', { class: 'btn', onclick: () => answer(false) }, t('Nein danke'))));
}

function renderOverview() {
  const s = S.status;
  const tv = s.totals;
  const chart = h('canvas');
  if (S.group === undefined) S.group = localStorageGet('group') || '';
  if (S.group && !(s.groups || []).some(g => g.toLowerCase() === S.group.toLowerCase())) S.group = '';
  const shown = s.devices.filter(d => inGroup(d, S.group));
  // Summen der gewählten Gruppe (Kosten anteilig nach Leistung)
  const gOn = shown.filter(d => d.online);
  const gHash = gOn.reduce((a, d) => a + (d.hashrate || 0), 0), gPower = gOn.reduce((a, d) => a + (d.wallPower ?? d.power ?? 0), 0);
  const allPower = tv.wallPower ?? tv.power;
  const groupCard = S.group ? h('div', { class: 'card row' },
    h('b', {}, S.group), h('span', { class: 'muted' }, t('{0} von {1} online', gOn.length, shown.length)),
    h('span', {}, hash(gHash)), h('span', {}, t('{0} W', n(gPower, 1))),
    h('span', {}, gHash > 0 ? t('{0} J/TH', n(gPower / (gHash / 1000), 2)) : '–'),
    tv.costPerDay != null && allPower > 0 ? h('span', { class: 'muted' }, t('≈ {0} {1} pro Tag', n(tv.costPerDay * gPower / allPower, 2), tv.currency)) : null) : null;
  const devs = shown.map(d => h('a', { class: 'card device', href: `#/device/${d.id}` },
    h('div', { class: 'head' }, h('span', { class: `dot ${dotClass(d)}` }), h('b', {}, d.name),
      d.benchmark?.running ? h('span', { class: 'pill' }, t('Benchmark')) : null,
      d.soak ? h('span', { class: 'pill' }, t('Dauertest')) : null,
      d.simulated ? h('span', { class: 'pill gray' }, t('Simulation')) : null,
      webUiIcon(d),
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
    // Ansicht-Zugang mit Gruppen: kein Gesamtverlauf über alle Miner (der Server liefert ihn dann nicht)
    S.viewGroups?.length ? null : h('div', { class: 'card' }, h('div', { class: 'chart-head' }, h('h3', {}, t('Hashrate gesamt')), h('span', { class: 'muted small' }, 'live')), h('div', { class: 'chart' }, chart)),
    s.whatsNew ? whatsNewCard(s.whatsNew) : null,
    s.onboarding ? onboardingCard(s.onboarding) : null,
    groupChips(s.groups, s.devices, renderOverview),
    groupCard,
    s.devices.length ? h('div', { class: 'devices' }, devs) : h('div', { class: 'card muted' }, t('Noch keine Miner eingetragen.'), isAdmin() ? t(' Unter Einstellungen → Geräte hinzufügen.') : ''),
    s.plugs?.length ? plugOverviewCard(s.plugs) : null,
    isAdmin() && s.configSaveError ? h('div', { class: 'banner danger' },
      h('b', {}, t('Einstellungen konnten nicht gespeichert werden: {0}', s.configSaveError)),
      h('p', { class: 'small' }, t('Änderungen gehen beim nächsten Neustart verloren. Bitte Speicherplatz und Schreibrechte des Datenordners prüfen.'))) : null,
    isAdmin() && s.walletConsentNeeded ? walletConsentBanner() : null,
    isAdmin() && S.https && !S.https.enabled && S.https.configurable && localStorageGet('httpsHint') !== 'later' ? httpsBanner() : null,
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
  if (!S.viewGroups?.length) drawChart(chart, [{ points: s.history.map(p => [p[0], p[1]]), color: cssVar('--ok'), format: hash }], []);
}

/** Monats- und Jahresberichte: Zusammenfassung, druckbare Seite (PDF über „Drucken“), CSV, Push. */
/** Kategorien des dauerhaften Protokolls (Schlüssel wie Core/Monitoring/EventCategories.cs). */
const JOURNAL_CATS = () => [['tuning', t('Frequenz/Spannung')], ['benchmark', t('Benchmark')], ['soak', t('Dauertest')],
  ['automation', t('Automatik/Watchdog')], ['fans', t('Lüfter')], ['connection', t('Verbindung')], ['settings', t('Einstellungen/Profil')],
  ['system', t('Server/System')], ['other', t('Sonstige')]];

/**
 * Protokoll: dauerhaft in history.db (mindestens 30 Tage, übersteht Neustarts und Updates) – wer hat wann welche
 * Frequenz/Spannung gesetzt, Benchmarks, Lüfterregelung, Verbindungen. Filter je Browser gemerkt.
 */
function renderJournal() {
  let saved = {};
  try { saved = JSON.parse(localStorageGet('journal') || '{}'); } catch { /* egal */ }
  const f = { range: saved.range || '24h', device: saved.device || '', cats: new Set(saved.cats || []), q: '' };
  const store = () => localStorageSet('journal', JSON.stringify({ range: f.range, device: f.device, cats: [...f.cats] }));
  const devices = S.status?.devices || [];
  const table = h('div', { class: 'table-wrap cmp-wrap' }, h('p', { class: 'muted', style: 'padding:8px' }, t('Lade …')));
  const count = h('span', { class: 'muted small' });
  const query = () => new URLSearchParams({ range: f.range, device: f.device, cats: [...f.cats].join(','), q: f.q });
  let timer;
  const load = async () => {
    const d = await api(`/journal?${query()}`).catch(e => { fill(table, h('p', { class: 'danger', style: 'padding:8px' }, e.message)); return null; });
    if (!d) return;
    const label = Object.fromEntries(JOURNAL_CATS());
    count.textContent = d.truncated ? t('neueste {0} Einträge', d.entries.length) : t('{0} Einträge', d.entries.length);
    fill(table, d.entries.length
      ? h('table', { class: 'journal' },
          h('thead', {}, h('tr', {}, [t('Zeit'), t('Miner'), t('Kategorie'), t('Meldung')].map(x => h('th', {}, x)))),
          h('tbody', {}, d.entries.flatMap(e => {
            // Klick auf eine Zeile: Erklärung „Was bedeutet das?“ / „Was kannst du tun?“ darunter auf- und zuklappen
            const x = d.explanations?.[e.explain];
            const detail = h('tr', { class: 'explain', hidden: true }, h('td', { colspan: 4 }, x ? h('div', { class: 'stack' },
              h('b', {}, x.title),
              h('div', {}, h('span', { class: 'muted' }, t('Was bedeutet das? ')), x.meaning),
              h('div', {}, h('span', { class: 'muted' }, t('Was kannst du tun? ')), x.action)) : null));
            const toggle = () => { detail.hidden = !detail.hidden; row.setAttribute('aria-expanded', String(!detail.hidden)); };
            const row = h('tr', { class: 'clickable', tabindex: '0', role: 'button', 'aria-expanded': 'false', title: t('Klicken für eine Erklärung'),
              onclick: ev => { if (!ev.target.closest('a')) toggle(); }, onkeydown: ev => { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); toggle(); } } },
              h('td', { class: 'num nowrap' }, new Date(e.time).toLocaleString(LOCALE)),
              h('td', { class: 'nowrap' }, e.device ? h('a', { href: `#/device/${e.device}` }, e.name) : h('span', { class: 'muted' }, e.name)),
              h('td', {}, h('span', { class: `pill cat-${e.category}` }, label[e.category] || e.category)),
              h('td', { class: 'msg' }, e.message, ' ', h('span', { class: 'muted small', 'aria-hidden': 'true' }, 'ⓘ')));
            return [row, detail];
          })))
      : h('p', { class: 'muted', style: 'padding:8px' }, t('Keine Einträge für diese Auswahl.')));
  };
  const chips = h('div', { class: 'row', role: 'group', 'aria-label': t('Kategorien') }, JOURNAL_CATS().map(([k, l]) => {
    const b = h('button', { type: 'button', class: 'seg-btn chip' + (f.cats.has(k) ? ' on' : ''), 'aria-pressed': String(f.cats.has(k)), onclick: () => {
      f.cats.has(k) ? f.cats.delete(k) : f.cats.add(k);
      b.classList.toggle('on', f.cats.has(k)); b.setAttribute('aria-pressed', String(f.cats.has(k)));
      store(); load();
    } }, l);
    return b;
  }));
  mount(h('div', { class: 'stack' }, h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h2', {}, t('Protokoll')), h('span', { class: 'spacer' }), count,
      h('a', { class: 'btn small', href: '#', onclick: e => { e.preventDefault(); location.href = `/api/v1/journal?${query()}&format=csv`; } }, t('CSV exportieren'))),
    h('p', { class: 'muted small' }, t('Frequenz/Spannung (mit Quelle), Benchmarks, Dauertests, Automatik, Lüfterregelung, Verbindungen und Server-Ereignisse. Dauerhaft gespeichert (mindestens 30 Tage) – läuft nach Neustarts und Updates weiter und ist in jeder Sicherung enthalten.')),
    h('div', { class: 'cmp-toolbar' },
      h('select', { style: 'width:auto', 'aria-label': t('Zeitraum'), onchange: e => { f.range = e.target.value; store(); load(); } },
        [['24h', t('24 h')], ['7d', t('7 Tage')], ['30d', t('30 Tage')]].map(([v, l]) => h('option', { value: v, selected: f.range === v }, l))),
      h('select', { style: 'width:auto;max-width:240px', 'aria-label': t('Miner'), onchange: e => { f.device = e.target.value; store(); load(); } },
        h('option', { value: '' }, t('alle Miner und Server')), h('option', { value: 'server', selected: f.device === 'server' }, t('nur Server')),
        devices.map(d => h('option', { value: d.id, selected: f.device === d.id }, d.name))),
      h('input', { type: 'search', placeholder: t('Suchen …'), 'aria-label': t('Suchen'), style: 'max-width:240px',
        oninput: e => { f.q = e.target.value; clearTimeout(timer); timer = setTimeout(load, 300); } })),
    chips,
    h('p', { class: 'muted small' }, t('Keine Kategorie gewählt = alle. Ein Klick auf einen Eintrag zeigt, was er bedeutet und was du tun kannst.')),
    table)));
  load();
}

async function renderReports() {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  mount(h('div', { class: 'stack' }, h('div', { class: 'card stack' }, h('h2', {}, t('Berichte')), body)));
  let list;
  try { list = await api('/reports'); } catch (e) { fill(body, h('p', { class: 'danger' }, e.message)); return; }
  if (!list.periods.length) { fill(body, h('p', { class: 'muted' }, t('Noch keine Messwerte für einen Bericht.'))); return; }
  const label = p => p.length === 4 ? t('Jahr {0}', p) : new Date(`${p}-01T00:00:00`).toLocaleDateString(LOCALE, { month: 'long', year: 'numeric' });
  const sel = h('select', { style: 'width:auto' }, list.periods.map(p => h('option', { value: p }, label(p))));
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

/** Nach einem Update: kurze Einführung nur in die neuen Funktionen anbieten (einmal je Version). */
function whatsNewCard(w) {
  const seen = async () => { if (await run(() => api('/whatsnew/seen', { method: 'POST', body: {} }))) { S.status.whatsNew = null; S.whatsNewOpen = false; renderOverview(); } };
  if (!S.whatsNewOpen)
    return h('div', { class: 'banner row' },
      h('span', { style: 'flex:1' }, t('BitaxeTuner wurde auf {0} aktualisiert. Kurze Einführung in die {1} neuen Funktionen?', w.version, w.features.length)),
      h('button', { class: 'btn primary small', onclick: () => { S.whatsNewOpen = true; renderOverview(); } }, t('Zeigen')),
      h('button', { class: 'btn small', onclick: seen }, t('Nein danke')));
  return h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h3', {}, t('Neu in {0}', w.version)), h('span', { class: 'spacer' }),
      h('button', { class: 'btn small primary', onclick: seen }, t('Verstanden'))),
    h('div', { class: 'stack' }, w.features.map(f => h('div', { class: 'row', style: 'align-items:flex-start' },
      h('span', { class: 'ok', style: 'font-size:16px;line-height:1.2' }, '★'),
      h('div', { style: 'flex:1' }, h('b', {}, t(f.title)), h('div', { class: 'small muted' }, t(f.text))),
      f.section ? h('a', { class: 'btn small', href: '#/settings', onclick: () => { S.settingsSection = t(f.section); } }, t('Ansehen')) : null))));
}

/** Einführung „Erste Schritte“: Checkliste, die sich selbst abhakt; Links springen zum passenden Einstellungs-Abschnitt. */
function onboardingCard(steps) {
  const done = steps.filter(x => x.done).length;
  const hide = async () => { if (await run(() => api('/onboarding', { method: 'POST', body: { show: false } }))) { S.status.onboarding = null; renderOverview(); } };
  return h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h3', {}, t('Erste Schritte')), h('span', { class: 'muted small' }, t('{0} von {1} erledigt', done, steps.length)),
      h('span', { class: 'spacer' }), h('button', { class: 'btn small', onclick: hide }, done === steps.length ? t('Fertig – ausblenden') : t('Ausblenden'))),
    h('p', { class: 'muted small' }, t('Willkommen bei BitaxeTuner! Diese Schritte richten das Wichtigste ein – jeder hakt sich selbst ab. Später wieder einblenden: Einstellungen → Allgemein.')),
    h('div', { class: 'stack' }, steps.map((x, i) => h('div', { class: 'row', style: 'align-items:flex-start' },
      h('span', { class: x.done ? 'ok' : 'muted', style: 'font-size:18px;line-height:1' }, x.done ? '✔' : String(i + 1)),
      h('div', { style: 'flex:1' }, h('b', {}, t(x.title)), h('div', { class: 'small muted' }, t(x.text))),
      x.section ? h('a', { class: `btn small ${x.done ? '' : 'primary'}`, href: '#/settings', onclick: () => { S.settingsSection = t(x.section); } }, x.done ? t('Ansehen') : t('Einrichten')) : null))));
}

/** Smart Plugs in der Übersicht: Leistung an der Steckdose, bei Miner-Plugs die Differenz zu AxeOS. */
function plugOverviewCard(plugs) {
  const roleText = { miners: t('Miner'), other: t('Nebenverbraucher'), total: t('Gesamtmessung') };
  return h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h3', {}, t('Smart Plugs')), h('span', { class: 'spacer' }),
      isAdmin() ? h('a', { class: 'btn small', href: '#/settings', onclick: () => { S.settingsSection = t('Smart Plugs'); } }, t('Einstellungen')) : null),
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

/** Ansicht der Vergleichsseite (Filter, Sortierung, Spaltengruppen) – je Browser gemerkt. */
function compareView() {
  if (S.cmp) return S.cmp;
  const def = { q: '', status: 'all', model: '', group: '', sort: 'name', dir: 1, groups: { power: true, temp: true, shares: true, system: false } };
  let saved = {};
  try { saved = JSON.parse(localStorageGet('compareView') || '{}'); } catch { /* egal */ }
  S.cmp = { ...def, ...saved, q: '', groups: { ...def.groups, ...(saved.groups || {}) } };
  return S.cmp;
}
function saveCompareView() {
  const { status, model, group, sort, dir, groups } = S.cmp;
  localStorageSet('compareView', JSON.stringify({ status, model, group, sort, dir, groups }));
}

/** „1.23G“ → 1,23·10⁹ (Best Diff kommt als Text). */
function parseDiff(v) {
  if (v == null || v === '') return null;
  const m = String(v).trim().match(/^([\d.,]+)\s*([kKMGTPE]?)$/);
  if (!m) return null;
  const f = { '': 1, k: 1e3, K: 1e3, M: 1e6, G: 1e9, T: 1e12, P: 1e15, E: 1e18 }[m[2]];
  const x = parseFloat(m[1].replace(',', '.'));
  return Number.isNaN(x) ? null : x * f;
}

/** Spalten des Vergleichs: value = Zahl zum Sortieren/Balken/Bestwert, text = Anzeige, best = 'max' | 'min'. */
function compareColumns() {
  const pct = (a, b) => a != null && b ? a / b * 100 : null;
  const rej = d => d.sharesAccepted != null && d.sharesAccepted + d.sharesRejected > 0 ? d.sharesRejected / (d.sharesAccepted + d.sharesRejected) * 100 : null;
  return [
    { key: 'hashrate', group: 'power', label: t('Hashrate'), value: d => d.hashrate, text: d => hash(d.hashrate), best: 'max', bar: true },
    { key: 'expected', group: 'power', label: t('Soll erreicht'), value: d => pct(d.hashrate, d.expectedHashrate), text: d => pct(d.hashrate, d.expectedHashrate) != null ? `${n(pct(d.hashrate, d.expectedHashrate), 0)} %` : '–', best: 'max' },
    { key: 'power', group: 'power', label: t('Leistung'), value: d => d.power, text: d => d.power != null ? t('{0} W', n(d.power, 1)) : '–', bar: true },
    { key: 'efficiency', group: 'power', label: t('Effizienz'), value: d => d.efficiency || null, text: d => d.efficiency ? t('{0} J/TH', n(d.efficiency, 2)) : '–', best: 'min' },
    { key: 'clock', group: 'power', label: t('Frequenz / Spannung'), value: d => d.frequency, text: d => d.frequency != null ? t('{0} MHz / {1} mV', d.frequency, d.voltage ?? '–') : '–' },
    { key: 'temp', group: 'temp', label: t('ASIC-Temperatur'), value: d => d.temp, text: d => d.temp != null ? t('{0} °C', n(d.temp, 1)) : '–', best: 'min' },
    { key: 'vrTemp', group: 'temp', label: t('VR-Temperatur'), value: d => d.vrTemp, text: d => d.vrTemp != null ? t('{0} °C', n(d.vrTemp, 0)) : '–', best: 'min' },
    { key: 'fan', group: 'temp', label: t('Lüfter'), value: d => d.fanRpm, text: d => d.fanRpm != null ? t('{0} rpm ({1} %)', d.fanRpm, d.fanPercent ?? '–') : '–' },
    { key: 'shares', group: 'shares', label: t('Shares'), value: d => d.sharesAccepted, text: d => d.sharesAccepted ?? '–' },
    { key: 'rejected', group: 'shares', label: t('abgelehnt'), value: rej, text: d => rej(d) != null ? `${n(rej(d), 2)} %` : '–', best: 'min' },
    { key: 'errorPercent', group: 'shares', label: t('Fehlerrate'), value: d => d.errorPercent, text: d => d.errorPercent != null ? `${n(d.errorPercent, 2)} %` : '–', best: 'min' },
    { key: 'bestDiff', group: 'shares', label: t('Best Diff'), value: d => parseDiff(d.bestDiff), text: d => d.bestDiff || '–', best: 'max' },
    { key: 'poolDifficulty', group: 'shares', label: t('Pool-Difficulty'), value: d => d.poolDifficulty, text: d => diffText(d.poolDifficulty) },
    { key: 'pool', group: 'shares', label: t('Pool'), value: d => d.pool || '', text: d => d.pool || '–' },
    { key: 'uptime', group: 'system', label: t('Laufzeit'), value: d => d.uptimeSeconds, text: d => dur(d.uptimeSeconds) },
    { key: 'firmware', group: 'system', label: t('Firmware'), value: d => d.firmwareText || '', text: d => d.firmwareText || '–' },
    { key: 'profile', group: 'system', label: t('Profil'), value: d => d.profile || '', text: d => d.profile || '–' },
    { key: 'automation', group: 'system', label: t('Automatik'), value: d => d.automation || '', text: d => d.automation || t('keine Automatik') },
  ];
}

/**
 * Vergleichsbericht zum Ausdrucken: Miner, Zeitraum, Werte und Diagramme wählen → druckbare Seite vom Server
 * (Schlüssel wie Core/Reports/CompareReports.cs). Auswahl je Browser gemerkt.
 */
const REPORT_VALUES = () => [
  ['now', t('Aktuell'), [['model', t('Modell / Profil')], ['hashrate', t('Hashrate')], ['expected', t('Soll erreicht')], ['power', t('Leistung')],
    ['efficiency', t('Effizienz')], ['clock', t('Frequenz / Spannung')], ['temp', t('ASIC-Temperatur')], ['vrTemp', t('VR-Temperatur')],
    ['fan', t('Lüfter')], ['errorPercent', t('Fehlerrate')], ['shares', t('Shares')], ['bestDiff', t('Best Diff')],
    ['poolDifficulty', t('Pool-Difficulty')], ['uptime', t('Laufzeit')], ['firmware', t('Firmware')]]],
  ['range', t('Über den Zeitraum'), [['avgHash', t('Ø Hashrate')], ['avgPower', t('Ø Leistung')], ['avgEff', t('Ø Effizienz')], ['avgTemp', t('Ø Temperatur')],
    ['tempPerWatt', t('Ø Temperatur je Watt')], ['avgVrTemp', t('Ø VR-Temperatur')], ['avgFan', t('Ø Lüfter')], ['availability', t('Verfügbarkeit')],
    ['tuning', t('Tuning-Änderungen')]]],
  ['bench', t('Letzter Benchmark'), [['benchHash', t('Beste Hashrate (Benchmark)')], ['benchEff', t('Beste Effizienz (Benchmark)')], ['maxStable', t('Stabil bis')]]],
];
const REPORT_CHARTS = () => [['hashrate', t('Hashrate')], ['efficiency', t('Effizienz')], ['power', t('Leistung')], ['temp', t('ASIC-Temperatur')],
  ['vrTemp', t('VR-Temperatur')], ['fan', t('Lüfter')]];
const REPORT_PRESETS = {
  standard: { values: ['model', 'hashrate', 'efficiency', 'temp', 'avgHash', 'avgEff', 'availability', 'benchHash', 'benchEff'], charts: ['hashrate', 'efficiency', 'temp'] },
  cooling: { values: ['model', 'clock', 'power', 'temp', 'vrTemp', 'fan', 'avgTemp', 'tempPerWatt', 'avgVrTemp', 'avgFan', 'avgPower', 'firmware'], charts: ['temp', 'vrTemp', 'fan', 'power'] },
};

function compareReportCard(devices) {
  let saved = {};
  try { saved = JSON.parse(localStorageGet('compareReport') || '{}'); } catch { /* egal */ }
  const sel = {
    ids: new Set((saved.ids || []).filter(id => devices.some(d => d.id === id))),
    range: saved.range || '24h',
    values: new Set(saved.values || REPORT_PRESETS.standard.values),
    charts: new Set(saved.charts || REPORT_PRESETS.standard.charts),
  };
  if (!sel.ids.size) devices.slice(0, 2).forEach(d => sel.ids.add(d.id));
  const store = () => localStorageSet('compareReport', JSON.stringify({ ids: [...sel.ids], range: sel.range, values: [...sel.values], charts: [...sel.charts] }));
  const box = h('div', { class: 'stack' });
  const check = (set, key, label, onToggle) => {
    const c = h('input', { type: 'checkbox', checked: set.has(key), onchange: () => { c.checked ? set.add(key) : set.delete(key); onToggle?.(c); store(); } });
    return h('label', { class: 'check' }, c, label);
  };
  const render = () => fill(box,
    h('div', { class: 'row' },
      h('span', { class: 'muted small' }, t('Vorauswahl:')),
      h('button', { class: 'btn small', onclick: () => { sel.values = new Set(REPORT_PRESETS.standard.values); sel.charts = new Set(REPORT_PRESETS.standard.charts); store(); render(); } }, t('Standard')),
      h('button', { class: 'btn small', onclick: () => { sel.values = new Set(REPORT_PRESETS.cooling.values); sel.charts = new Set(REPORT_PRESETS.cooling.charts); store(); render(); } }, t('Kühlung (z. B. für eine Frage in der Community)'))),
    h('div', { class: 'cmp-report-grid' },
      h('div', { class: 'stack' }, h('h3', {}, t('Miner (bis 6)')),
        devices.map(d => check(sel.ids, d.id, d.name, c => {
          if (c.checked && sel.ids.size > 6) { sel.ids.delete(d.id); c.checked = false; toast(t('Höchstens {0} Miner je Bericht.', 6), 'error'); }
        })),
        h('h3', {}, t('Zeitraum')),
        h('select', { style: 'width:auto', onchange: e => { sel.range = e.target.value; store(); } },
          [['24h', t('24 h')], ['7d', t('7 Tage')], ['30d', t('30 Tage')]].map(([v, l]) => h('option', { value: v, selected: sel.range === v }, l))),
        h('h3', {}, t('Diagramme')), REPORT_CHARTS().map(([k, l]) => check(sel.charts, k, l))),
      REPORT_VALUES().map(([g, title, items]) => h('div', { class: 'stack' }, h('h3', {}, title), items.map(([k, l]) => check(sel.values, k, l))))),
    h('div', { class: 'row' },
      h('button', { class: 'btn primary', onclick: () => {
        if (sel.ids.size < 1) return toast(t('Bitte mindestens einen Miner auswählen.'), 'error');
        const q = new URLSearchParams({ ids: [...sel.ids].join(','), range: sel.range, values: [...sel.values].join(','), charts: [...sel.charts].join(',') });
        window.open(`/api/v1/compare/report?${q}`, '_blank', 'noopener');
      } }, t('Bericht öffnen')),
      h('span', { class: 'muted small' }, t('Öffnet eine druckbare Seite – als PDF über „Drucken“. Ohne IP- und Wallet-Adressen.'))));
  render();
  return h('div', { class: 'card stack', id: 'cmp-report', hidden: true }, h('h2', {}, t('Vergleichsbericht')), box);
}

/** Vergleichsseite: Kennzahlen, Filterleiste und Tabelle (je Miner eine Zeile). Neue Messwerte tauschen nur den Inhalt aus. */
function renderCompare() {
  const s = S.status;
  if (!s) { api('/status').then(x => { S.status = x; renderCompare(); }); return; }
  const v = compareView();
  S.compareKpis = h('div', { class: 'tiles' });
  S.compareTable = h('div', { class: 'table-wrap cmp-wrap' });
  S.compareCount = h('span', { class: 'muted small' });
  const changed = () => { saveCompareView(); updateCompare(); };
  const chips = (options, get, set) => h('div', { class: 'seg', role: 'group' }, options.map(([val, label]) => {
    const b = h('button', { type: 'button', class: 'seg-btn' + (get() === val ? ' on' : ''), 'aria-pressed': String(get() === val), onclick: () => {
      set(val);
      [...b.parentNode.children].forEach(x => { const on = x === b; x.classList.toggle('on', on); x.setAttribute('aria-pressed', String(on)); });
      changed();
    } }, label);
    return b;
  }));
  const models = [...new Set(s.devices.map(d => d.model).filter(Boolean))].sort();
  const groupSel = s.groups?.length ? h('select', { style: 'width:auto;max-width:200px', 'aria-label': t('Gruppe'), onchange: e => { v.group = e.target.value; changed(); } },
    h('option', { value: '' }, t('alle Gruppen')), s.groups.map(g => h('option', { value: g, selected: v.group === g }, g))) : null;
  const modelSel = h('select', { style: 'width:auto;max-width:240px', 'aria-label': t('Modell'), onchange: e => { v.model = e.target.value; changed(); } },
    h('option', { value: '' }, t('alle Modelle')), models.map(m => h('option', { value: m, selected: v.model === m }, m)));
  const search = h('input', { type: 'search', value: v.q, placeholder: t('Miner suchen …'), 'aria-label': t('Miner suchen'), style: 'max-width:220px', oninput: e => { v.q = e.target.value; updateCompare(); } });
  const groups = [['power', t('Leistung')], ['temp', t('Temperatur')], ['shares', t('Shares & Pool')], ['system', t('System')]];
  const groupChips = h('div', { class: 'seg', role: 'group', 'aria-label': t('Spalten') }, groups.map(([g, label]) => {
    const b = h('button', { type: 'button', class: 'seg-btn' + (v.groups[g] ? ' on' : ''), 'aria-pressed': String(!!v.groups[g]), onclick: () => {
      v.groups[g] = !v.groups[g];
      b.classList.toggle('on', v.groups[g]); b.setAttribute('aria-pressed', String(v.groups[g]));
      changed();
    } }, label);
    return b;
  }));
  mount(h('div', { class: 'stack' },
    h('div', { class: 'card stack' },
      h('div', { class: 'titlebar' }, h('h2', {}, t('Vergleich')), h('span', { class: 'spacer' }), S.compareCount,
        h('button', { class: 'btn small', onclick: () => { const c = $('#cmp-report'); c.hidden = !c.hidden; if (!c.hidden) c.scrollIntoView({ behavior: 'smooth', block: 'start' }); } }, t('Bericht …'))),
      S.compareKpis,
      h('div', { class: 'cmp-toolbar' },
        search,
        chips([['all', t('alle')], ['online', 'online'], ['offline', 'offline']], () => v.status, x => { v.status = x; }),
        models.length > 1 ? modelSel : null,
        groupSel,
        h('span', { class: 'spacer' }),
        h('span', { class: 'muted small' }, t('Spalten:')), groupChips),
      S.compareTable,
      h('p', { class: 'muted small' }, t('Spaltenüberschrift anklicken zum Sortieren. ★ = bester Wert der angezeigten Miner; Balken bei Hashrate und Leistung = Anteil am höchsten Wert.'))),
    compareReportCard(s.devices),
    isAdmin() ? advisorCard() : null));
  updateCompare();
}

function updateCompare() {
  const s = S.status;
  if (!s || !S.compareTable || !document.body.contains(S.compareTable)) return;
  const v = compareView();
  const q = v.q.trim().toLowerCase();
  const rows = s.devices.filter(d =>
    (v.status === 'all' || (v.status === 'online') === !!d.online) &&
    (!v.model || d.model === v.model) &&
    inGroup(d, v.group) &&
    (!q || `${d.name} ${d.model || ''} ${d.profile || ''}`.toLowerCase().includes(q)));
  const cols = compareColumns().filter(c => v.groups[c.group]);
  const all = compareColumns();
  const sortCol = all.find(c => c.key === v.sort);
  const key = d => v.sort === 'name' ? d.name.toLowerCase() : sortCol ? sortCol.value(d) : null;
  rows.sort((a, b) => {
    const x = key(a), y = key(b);
    if (x == null || x === '') return 1;           // ohne Wert immer ans Ende
    if (y == null || y === '') return -1;
    return (typeof x === 'string' ? x.localeCompare(y) : x - y) * v.dir;
  });

  // Kennzahlen der angezeigten Miner
  const on = rows.filter(d => d.online);
  const sum = f => on.reduce((a, d) => a + (f(d) || 0), 0);
  const hr = sum(d => d.hashrate), pw = sum(d => d.power);
  const eff = on.filter(d => d.efficiency).sort((a, b) => a.efficiency - b.efficiency)[0];
  const hot = on.filter(d => d.temp != null).sort((a, b) => b.temp - a.temp)[0];
  fill(S.compareKpis,
    tile(t('Hashrate'), hash(hr), t('{0} von {1} online', on.length, rows.length), on.length ? 'ok' : 'danger'),
    tile(t('Leistung'), t('{0} W', n(pw, 1)), hr > 0 ? t('Ø {0} J/TH', n(pw / (hr / 1000), 2)) : ''),
    tile(t('Effizientester'), eff ? t('{0} J/TH', n(eff.efficiency, 2)) : '–', eff ? eff.name : ''),
    tile(t('Heißester'), hot ? t('{0} °C', n(hot.temp, 1)) : '–', hot ? hot.name : ''));
  S.compareCount.textContent = rows.length === s.devices.length ? t('{0} Miner', rows.length) : t('{0} von {1} Miner', rows.length, s.devices.length);

  // Bestwerte und Balken nur über die angezeigten, laufenden Miner
  const stats = {};
  for (const c of cols) {
    const vals = on.map(c.value).filter(x => typeof x === 'number' && !Number.isNaN(x));
    stats[c.key] = { max: vals.length ? Math.max(...vals) : null, min: vals.length ? Math.min(...vals) : null, n: vals.length };
  }
  const sortBtn = (k, label) => {
    const active = v.sort === k;
    return h('button', { type: 'button', class: 'th-sort' + (active ? ' on' : ''), onclick: () => {
      // erster Klick: Bester zuerst (bei „weniger ist besser“ aufsteigend), Namen A–Z
      if (v.sort === k) v.dir = -v.dir; else { v.sort = k; v.dir = k === 'name' || all.find(c => c.key === k)?.best === 'min' ? 1 : -1; }
      saveCompareView(); updateCompare();
    } }, label, h('span', { class: 'arrow', 'aria-hidden': 'true' }, active ? (v.dir > 0 ? '▲' : '▼') : '↕'));
  };
  const ariaSort = k => v.sort === k ? (v.dir > 0 ? 'ascending' : 'descending') : null;
  const cell = (c, d) => {
    const val = c.value(d), st = stats[c.key];
    // ★ nur, wenn sich die Werte unterscheiden – sonst wären alle „am besten“
    const isBest = d.online && c.best && st.n > 1 && st.max !== st.min && typeof val === 'number' && val === (c.best === 'max' ? st.max : st.min);
    const width = c.bar && d.online && typeof val === 'number' && st.max > 0 ? Math.max(4, val / st.max * 100) : null;
    return h('td', { class: 'num' + (isBest ? ' best' : '') },
      h('div', { class: 'cmp-val' }, c.text(d), isBest ? h('span', { class: 'star', title: t('bester Wert'), 'aria-label': t('bester Wert') }, ' ★') : null),
      width != null ? h('div', { class: 'cmp-bar', 'aria-hidden': 'true' }, h('span', { style: `width:${width.toFixed(1)}%` })) : null);
  };
  fill(S.compareTable, rows.length
    ? h('table', { class: 'cmp-table' },
        h('thead', {}, h('tr', {},
          h('th', { class: 'sticky', 'aria-sort': ariaSort('name') }, sortBtn('name', t('Miner'))),
          cols.map(c => h('th', { 'aria-sort': ariaSort(c.key) }, sortBtn(c.key, c.label))))),
        h('tbody', {}, rows.map(d => h('tr', { class: d.online ? '' : 'off' },
          h('td', { class: 'sticky' },
            h('div', { class: 'cmp-name' }, h('span', { class: `dot ${dotClass(d)}` }), h('a', { href: `#/device/${d.id}` }, d.name),
              d.webUrl ? h('a', { href: d.webUrl, target: '_blank', rel: 'noopener', class: 'small', title: t('AxeOS von {0} öffnen', d.name) }, '↗') : null),
            h('div', { class: 'muted small' }, d.online ? (d.model || '–') : (d.maintenance ? t('Neustart/Tuning …') : (d.error || 'offline')))),
          cols.map(c => cell(c, d))))))
    : h('p', { class: 'muted' }, t('Kein Miner passt zum Filter.')));
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
                h('p', {}, h('b', {}, t('Vorschlag: {0} MHz / {1} mV', c.frequencyMhz, c.coreVoltageMv)), t(' · {0} · {1} W · {2} J/TH', hash(c.hashrateGh), n(c.powerW, 1), n(c.jth, 2)),
                  c.wallJth != null ? h('span', { class: 'muted' }, t(' · Steckdose ≈ {0} W · {1} J/TH', n(c.wallPowerW, 1), n(c.wallJth, 2))) : null),
                h('p', { class: 'small' },
                  `${signed(c.deltaGh, 0, 'GH/s')} · ${signed(c.deltaW, 1, 'W')} · `,
                  h('b', { class: c.monthlyCostDelta < 0 ? 'ok' : '' }, t('{0} pro Monat', signed(c.monthlyCostDelta, 2, d.currency))),
                  ` · ${c.confidence}`),
                h('div', { class: 'row' },
                  h('button', { class: 'btn small', onclick: () => apply(m, c, false) }, t('Anwenden …')),
                  c.soakPassed ? null : h('button', { class: 'btn small primary', onclick: () => apply(m, c, true) }, t('Anwenden + Dauertest 24 h …'))))
            : null,
          h('p', { class: 'small muted' }, m.note),
          m.wallFactor ? h('p', { class: 'small muted' }, t('Steckdosenwerte hochgerechnet mit dem gemessenen Faktor ×{0} (Smart Plug, letzte 7 Tage); die Kosten pro Monat rechnen damit.', n(m.wallFactor, 2))) : null,
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
  ['live', t('Live')], ['benchmark', t('Benchmark'), true], ['results', t('Ergebnisse')], ['compare', t('Vorher/Nachher')], ['health', t('Gesundheit')],
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
      d.webUrl ? h('a', { class: 'btn small', href: d.webUrl, target: '_blank', rel: 'noopener', title: t('AxeOS von {0} öffnen', d.name) }, t('AxeOS öffnen ↗')) : null,
      h('span', { class: 'pill' }, S.detail.profile.name)),
    d.suggestion && isAdmin() ? suggestionBanner(d) : null,
    h('div', { class: 'tabs' }, TABS().filter(tv => !tv[2] || isAdmin()).map(([k, label]) =>
      h('a', { href: `#/device/${d.id}/${k}`, class: k === tab ? 'active' : null }, label))),
    h('div', {
      id: 'tab', oninput: markTabDirty, onchange: markTabDirty,
      // Voreinstellungen/Zeitplan werden per Knopf bearbeitet – auch das sind ungespeicherte Änderungen
      onclick: e => { if (S.route.tab === 'automation' && e.target.closest('button')) markTabDirty(); },
    })));
  refreshDeviceTab();
}

/**
 * Ungespeicherte Eingaben im Benchmark- oder Automatik-Tab: Neue Daten vom Server (z. B. Automatik-Status) bauen den Tab
 * dann nicht neu auf – sonst wären die Eingaben nach ein paar Sekunden weg (Issue #9). Gespeichert/gestartet → cleanTab().
 */
function markTabDirty() {
  const tab = $('#tab');
  if (tab && ['benchmark', 'automation', 'live'].includes(S.route?.tab)) tab.dataset.dirty = S.route.tab;
}
function cleanTab() {
  const tab = $('#tab');
  if (tab) delete tab.dataset.dirty;
}

function refreshDeviceTab() {
  const tab = $('#tab');
  if (!tab) return;
  if (tab.dataset.dirty === S.route.tab) {
    const progress = $('#bench-progress');
    if (progress) fill(progress, ...benchProgress(summaryOf(S.route.id)?.benchmark));
    return;
  }
  delete tab.dataset.dirty;
  const render = { live: tabLive, benchmark: tabBenchmark, results: tabResults, compare: tabCompare, health: tabHealth, automation: tabAutomation, backups: tabBackups, log: tabLog }[S.route.tab];
  // Protokoll-Tab nicht bei jedem Neuladen neu aufbauen (Live-Log liefe sonst neu an)
  if (S.route.tab === 'log' && $('#miner-log')) { fillAppLog(); return; }
  fill(tab, render());
}

/** Gesundheit: Hinweise der Frühwarnung und die verglichenen Werte (7 Tage gegen 4 Wochen davor). */
function tabHealth() {
  const box = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  api(`/devices/${S.route.id}/health`).then(d => {
    const v = (x, f) => x == null ? '–' : f(x);
    const rows = [
      [t('Temperatur je Watt'), x => t('{0} °C/W', n(x, 2)), 'tempPerWatt'],
      [t('ASIC-Temperatur'), x => t('{0} °C', n(x, 1)), 'temp'],
      [t('Effizienz'), x => t('{0} J/TH', n(x, 2)), 'jth'],
      [t('Lüfter (U/min je %)'), x => n(x, 0), 'rpmPerPercent'],
      [t('Abgelehnte Shares'), x => t('{0} %', n(x * 100, 2)), 'rejectShare'],
      [t('Verfügbarkeit'), x => t('{0} %', n(x * 100, 1)), 'availability'],
    ];
    fill(box,
      d.findings.length
        ? d.findings.map(f => h('div', { class: 'banner row' }, h('span', { style: 'flex:1' }, h('b', {}, f.title), ' – ', f.text)))
        : h('p', { class: 'ok' }, t('Keine Auffälligkeiten.')),
      h('div', { class: 'card table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, [t('Wert'), t('letzte 7 Tage'), t('4 Wochen davor')].map(x => h('th', {}, x)))),
        h('tbody', {}, rows.map(([label, f, key]) => h('tr', {}, h('td', {}, label), h('td', { class: 'num' }, v(d.recent[key], f)), h('td', { class: 'num' }, v(d.base[key], f))))))),
      h('p', { class: 'muted small' }, t('Einmal am Tag geprüft; gemeldet wird nur eine deutliche Veränderung (Push „Gesundheit“ in den Einstellungen). Lüfter und abgelehnte Shares werden seit 0.7.0 erfasst – der Vergleich braucht einige Wochen Daten.')));
  }).catch(e => fill(box, h('p', { class: 'danger' }, e.message)));
  return box;
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
  if (await run(() => api(`/devices/${id}/change`, { method: 'POST', body: { frequency, voltage } }), t('Einstellung angewendet.'))) { cleanTab(); reloadDetailSoon(); }
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
      S.detail.profile.notes ? h('p', { class: 'muted small' }, S.detail.profile.notes) : null) : null,
    isAdmin() && d.online ? minerFanCard(d) : null);
}

/**
 * Lüfter des Miners (AxeOS/NerdQAxe): Automatik mit Zieltemperatur oder fester Wert. Grenzen vom Server (Profil),
 * Bestätigung mit alt → neu, Änderung im Protokoll.
 */
function minerFanCard(d) {
  const lim = S.detail.fanLimits || { minTarget: 45, maxTarget: 65, minPercent: 20 };
  const mode = h('select', { style: 'width:auto' }, h('option', { value: 'auto', selected: d.fanAuto !== false }, t('Automatik')), h('option', { value: 'manual', selected: d.fanAuto === false }, t('Manuell')));
  const target = h('input', { type: 'number', min: lim.minTarget, max: lim.maxTarget, step: 1, value: Math.min(lim.maxTarget, Math.max(lim.minTarget, d.fanTarget ?? 60)) });
  const percent = h('input', { type: 'number', min: lim.minPercent, max: 100, step: 5, value: Math.max(lim.minPercent, d.fanPercent ?? 100) });
  const targetBox = h('div', {}, h('label', {}, t('Zieltemperatur (°C, {0}–{1})', lim.minTarget, lim.maxTarget)), target);
  const percentBox = h('div', {}, h('label', {}, t('Drehzahl (%, {0}–100)', lim.minPercent)), percent);
  const show = () => { targetBox.hidden = mode.value !== 'auto'; percentBox.hidden = mode.value === 'auto'; };
  mode.addEventListener('change', show);
  show();
  const text = (auto, tgt, pct) => auto ? (tgt != null ? t('Automatik, Ziel {0} °C', tgt) : t('Automatik')) : t('Manuell {0} %', pct ?? '?');
  const apply = async () => {
    const auto = mode.value === 'auto';
    const body = { auto, targetTemp: +target.value, percent: +percent.value };
    const msg = t('Lüfter von {0}: {1} → {2}', d.name, text(d.fanAuto !== false, d.fanTarget, d.fanPercent), text(auto, body.targetTemp, body.percent)) +
      (auto ? '' : '\n\n' + t('Achtung: Im manuellen Modus reagiert der Lüfter nicht mehr auf die Temperatur. Der Überhitzungsschutz von AxeOS und die Temperatur-Meldungen bleiben aktiv.'));
    if (!await confirmBox(t('Lüfter einstellen'), msg, t('Anwenden'))) return;
    if (await run(() => api(`/devices/${S.route.id}/fan`, { method: 'POST', body }), t('Lüfter eingestellt.'))) { cleanTab(); reloadDetailSoon(); }
  };
  return h('div', { class: 'card stack' },
    h('h3', {}, t('Lüfter des Miners')),
    h('p', { class: 'muted small' }, t('Aktuell: {0}', text(d.fanAuto !== false, d.fanTarget, d.fanPercent)) + (d.fanRpm != null ? ` · ${d.fanRpm} rpm` : '')),
    h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Modus')), mode), targetBox, percentBox,
      h('button', { class: 'btn', onclick: apply, disabled: d.benchmark?.running }, t('Lüfter anwenden …'))),
    d.benchmark?.running ? h('p', { class: 'muted small' }, t('Während eines Benchmarks steuert der Benchmark den Lüfter.')) : null);
}

/** Difficulty kurz wie AxeOS: 1260 → „1,26k“, 2,4·10⁹ → „2,40G“. */
function diffText(v) {
  if (!v) return '–';
  const u = [[1e15, 'P'], [1e12, 'T'], [1e9, 'G'], [1e6, 'M'], [1e3, 'k']].find(([x]) => v >= x);
  return u ? n(v / u[0], 2) + u[1] : n(v, 0);
}

function liveTiles(d) {
  return [
    tile(t('Hashrate'), hash(d.hashrate), d.expectedHashrate ? t('Soll {0}', hash(d.expectedHashrate)) : '', d.online ? 'ok' : 'danger'),
    tile(t('ASIC / VR'), d.temp != null ? t('{0} °C', n(d.temp, 1)) : '–', d.vrTemp != null ? t('VR {0} °C', n(d.vrTemp, 0)) : ''),
    tile(t('Leistung'), d.power != null ? t('{0} W', n(d.power, 1)) : '–', d.efficiency ? t('{0} J/TH', n(d.efficiency, 2)) : ''),
    tile(t('Frequenz'), d.frequency != null ? t('{0} MHz', d.frequency) : '–', d.voltage != null ? t('{0} mV', d.voltage) : ''),
    tile(t('Lüfter'), d.fanRpm != null ? t('{0} rpm', d.fanRpm) : '–', d.fanPercent != null ? `${d.fanPercent} %` : ''),
    tile(t('Shares'), d.sharesAccepted ?? '–', [d.errorPercent != null ? t('Fehlerrate {0} %', n(d.errorPercent, 2)) : '',
      d.poolDifficulty ? t('Pool-Diff {0}', diffText(d.poolDifficulty)) : ''].filter(Boolean).join(' · ')),
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
  ['stabilityThreshold', t('Stabil ab (Anteil Soll)')], ['maxErrorPercent', t('Max. Fehlerrate (%)')], ['minSamples', t('Mindestanzahl Messwerte')],
  ['minInputVoltageMv', t('Min. Eingangsspannung (mV, leer = aus)'), true], ['maxInputVoltageMv', t('Max. Eingangsspannung (mV, leer = aus)'), true],
];

/** Ja/Nein-Optionen des Benchmarks (wie in der Desktop-App). */
const BENCH_CHECKS = () => [
  ['tryLowerVoltage', t('Pro Frequenz auch niedrigere Spannung testen (effizienter, dauert länger)')],
  ['restartAfterApply', t('Nach jeder Änderung neu starten (sauberere Messwerte)')],
];

function tabBenchmark() {
  const d = summaryOf(S.route.id);
  const base = S.detail.benchmarkDefaults;
  const inputs = {};
  const sel = (key, opts) => { const s = h('select', {}, opts.map(([v, l]) => h('option', { value: v, selected: base[key] === v }, l))); inputs[key] = s; return s; };
  const form = h('div', { class: 'form' },
    BENCH_FIELDS().map(([k, label, optional]) => { const i = h('input', { type: 'number', step: 'any', value: base[k] ?? '' }); i.optional = optional; inputs[k] = i; return h('div', {}, h('label', {}, label), i); }),
    h('div', {}, h('label', {}, t('Am Ende setzen')), sel('restoreMode', [['Best', t('Beste Einstellung')], ['Original', t('Ursprüngliche Einstellung')]])),
    h('div', {}, h('label', {}, t('Beste nach')), sel('restoreRanking', [['Balanced', t('Ausgewogen')], ['MaxHashrate', t('Hashrate')], ['Efficiency', t('Effizienz')]])),
    h('div', {}, h('label', {}, t('Lüfter')), sel('fanMode', [['KeepCurrent', t('unverändert')], ['Full', t('100 % während des Tests')]])));
  const settings = () => {
    const s = { ...base };
    for (const [k, el] of Object.entries(inputs))
      s[k] = el.tagName === 'SELECT' ? el.value : el.type === 'checkbox' ? el.checked : el.optional && el.value.trim() === '' ? null : Number(el.value);
    return s;
  };
  const start = async resume => {
    const body = { settings: settings(), resume };
    const plan = await run(() => api(`/devices/${S.route.id}/benchmark/prepare`, { method: 'POST', body }));
    if (!plan) return;
    if (!await confirmBox(resume ? t('Benchmark fortsetzen') : t('Benchmark starten'), plan.confirmText, t('Starten'))) return;
    if (await run(() => api(`/devices/${S.route.id}/benchmark/start`, { method: 'POST', body }), t('Benchmark läuft auf dem Server.'))) { cleanTab(); reloadDetailSoon(); }
  };
  const session = S.detail.session;
  const running = d.benchmark?.running;
  return h('div', { class: 'stack' },
    h('div', { class: 'card stack', id: 'bench-progress' }, benchProgress(d.benchmark)),
    h('div', { class: 'card stack' },
      h('h3', {}, t('Suchbereich')),
      h('p', { class: 'muted small' }, t('Profil {0}: {1}–{2} MHz, {3}–{4} mV · {5}', S.detail.profile.name, S.detail.profile.minFrequencyMhz, S.detail.profile.maxFrequencyMhz, S.detail.profile.minVoltageMv, S.detail.profile.maxVoltageMv, S.detail.estimatedDuration)),
      form,
      h('div', { class: 'stack' }, BENCH_CHECKS().map(([k, label]) => { const c = h('input', { type: 'checkbox', checked: !!base[k] }); inputs[k] = c; return h('label', { class: 'check' }, c, label); })),
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

/**
 * Benchmark-Ergebnis als Voreinstellung der Automatik speichern: Name vorschlagen, beim Ersetzen alt → neu zeigen und
 * warnen, wenn der Zeitplan diese Voreinstellung nutzt (die Freigabe gilt für den Namen, nicht für die Werte).
 */
async function saveResultAsPreset(r, suggested) {
  const c = S.detail.config;
  const name = h('input', { value: suggested || t('{0} MHz', r.frequencyMhz), maxlength: 40 });
  const note = h('p', { class: 'small' });
  const update = () => {
    const old = (c?.presets || []).find(p => p.name.toLowerCase() === name.value.trim().toLowerCase());
    const used = old && c?.schedule && [c.schedule.defaultPreset, c.schedule.cheapPreset, c.schedule.expensivePreset, ...(c.schedule.entries || []).map(e => e.preset)]
      .some(p => (p || '').toLowerCase() === old.name.toLowerCase());
    note.className = 'small ' + (used ? 'warn' : 'muted');
    note.textContent = !old ? t('Neue Voreinstellung – am Miner ändert sich dabei nichts.')
      : t('Ersetzt „{0}“: {1} MHz / {2} mV → {3} MHz / {4} mV.', old.name, old.frequencyMhz, old.coreVoltageMv, r.frequencyMhz, r.coreVoltageMv) +
        (used ? ' ' + t('Achtung: Der Zeitplan nutzt diese Voreinstellung und setzt künftig die neuen Werte.') : '');
  };
  name.addEventListener('input', update);
  update();
  const body = h('div', { class: 'stack' },
    h('p', {}, t('{0} MHz / {1} mV als Voreinstellung für Zeitplan und Strompreis-Automatik speichern.', r.frequencyMhz, r.coreVoltageMv)),
    h('div', {}, h('label', {}, t('Name')), name), note);
  setTimeout(() => { name.focus(); name.select(); }, 0);
  if (!await confirmBox(t('In Automatik speichern'), body, t('Speichern'))) return;
  const res = await run(() => api(`/devices/${S.route.id}/presets`, { method: 'POST', body: { name: name.value, frequencyMhz: r.frequencyMhz, coreVoltageMv: r.coreVoltageMv } }),
    t('Voreinstellung gespeichert.'));
  if (res && S.detail.config) S.detail.config.presets = res.presets;
}

// Sortierung der Ergebnisliste: erster Klick = Bester zuerst (weniger ist besser → aufsteigend); je Browser gemerkt
const RESULT_COLS = () => [
  ['frequencyMhz', 'MHz', -1], ['coreVoltageMv', 'mV', 1], ['isStable', t('Ergebnis'), -1], ['avgHashRateGh', t('Hashrate'), -1], ['expectedHashRateGh', t('Soll'), -1],
  ['avgPowerW', t('Leistung'), 1], ['efficiencyJth', 'J/TH', 1], ['maxChipTempC', t('Chip max'), 1], ['maxVrTempC', t('VR max'), 1], ['avgErrorPercent', t('Fehler %'), 1],
];
function resultSort() {
  if (S.resultSort === undefined) { try { S.resultSort = JSON.parse(localStorageGet('resultSort') || 'null'); } catch { S.resultSort = null; } }
  return S.resultSort;
}
function sortedResults(results) {
  const so = resultSort();
  if (!so) return results;
  const val = r => so.key === 'isStable' ? (r.isStable ? 1 : 0) : r[so.key];
  // fehlende Werte immer ans Ende
  return [...results].sort((a, b) => {
    const x = val(a), y = val(b);
    if (x == null || Number.isNaN(x)) return y == null ? 0 : 1;
    if (y == null || Number.isNaN(y)) return -1;
    return (x - y) * so.dir;
  });
}

function tabResults() {
  const s = S.detail.session;
  if (!s || !s.results.length) return h('div', { class: 'card muted' }, t('Noch keine Benchmark-Ergebnisse.'));
  const best = s.ranking.balanced;
  const bestCard = (label, r, presetName) => r ? h('div', { class: 'tile' }, h('div', { class: 'label' }, label),
    h('div', { class: 'value' }, t('{0} MHz / {1} mV', r.frequencyMhz, r.coreVoltageMv)),
    h('div', { class: 'sub' }, `${hash(r.avgHashRateGh)} · ${r.efficiencyJth ? n(r.efficiencyJth, 2) + t(' J/TH') : '–'}`),
    isAdmin() ? h('div', { class: 'row', style: 'margin-top:6px' },
      h('button', { class: 'btn small', onclick: () => applyChange(r.frequencyMhz, r.coreVoltageMv) }, t('Anwenden …')),
      h('button', { class: 'btn small', onclick: () => saveResultAsPreset(r, presetName) }, t('In Automatik speichern …'))) : null) : null;
  const so = resultSort();
  const sortBtn = ([k, label, firstDir]) => {
    const active = so?.key === k;
    return h('th', { 'aria-sort': active ? (so.dir > 0 ? 'ascending' : 'descending') : null }, h('button', {
      type: 'button', class: 'th-sort' + (active ? ' on' : ''), title: t('Sortieren'), onclick: () => {
        S.resultSort = active ? (so.dir === firstDir ? { key: k, dir: -firstDir } : null) : { key: k, dir: firstDir };  // dritter Klick: Reihenfolge des Laufs
        localStorageSet('resultSort', JSON.stringify(S.resultSort));
        refreshDeviceTab();
      },
    }, label, h('span', { class: 'arrow', 'aria-hidden': 'true' }, active ? (so.dir > 0 ? '▲' : '▼') : '↕')));
  };
  return h('div', { class: 'stack' },
    h('div', { class: 'tiles wide' }, bestCard(t('Beste Hashrate'), s.ranking.hashrate, t('Hashrate')), bestCard(t('Beste Effizienz'), s.ranking.efficiency, t('Effizienz')),
      bestCard(t('Ausgewogen'), s.ranking.balanced, t('Ausgewogen'))),
    h('div', { class: 'card' },
      h('div', { class: 'titlebar' }, h('h3', {}, t('Lauf vom {0}', time(s.startedAt))), h('span', { class: 'spacer' }), h('span', { class: 'muted small' }, s.finishReason || (s.isFinished ? t('abgeschlossen') : t('nicht abgeschlossen')))),
      h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, RESULT_COLS().map(sortBtn), h('th', {}, ''))),
        h('tbody', {}, sortedResults(s.results).map(r => h('tr', { class: best && r.frequencyMhz === best.frequencyMhz && r.coreVoltageMv === best.coreVoltageMv ? 'best' : null },
          h('td', {}, r.frequencyMhz), h('td', {}, r.coreVoltageMv), h('td', { class: r.isStable ? 'ok' : 'danger' }, r.outcomeText),
          h('td', {}, hash(r.avgHashRateGh)), h('td', {}, hash(r.expectedHashRateGh)), h('td', {}, t('{0} W', n(r.avgPowerW, 1))),
          h('td', {}, r.efficiencyJth ? n(r.efficiencyJth, 2) : '–'), h('td', {}, r.maxChipTempC != null ? n(r.maxChipTempC, 1) : '–'),
          h('td', {}, r.maxVrTempC != null ? n(r.maxVrTempC, 0) : '–'), h('td', {}, r.avgErrorPercent != null ? n(r.avgErrorPercent, 2) : '–'),
          h('td', { class: 'nowrap' }, isAdmin() && r.isStable ? [
            h('button', { class: 'btn small', onclick: () => applyChange(r.frequencyMhz, r.coreVoltageMv) }, t('Anwenden …')), ' ',
            h('button', { class: 'btn small', title: t('Als Voreinstellung für Zeitplan und Strompreis-Automatik speichern'), onclick: () => saveResultAsPreset(r) }, t('Speichern …'))] : null))))))));
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

  const upsertPreset = (name, frequencyMhz, coreVoltageMv) => {
    const i = presets.findIndex(p => p.name.toLowerCase() === name.toLowerCase());
    const p = { name, frequencyMhz, coreVoltageMv };
    if (i >= 0) presets[i] = p; else presets.push(p);
    renderPresets();
  };
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
    if (r) { cleanTab(); await loadDevice(false); if ((guard.enabled && !r.thermalGuardApproved) || (sched.enabled && !r.scheduleApproved)) toast(t('Geänderte Regeln brauchen eine neue Freigabe.'), 'info'); }
    return r;
  };
  const approve = async rule => {
    if (!await save()) return;
    const tv = await run(() => api(`/devices/${S.route.id}/automation/approval-text`, { method: 'POST', body: { rule } }));
    if (!tv || !await confirmBox(t('Regel freigeben'), tv.text, t('Freigeben'))) return;
    if (await run(() => api(`/devices/${S.route.id}/automation/approve`, { method: 'POST', body: { rule } }), t('Regel freigegeben.'))) { cleanTab(); loadDevice(false); }
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
        }, t('Hinzufügen'))),
      // Abkürzungen wie in der Desktop-App; gespeichert wird erst mit „Speichern“ (der Server prüft die Profilgrenzen)
      h('div', { class: 'row' },
        h('button', { class: 'btn small', onclick: () => {
          const d = summaryOf(S.route.id);
          if (!d?.online || d.frequency == null) return toast(t('Kein aktueller Wert – Miner nicht erreichbar.'), 'error');
          upsertPreset(pName.value.trim() || t('{0} MHz', d.frequency), d.frequency, d.voltage);
          pName.value = '';
        } }, t('Aktuelle Einstellung übernehmen')),
        h('button', { class: 'btn small', onclick: () => {
          const r = S.detail.session?.ranking;
          if (!r?.hashrate || !r?.efficiency) return toast(t('Es gibt noch keine stabilen Benchmark-Ergebnisse für dieses Gerät.'), 'error');
          upsertPreset(t('Hashrate'), r.hashrate.frequencyMhz, r.hashrate.coreVoltageMv);
          upsertPreset(t('Effizienz'), r.efficiency.frequencyMhz, r.efficiency.coreVoltageMv);
        } }, t('Aus Benchmark (Hashrate + Effizienz)')),
        h('span', { class: 'spacer' }),
        h('button', { class: 'btn small primary', onclick: save }, t('Speichern')))),
    h('div', { class: 'card stack' },
      h('div', { class: 'titlebar' }, h('h3', {}, t('Temperaturschutz')), h('span', { class: `pill ${c.thermalGuardApproved ? '' : 'gray'}` }, c.thermalGuardApproved ? t('freigegeben') : t('nicht freigegeben'))),
      checkInput(guard, 'enabled', t('eingeschaltet')),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, t('Max. Chip (°C)')), numInput(guard, 'maxChipTempC', 0.5)), h('div', {}, h('label', {}, t('Max. VR (°C)')), numInput(guard, 'maxVrTempC', 0.5)),
        h('div', {}, h('label', {}, t('durchgehend (min)')), numInput(guard, 'minutes')), h('div', {}, h('label', {}, t('Absenken um (MHz)')), numInput(guard, 'stepMhz')),
        h('div', {}, h('label', {}, t('nie unter (MHz)')), numInput(guard, 'minFrequencyMhz')), h('div', {}, h('label', {}, t('Zurück nach (min kühl)')), numInput(guard, 'recoverMinutes'))),
      checkInput(guard, 'recover', t('schrittweise zurück, wenn wieder kühl')),
      h('div', { class: 'row' }, h('button', { class: 'btn', onclick: save }, t('Speichern')), h('button', { class: 'btn primary', onclick: () => approve('thermal') }, t('Speichern & freigeben …')))),
    h('div', { class: 'card stack' },
      h('div', { class: 'titlebar' }, h('h3', {}, t('Zeitplan / Strompreis')), h('span', { class: `pill ${c.scheduleApproved ? '' : 'gray'}` }, c.scheduleApproved ? t('freigegeben') : t('nicht freigegeben'))),
      checkInput(sched, 'enabled', t('eingeschaltet')),
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
  // Filter wie in der Desktop-App: Kategorie (Mehrfachauswahl), Level und Freitext über Modul und Nachricht
  const cats = [['Shares', t('Shares')], ['Pool', t('Pool/Stratum')], ['Asic', t('ASIC/Jobs')], ['Thermal', t('Temperatur/Lüfter/Strom')],
    ['System', t('System/WLAN')], ['Other', t('Sonstige')]];
  const levels = [['E', t('Fehler')], ['W', t('Warnungen')], ['I', 'Info'], ['D', 'Debug']];
  let off = new Set();
  try { off = new Set(JSON.parse(localStorageGet('logFilterOff') || '[]')); } catch { /* egal */ }
  const counts = {};
  const text = h('input', { type: 'search', placeholder: t('Filter nach Text oder Modul (z. B. asic_result, stratum, fan)'), style: 'max-width:320px', oninput: () => refilter() });
  const chip = (key, label) => {
    const cnt = h('span', { class: 'muted' });
    const b = h('button', { type: 'button', class: 'btn small' + (off.has(key) ? '' : ' primary'), 'aria-pressed': String(!off.has(key)), onclick: () => {
      off.has(key) ? off.delete(key) : off.add(key);
      b.className = 'btn small' + (off.has(key) ? '' : ' primary');
      b.setAttribute('aria-pressed', String(!off.has(key)));
      localStorageSet('logFilterOff', JSON.stringify([...off]));
      refilter();
    } }, label, ' ', cnt);
    b.count = cnt;
    return b;
  };
  const catChips = cats.map(([k, l]) => chip(k, l));
  const levelChips = levels.map(([k, l]) => chip(k, l));
  const lvl = l => { const c = String(l.level || 'I')[0]; return c === 'V' ? 'D' : c; };
  const visible = l => {
    if (lvl(l) === 'A') return true;
    if (off.has(l.category || 'Other') || off.has(lvl(l))) return false;
    const f = text.value.trim().toLowerCase();
    return !f || (l.tag || '').toLowerCase().includes(f) || (l.message || '').toLowerCase().includes(f);
  };
  const refilter = () => { for (const el of minerLog.children) el.style.display = visible(el.line) ? '' : 'none'; if (follow.checked) minerLog.scrollTop = minerLog.scrollHeight; };
  const showCounts = () => cats.forEach(([k], i) => { catChips[i].count.textContent = counts[k] ? `(${counts[k]})` : ''; });
  S.logEs?.close();
  const es = new EventSource(`/api/v1/devices/${S.route.id}/minerlog`);
  S.logEs = es;
  es.addEventListener('line', e => {
    const l = JSON.parse(e.data);
    const tv = new Date(l.time).toLocaleTimeString(LOCALE);
    const el = h('div', { class: lvl(l) }, `${tv} ${l.tag ? l.tag + ': ' : ''}${l.message}`);
    el.line = l;
    if (!visible(l)) el.style.display = 'none';
    minerLog.append(el);
    counts[l.category || 'Other'] = (counts[l.category || 'Other'] || 0) + 1;
    while (minerLog.childElementCount > 2000) {
      const c = minerLog.firstChild.line?.category || 'Other';
      counts[c] = Math.max(0, (counts[c] || 1) - 1);
      minerLog.firstChild.remove();
    }
    showCounts();
    if (follow.checked) minerLog.scrollTop = minerLog.scrollHeight;
  });
  es.addEventListener('status', e => { state.textContent = JSON.parse(e.data).text; });
  setTimeout(fillAppLog, 0);
  return h('div', { class: 'grid', style: 'grid-template-columns:repeat(auto-fit,minmax(340px,1fr))' },
    h('div', { class: 'card stack' }, h('h3', {}, t('App-Protokoll (Server)')), appLog),
    h('div', { class: 'card stack' }, h('div', { class: 'titlebar' }, h('h3', {}, t('Miner-Logs live')), h('span', { class: 'spacer' }), state),
      h('div', { class: 'row wrap', role: 'group', 'aria-label': t('Kategorien') }, catChips),
      h('div', { class: 'row wrap' }, levelChips, text),
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
  const sm = tv.summary;
  const tab = ['rewards', 'sales', 'wallets'].includes(S.taxTab) ? S.taxTab : 'rewards';
  const tabLink = (k, label) => h('a', { href: '#', class: k === tab ? 'active' : null, onclick: e => { e.preventDefault(); S.taxTab = k; renderTax(); } }, label);
  const refresh = async () => { if (await run(() => api('/tax/refresh', { method: 'POST' }), t('Wallets geprüft.'))) renderTax(); };
  const salesText = sm.saleCount
    ? t('{0}: {1} Verkäufe · steuerpflichtiger Gewinn {2} € (Freigrenze {3} €)', sm.year, sm.saleCount, n(sm.taxableGainEur, 2), n(sm.freeLimitEur, 0))
      + (sm.saleMissingPrice ? t(' · Kurs fehlt') : '') + (sm.saleUnmatched ? t(' · Menge ohne Zufluss') : '')
    : t('{0}: keine Verkäufe erfasst', sm.year);
  mount(h('div', { class: 'stack' },
    h('div', { class: 'card stack' },
      h('div', { class: 'titlebar' }, h('h2', {}, t('Steuer – dokumentierte Zuflüsse')), h('span', { class: 'spacer' }),
        h('button', { class: 'btn', onclick: refresh }, t('Jetzt prüfen')),
        h('a', { class: 'btn', href: '/api/v1/tax/rewards.csv', download: '' }, t('CSV exportieren'))),
      h('p', {}, t('{0}: {1} Zuflüsse · {2} €', sm.year, sm.rewardCount, n(sm.rewardEur, 2)) + (sm.rewardsWithoutPrice ? t(' · {0} ohne Kurs', sm.rewardsWithoutPrice) : ''),
        h('br'), salesText),
      h('p', { class: 'muted small' }, t('Überwachte Wallets: {0} · letzte Prüfung {1}. Rohrechnung nach deutschem Steuerrecht (§ 23 EStG, FIFO, ein Jahr Haltefrist) für die eigene Übersicht – keine Steuerberatung.', tv.wallets.length, time(tv.status))),
      tv.warning ? h('p', { class: 'danger' }, tv.warning) : null),
    h('div', { class: 'tabs' }, tabLink('rewards', t('Zuflüsse')), tabLink('sales', t('Verkäufe')), tabLink('wallets', t('Wallets ({0})', tv.wallets.length))),
    tab === 'rewards' ? taxRewardsCard(tv) : tab === 'sales' ? taxSalesCard(tv) : taxWalletsCard(tv),
    taxEnergyCard()));
}

const COINS = [['Bitcoin', 'BTC'], ['BitcoinCash', 'BCH']];
const coinSelect = (value, auto) => {
  const el = h('select', { style: 'width:auto' }, [...(auto ? [['', t('automatisch')]] : []), ...COINS].map(([v, l]) => h('option', { value: v }, l)));
  el.value = value ?? '';
  return el;
};
const sat8 = v => n(v, 8);

/** Zuflüsse: Kurs/Notiz nachtragen, Eingänge entfernen, die kein Mining-Ertrag sind. */
function taxRewardsCard(tv) {
  const edit = async r => {
    const price = h('input', { value: r.eurPriceAtReceipt ?? '', inputmode: 'decimal', placeholder: t('z. B. 58.250,00') });
    const note = h('input', { value: r.note || '', maxlength: 500 });
    const body = h('div', { class: 'stack' },
      h('p', {}, t('{0} {1} vom {2}', sat8(r.amount), r.symbol, time(r.receivedAtUtc))),
      h('div', {}, h('label', {}, t('EUR-Kurs je Coin (leer = entfernen)')), price),
      r.priceSource ? h('p', { class: 'muted small' }, t('Bisher: {0}', r.priceSource)) : null,
      h('div', {}, h('label', {}, t('Notiz')), note),
      h('p', { class: 'muted small' }, t('Ein geänderter Kurs gilt als manuell und wird nicht mehr automatisch überschrieben.')));
    if (!await confirmBox(t('Zufluss bearbeiten'), body, t('Speichern'))) return;
    const changed = price.value.trim() !== String(r.eurPriceAtReceipt ?? '');
    if (await run(() => api(`/tax/rewards/${r.id}`, { method: 'PUT', body: { price: changed ? price.value : null, note: note.value } }), t('Gespeichert.'))) renderTax();
  };
  const remove = async r => {
    if (!await confirmBox(t('Eintrag entfernen'), t('Eintrag vom {0} über {1} {2} entfernen? Nur für Eingänge, die kein Mining-Ertrag sind. Die Transaktion wird danach dauerhaft ignoriert.', time(r.receivedAtUtc), sat8(r.amount), r.symbol), t('Entfernen'), true)) return;
    if (await run(() => api(`/tax/rewards/${r.id}`, { method: 'DELETE' }), t('Eintrag entfernt, Transaktion wird künftig ignoriert.'))) renderTax();
  };
  if (!tv.rewards.length) return h('div', { class: 'card muted' }, t('Noch keine Zuflüsse erfasst.'));
  return h('div', { class: 'card table-wrap' }, h('table', {},
    h('thead', {}, h('tr', {}, [t('Datum'), 'Coin', t('Betrag'), t('Kurs (EUR)'), t('Wert (EUR)'), t('Rest'), t('Haltefrist'), t('Wallet'), t('Notiz'), ''].map(x => h('th', {}, x)))),
    h('tbody', {}, tv.rewards.map(r => h('tr', {},
      h('td', {}, time(r.receivedAtUtc)), h('td', {}, r.symbol), h('td', { class: 'num' }, sat8(r.amount)),
      h('td', { class: 'num', title: r.priceSource || '' }, r.eurPriceAtReceipt != null ? n(r.eurPriceAtReceipt, 2) + (r.manualPrice ? ' ✎' : '') : h('span', { class: 'warn' }, t('fehlt'))),
      h('td', { class: 'num' }, r.eurValue != null ? n(r.eurValue, 2) : '–'),
      h('td', { class: 'num' }, r.remaining > 0 ? sat8(r.remaining) : '–'),
      h('td', { class: 'small' }, r.remaining > 0 ? r.holdingStatus : t('verkauft')),
      h('td', {}, r.walletLabel || ''), h('td', { class: 'small' }, r.note || ''),
      h('td', { class: 'row', style: 'flex-wrap:nowrap' },
        h('button', { class: 'btn small', onclick: () => edit(r) }, t('Bearbeiten')),
        h('button', { class: 'btn small', title: t('Kein Mining-Ertrag – entfernen'), onclick: () => remove(r) }, '✕')))))));
}

/** Verkäufe: erfassen (FIFO, Haltefrist), löschen, als CSV exportieren. */
function taxSalesCard(tv) {
  const coin = coinSelect('Bitcoin');
  const date = h('input', { type: 'date', value: new Date().toLocaleDateString('sv'), max: new Date().toLocaleDateString('sv'), style: 'width:auto' });
  const amount = h('input', { inputmode: 'decimal', placeholder: t('Menge, z. B. 0,0025'), style: 'width:auto;flex:1 1 130px' });
  const proceeds = h('input', { inputmode: 'decimal', placeholder: t('Erlös in EUR'), style: 'width:auto;flex:1 1 110px' });
  const note = h('input', { placeholder: t('Notiz (optional)'), maxlength: 200, style: 'width:auto;flex:2 1 160px' });
  const stock = h('p', { class: 'muted small' });
  const showStock = () => { const sym = COINS.find(c => c[0] === coin.value)[1]; stock.textContent = t('Dokumentierter Bestand: {0} {1}', sat8(tv.available[sym] ?? 0), sym); };
  coin.addEventListener('change', showStock);
  showStock();
  const add = async () => {
    const body = { coin: coin.value, date: date.value, amount: amount.value, proceeds: proceeds.value, note: note.value, allowOversell: false };
    try {
      await api('/tax/disposals', { method: 'POST', body });
    } catch (e) {
      if (e.status !== 409) { if (e.status !== 401) toast(e.message, 'error'); return; }
      if (!await confirmBox(t('Verkauf erfassen'), e.message, t('Trotzdem erfassen'), true)) return;
      if (!await run(() => api('/tax/disposals', { method: 'POST', body: { ...body, allowOversell: true } }))) return;
    }
    toast(t('Verkauf erfasst.'), 'ok');
    renderTax();
  };
  const remove = async d => {
    if (!await confirmBox(t('Verkauf löschen'), t('Verkauf vom {0} über {1} {2} löschen?', new Date(d.soldAtUtc).toLocaleDateString(LOCALE), sat8(d.amount), d.symbol), t('Löschen'), true)) return;
    if (await run(() => api(`/tax/disposals/${d.id}`, { method: 'DELETE' }), t('Verkauf gelöscht.'))) renderTax();
  };
  const hint = d => [d.missingPrice ? t('Kurs fehlt bei einem Zufluss') : null,
    d.unmatchedAmount > 0 ? t('{0} ohne dokumentierten Zufluss', sat8(d.unmatchedAmount)) : null].filter(Boolean).join(' · ');
  return h('div', { class: 'stack' },
    h('div', { class: 'card stack' },
      h('h3', {}, t('Verkauf oder Tausch erfassen')),
      h('div', { class: 'row' }, coin, date, amount, proceeds, note, h('button', { class: 'btn primary', onclick: add }, t('Erfassen'))),
      stock,
      h('p', { class: 'muted small' }, t('Zuordnung nach FIFO zu den dokumentierten Zuflüssen; Anteile, die länger als ein Jahr gehalten wurden, sind steuerfrei. Bei Tausch den Marktwert der Gegenleistung als Erlös eintragen.'))),
    h('div', { class: 'card stack' },
      h('div', { class: 'titlebar' }, h('h3', {}, t('Erfasste Verkäufe')), h('span', { class: 'spacer' }),
        tv.disposals.length ? h('a', { class: 'btn small', href: '/api/v1/tax/disposals.csv', download: '' }, t('CSV exportieren')) : null),
      tv.disposals.length ? h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, [t('Datum'), 'Coin', t('Menge'), t('Erlös'), t('Anschaffung'), t('Gewinn (steuerpflichtig)'), t('haltefristfrei'), t('Hinweis'), t('Notiz'), ''].map(x => h('th', {}, x)))),
        h('tbody', {}, tv.disposals.map(d => h('tr', {},
          h('td', {}, new Date(d.soldAtUtc).toLocaleDateString(LOCALE)), h('td', {}, d.symbol), h('td', { class: 'num' }, sat8(d.amount)),
          h('td', { class: 'num' }, n(d.proceedsEur, 2)), h('td', { class: 'num' }, n(d.costBasisEur, 2)), h('td', { class: 'num' }, n(d.taxableGainEur, 2)),
          h('td', { class: 'num' }, d.taxFreeAmount > 0 ? sat8(d.taxFreeAmount) : '–'), h('td', { class: 'small warn' }, hint(d)), h('td', { class: 'small' }, d.note || ''),
          h('td', {}, h('button', { class: 'btn small', title: t('Löschen'), onclick: () => remove(d) }, '✕')))))))
        : h('p', { class: 'muted' }, t('Noch keine Verkäufe erfasst.'))));
}

/** Wallets: hinzufügen, aus den Minern übernehmen, umbenennen/Coin ändern, aus der Überwachung nehmen. */
function taxWalletsCard(tv) {
  const address = h('input', { placeholder: t('Wallet-Adresse'), autocomplete: 'off', spellcheck: false, style: 'width:auto;flex:3 1 260px' });
  const coin = coinSelect('', true);
  const label = h('input', { placeholder: t('Bezeichnung (optional)'), maxlength: 60, style: 'width:auto;flex:1 1 150px' });
  const add = async () => {
    if (await run(() => api('/tax/wallets', { method: 'POST', body: { address: address.value, coin: coin.value || null, label: label.value } }), t('Wallet hinzugefügt.'))) renderTax();
  };
  const importMiners = async () => {
    const r = await run(() => api('/tax/wallets/import', { method: 'POST' }));
    if (!r) return;
    let text = r.added || r.skipped ? t('{0} Adresse(n) übernommen, {1} bereits vorhanden.', r.added, r.skipped)
      : t('Keine Adresse gefunden. Miner müssen online sein oder eine Wallet-Adresse in den Einstellungen haben.');
    if (r.ambiguous.length) text += t(' Coin bei {0} bitte prüfen (Legacy-Adresse, BCH angenommen).', r.ambiguous.join(', '));
    toast(text, r.added ? 'ok' : 'info', 10000);
    renderTax();
  };
  const edit = async w => {
    const name = h('input', { value: w.label, maxlength: 60 });
    const c = coinSelect(w.coin);
    const body = h('div', { class: 'stack' }, h('p', { class: 'small', style: 'font-family:monospace;word-break:break-all' }, w.address),
      h('div', {}, h('label', {}, t('Bezeichnung')), name), h('div', {}, h('label', {}, 'Coin'), c),
      h('p', { class: 'muted small' }, t('Den Coin nur ändern, wenn er falsch erkannt wurde (Legacy-Adressen 1…/3… gibt es bei BTC und BCH).')));
    if (!await confirmBox(t('Wallet bearbeiten'), body, t('Speichern'))) return;
    if (await run(() => api(`/tax/wallets/${w.id}`, { method: 'PUT', body: { label: name.value, coin: c.value } }), t('Gespeichert.'))) renderTax();
  };
  const remove = async w => {
    if (!await confirmBox(t('Wallet entfernen'), t('„{0}“ aus der Überwachung entfernen? Bereits dokumentierte Zuflüsse bleiben erhalten.', w.label), t('Entfernen'), true)) return;
    if (await run(() => api(`/tax/wallets/${w.id}`, { method: 'DELETE' }), t('Wallet entfernt.'))) renderTax();
  };
  const short = a => a.length > 24 ? a.slice(0, 12) + '…' + a.slice(-8) : a;
  return h('div', { class: 'stack' },
    h('div', { class: 'card stack' },
      h('h3', {}, t('Wallet hinzufügen')),
      h('div', { class: 'row' }, address, coin, label, h('button', { class: 'btn primary', onclick: add }, t('Hinzufügen')),
        h('button', { class: 'btn', onclick: importMiners }, t('Aus Minern übernehmen'))),
      h('p', { class: 'muted small' }, t('Eingänge auf diesen Adressen werden mit EUR-Kurs zum Zuflusszeitpunkt dokumentiert. Dafür fragt BitaxeTuner die Adressen bei mempool.space (BTC) bzw. Blockchair (BCH) ab.'))),
    h('div', { class: 'card table-wrap' }, tv.wallets.length ? h('table', {},
      h('thead', {}, h('tr', {}, [t('Bezeichnung'), 'Coin', t('Adresse'), t('Hinzugefügt'), ''].map(x => h('th', {}, x)))),
      h('tbody', {}, tv.wallets.map(w => h('tr', {},
        h('td', {}, w.label), h('td', {}, COINS.find(c => c[0] === w.coin)?.[1] || w.coin), h('td', { class: 'small', style: 'font-family:monospace;word-break:break-all', title: w.address }, short(w.address)),
        h('td', { class: 'small' }, time(w.addedAtUtc)),
        h('td', { class: 'row', style: 'flex-wrap:nowrap' },
          h('button', { class: 'btn small', onclick: () => edit(w) }, t('Bearbeiten')),
          h('button', { class: 'btn small', title: t('Entfernen'), onclick: () => remove(w) }, '✕'))))))
      : h('p', { class: 'muted' }, t('Noch keine Wallets eingetragen.'))));
}

/** Steuer-Bereich: Stromkosten je Monat neben den Zuflüssen (aus den Monatsberichten). */
function taxEnergyCard() {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  const year = h('select', { style: 'width:auto' });
  const load = async () => {
    fill(body, h('p', { class: 'muted' }, t('Lade …')));
    let d;
    try { d = await api(`/reports/${year.value}/months`); } catch (e) { fill(body, h('p', { class: 'danger' }, e.message)); return; }
    const rows = d.months.filter(m => m.hasData || m.incomeEur > 0);
    if (!rows.length) { fill(body, h('p', { class: 'muted' }, t('Für dieses Jahr liegen keine Messwerte oder Zuflüsse vor.'))); return; }
    const sum = k => rows.reduce((a, m) => a + m[k], 0);
    const cur = d.currency || '€';
    const month = p => new Date(`${p}-01T00:00:00`).toLocaleDateString(LOCALE, { month: 'long' });
    fill(body, h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {}, [t('Monat'), 'kWh', t('Stromkosten ({0})', cur), t('Zuflüsse (EUR)'), t('Differenz')].map(x => h('th', {}, x)))),
      h('tbody', {}, rows.map(m => h('tr', {},
        h('td', {}, month(m.period) + (m.partial ? t(' (läuft)') : '')), h('td', { class: 'num' }, m.hasData ? n(m.kwh, 1) : '–'),
        h('td', { class: 'num' }, m.hasData ? n(m.cost, 2) : '–'),
        h('td', { class: 'num' }, n(m.incomeEur, 2) + (m.incomeMissing ? t(' ({0} ohne Kurs)', m.incomeMissing) : '')),
        h('td', { class: `num ${m.incomeEur - m.cost < 0 ? 'danger' : 'ok'}` }, n(m.incomeEur - m.cost, 2)))),
        h('tr', {}, h('td', {}, h('b', {}, t('Summe'))), h('td', { class: 'num' }, h('b', {}, n(sum('kwh'), 1))), h('td', { class: 'num' }, h('b', {}, n(sum('cost'), 2))),
          h('td', { class: 'num' }, h('b', {}, n(sum('incomeEur'), 2))), h('td', { class: 'num' }, h('b', {}, n(sum('incomeEur') - sum('cost'), 2))))))),
      h('p', { class: 'muted small' }, t('Stromkosten aus den Messwerten (mit Smart Plugs und Stundenpreisen, wenn eingerichtet). Ob und wie Stromkosten steuerlich berücksichtigt werden, klärt deine Steuerberatung – keine Steuerberatung.')));
  };
  api('/reports').then(r => {
    const years = [...new Set(r.periods.filter(p => p.length === 4).concat([String(new Date().getFullYear())]))].sort().reverse();
    fill(year, years.map(y => h('option', { value: y }, y)));
    year.addEventListener('change', load);
    load();
  }).catch(e => fill(body, h('p', { class: 'danger' }, e.message)));
  return h('div', { class: 'card stack' },
    h('div', { class: 'titlebar' }, h('h2', {}, t('Stromkosten je Monat')), h('span', { class: 'spacer' }), year), body);
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
  const deviceRows = status.devices.flatMap(d => {
    const name = h('input', { value: d.name });
    // Weitere Felder wie in der Desktop-App – erst beim Aufklappen geladen
    const more = h('tr', { hidden: true }, h('td', { colspan: 3 }, h('div', { class: 'muted small' }, t('Lade …'))));
    const extra = {};
    const toggle = h('button', { class: 'btn small', 'aria-expanded': 'false', onclick: async () => {
      const open = more.hidden;
      more.hidden = !open;
      toggle.setAttribute('aria-expanded', String(open));
      if (!open || extra.loaded) return;
      const det = await run(() => api(`/devices/${d.id}`));
      const c = det?.config;
      if (!c) return;
      extra.loaded = true;
      extra.wallet = h('input', { value: c.walletAddress || '', placeholder: t('leer = aus Stratum-User'), class: 'mono' });
      extra.coin = h('select', {}, [['Auto', t('Auto (aus Adresse)')], ['BTC', 'BTC'], ['BCH', 'BCH']].map(([v, l]) => h('option', { value: v, selected: c.coin === v }, l)));
      extra.repo = h('input', { value: c.firmwareRepo || '', placeholder: t('leer = kein Check') });
      extra.logAlerts = h('input', { type: 'checkbox', checked: !!c.logAlerts });
      extra.groups = h('input', { value: (d.groups || []).join(', '), placeholder: t('z. B. Community, Keller'), list: 'group-names' });
      fill(more.firstChild, h('div', { class: 'stack' },
        h('div', { class: 'form' },
          h('div', { style: 'grid-column:span 2' }, h('label', {}, t('Wallet-Adresse')), extra.wallet),
          h('div', {}, h('label', {}, t('Coin')), extra.coin),
          h('div', {}, h('label', {}, t('Firmware-Repository (GitHub)')), extra.repo),
          h('div', { style: 'grid-column:span 2' }, h('label', {}, t('Gruppen (mit Komma getrennt)')), extra.groups)),
        h('datalist', { id: 'group-names' }, (status.groups || []).map(g => h('option', { value: g }))),
        h('label', { class: 'check' }, extra.logAlerts, t('Log-Alarme für diesen Miner (liest die Miner-Logs dauerhaft mit und belegt dafür einen der wenigen WebSocket-Plätze)'))));
    } }, t('Details'));
    const save = () => {
      const body = { name: name.value };
      if (extra.loaded) Object.assign(body, { walletAddress: extra.wallet.value, coin: extra.coin.value, firmwareRepo: extra.repo.value, logAlerts: extra.logAlerts.checked,
        groups: extra.groups.value.split(',').map(x => x.trim()).filter(Boolean) });
      return run(() => api(`/devices/${d.id}`, { method: 'PUT', body }), t('Gespeichert.'));
    };
    return [h('tr', {}, h('td', {}, name), h('td', {}, d.host), h('td', {},
      toggle, ' ',
      h('button', { class: 'btn small', onclick: save }, t('Speichern')), ' ',
      h('button', {
        class: 'btn small danger', onclick: async () => {
          if (!await confirmBox(t('Gerät entfernen'), t('„{0}“ entfernen? Verlauf in history.db und Steuerdaten bleiben erhalten.', d.name), t('Entfernen'), true)) return;
          if (await run(() => api(`/devices/${d.id}`, { method: 'DELETE' }), t('Entfernt.'))) renderSettings();
        },
      }, t('Entfernen')))), more];
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
      h('div', { class: 'row' }, h('button', { class: 'btn small', onclick: async () => {
        if (await run(() => api('/onboarding', { method: 'POST', body: { show: true } }))) toast(t('„Erste Schritte“ steht wieder in der Übersicht.'), 'ok', 8000);
      } }, t('Einführung „Erste Schritte“ wieder anzeigen'))),
      h('div', { class: 'form' },
        h('div', {}, h('label', {}, t('Abfrage alle (s)')), text(s, 'intervalSeconds', 'number')),
        h('div', {}, h('label', {}, t('Live-Verlauf (min)')), text(s, 'historyMinutes', 'number')),
        h('div', {}, h('label', {}, t('Verlauf aufbewahren (Tage)')), text(s, 'historyDays', 'number')),
        h('div', {}, h('label', {}, t('Strompreis (ct/kWh)')), text(s, 'electricityCtPerKwh', 'number')),
        h('div', {}, h('label', {}, t('Preis ist')), (() => { const el = h('select', { onchange: e => { s.electricityPriceIsNet = e.target.value === 'net'; } },
          h('option', { value: 'gross' }, t('brutto (inkl. MwSt.)')), h('option', { value: 'net' }, t('netto (zzgl. MwSt.)'))); el.value = s.electricityPriceIsNet ? 'net' : 'gross'; return el; })()),
        h('div', {}, h('label', {}, t('MwSt. (%)')), text(s, 'vatPercent', 'number')),
        h('div', {}, h('label', {}, t('Währung')), text(s, 'currency')),
        h('div', {}, h('label', {}, t('Warnung ab ASIC (°C)')), text(s, 'tempWarn', 'number')),
        h('div', {}, h('label', {}, t('Wallets prüfen alle (min)')), text(s, 'walletPollMinutes', 'number')),
        h('div', {}, h('label', {}, t('Steuer-Erfassung alle (min)')), text(s, 'taxPollMinutes', 'number'))),
      checkInput(s, 'restartAfterApply', t('Nach Frequenz-/Spannungsänderung neu starten')),
      checkInput(s, 'checkForUpdates', t('Nach neuen Versionen suchen')),
      checkInput(s, 'walletLookupConsent', t('Aus dem Pool-Benutzer erkannte Wallet-Adressen bei mempool.space/Blockchair abfragen')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Sprache des Servers (Push, Tagesbericht, E-Paper)')),
        select(s, 'language', [['auto', t('Automatisch (Systemsprache)')], ['de', 'Deutsch'], ['en', 'English']]))),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('PIN für „Nur ansehen“ (mind. 6 Ziffern, „-“ = entfernen)')), pin)),
      s.viewerPinLegacy ? h('p', { class: 'warn small' }, t('Die PIN ist noch im alten Format gespeichert. Bitte einmal neu setzen (mind. 6 Ziffern) – sie wird dann sicherer gespeichert.')) : null),
    h('div', { class: 'card stack' }, h('h2', {}, t('Push-Benachrichtigungen')),
      pushTargetsEditor(nt, text, select, status?.devices || [], save),
      h('div', { class: 'row' },
        h('button', { class: 'btn', onclick: async () => { await save(); const r = await run(() => api('/notifications/test', { method: 'POST', body: {} })); if (r) toast(r.ok ? t('Testnachricht gesendet.') : r.error, r.ok ? 'ok' : 'error'); } }, t('Speichern & alle testen')),
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
    viewersCard(status?.groups || []),
    connectionCard(),
    metricsCard(),
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
  addToc();
  if (S.settingsSection) { jumpTo(S.settingsSection); S.settingsSection = null; }
}

/**
 * Inhaltsverzeichnis für lange Seiten: mitlaufende Leiste mit allen Karten-Überschriften (h2), Klick springt dorthin,
 * beim Scrollen wird der aktuelle Abschnitt markiert.
 */
function addToc() {
  const root = $('#app').firstElementChild;
  if (!root) return;
  const heads = [...root.querySelectorAll('.card > h2, .card > .titlebar > h2')];
  if (heads.length < 4) return;
  const links = heads.map((hd, i) => {
    const card = hd.closest('.card');
    card.id = card.id || `abschnitt-${i}`;
    return h('a', { href: '#', 'data-target': card.id, onclick: e => { e.preventDefault(); jumpTo(card.id); } }, hd.textContent);
  });
  const toc = h('nav', { class: 'toc', 'aria-label': t('Inhalt') }, links);
  toc.style.top = `${($('.top')?.offsetHeight || 0)}px`;
  root.prepend(toc);
  const mark = () => {
    const top = toc.getBoundingClientRect().bottom + 12;
    let current = links[0];
    heads.forEach((hd, i) => { if (hd.closest('.card').getBoundingClientRect().top <= top) current = links[i]; });
    if (window.innerHeight + window.scrollY >= document.body.scrollHeight - 4) current = links[links.length - 1];
    links.forEach(a => a.classList.toggle('active', a === current));
  };
  window.removeEventListener('scroll', S.tocScroll || (() => {}));
  S.tocScroll = () => { if (document.body.contains(toc)) mark(); };
  window.addEventListener('scroll', S.tocScroll, { passive: true });
  mark();
}

/** Zu einer Karte springen – per Kennung oder Überschrift (z. B. „Smart Plugs“). */
function jumpTo(idOrTitle) {
  let card = document.getElementById(idOrTitle);
  if (!card) card = [...document.querySelectorAll('.card')].find(c => c.querySelector('h2')?.textContent === idOrTitle);
  if (!card) return;
  const toc = $('.toc');
  const offset = ($('.top')?.offsetHeight || 0) + (toc?.offsetHeight || 0) + 8;
  window.scrollTo({ top: card.getBoundingClientRect().top + window.scrollY - offset, behavior: 'smooth' });
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
  if (display?.status) parts.push(displayCard(display));   // Ansicht-Zugang mit Gruppen: keine E-Paper-Anzeige (Summen über alle Miner)
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
      d.device ? h('p', { class: 'muted small' }, t('Angeschlossen über: {0}', d.device)) : null,
      (() => {
        const own = h('div', { class: 'stack' }, picoConnectionInputs(s, 'display'),
          h('div', { class: 'row' }, h('button', { class: 'btn', onclick: () => picoWlanSetup('display') }, t('Display-Pico für WLAN einrichten …'))),
          h('p', { class: 'muted small' }, t('Eigener Display-Pico: Pico 2 WH direkt auf das Waveshare-E-Paper stecken (keine Kabel), eigenes USB-Netzteil. Er zeigt die Anzeige und meldet seine Taster; Lüfter bleiben am Lüfter-Pico.')));
        own.hidden = s.device !== 'own';
        const sel = h('select', { style: 'width:auto', onchange: e => { s.device = e.target.value; own.hidden = s.device !== 'own'; } },
          h('option', { value: 'fans' }, t('am Lüfter-Pico (Kabel zur Platine)')), h('option', { value: 'own' }, t('eigener Display-Pico (aufgesteckt)')));
        sel.value = s.device || 'fans';
        return h('div', { class: 'stack' }, h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Anzeige hängt')), sel)), own);
      })(),
      checkInput(s, 'inverted', t('Farben umkehren: helle Schrift auf schwarzem Grund (Rot bleibt rot)')),
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

/** Verbindung eines Pico: USB (Port) oder WLAN (Gerätename/IP). obj = Lüfter- oder Anzeige-Einstellungen. */
function picoConnectionInputs(obj, role) {
  const usb = h('div', {}, h('label', {}, t('USB-Port („auto“ = Pico automatisch finden)')), h('input', { value: obj.port || 'auto', oninput: e => { obj.port = e.target.value; } }));
  const wlan = h('div', {}, h('label', {}, t('WLAN: Gerätename oder IP-Adresse')),
    h('input', { value: obj.networkHost || '', placeholder: role === 'display' ? 'bitaxetuner-display.local' : 'bitaxetuner-fans.local', oninput: e => { obj.networkHost = e.target.value; } }));
  const show = () => { usb.hidden = obj.connection === 'wlan'; wlan.hidden = obj.connection !== 'wlan'; };
  const sel = h('select', { style: 'width:auto', onchange: e => { obj.connection = e.target.value; show(); } },
    h('option', { value: 'usb' }, t('USB-Kabel am Server')), h('option', { value: 'wlan' }, t('WLAN (Pico 2 W)')));
  sel.value = obj.connection || 'usb';
  show();
  return h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Verbindung')), sel), usb, wlan,
    obj.networkIp ? h('div', {}, h('label', {}, t('zuletzt erreicht')), h('p', { class: 'small' }, obj.networkIp)) : null);
}

/**
 * Pico für WLAN einrichten: Pico per USB an den Server, WLAN-Zugang eintragen. Der Server spielt Programm, Rolle,
 * WLAN-Zugang und einen neuen Schlüssel auf; das WLAN-Passwort speichert er selbst nicht.
 */
async function picoWlanSetup(role) {
  const ports = await run(() => api('/fans/ports'));
  if (!ports) return;
  const port = h('select', { style: 'width:auto' }, [['auto', t('automatisch')], ...ports.pico.map(p => [p, p])].map(([v, l]) => h('option', { value: v }, l)));
  const ssid = h('input', { autocomplete: 'off', maxlength: 32 });
  const pw = h('input', { type: 'password', autocomplete: 'new-password', maxlength: 63 });
  const host = h('input', { value: role === 'display' ? 'bitaxetuner-display' : 'bitaxetuner-fans', maxlength: 32 });
  const body = h('div', { class: 'stack' },
    h('p', {}, role === 'display'
      ? t('Display-Pico (Pico 2 WH, auf das E-Paper gesteckt) jetzt per USB-Datenkabel an diesen Server anschließen.')
      : t('Lüfter-Pico (Pico 2 WH auf der Lüfterplatine) jetzt per USB-Datenkabel an diesen Server anschließen.')),
    h('div', {}, h('label', {}, t('USB-Port')), port,
      ports.pico.length === 0 ? h('p', { class: 'small warn' }, t('Gerade kein Pico am USB erkannt – anschließen und den Dialog neu öffnen.')) : null),
    h('div', {}, h('label', {}, t('WLAN-Name (SSID, 2,4 GHz)')), ssid),
    h('div', {}, h('label', {}, t('WLAN-Passwort')), pw),
    h('div', {}, h('label', {}, t('Gerätename im Heimnetz')), host),
    h('p', { class: 'muted small' }, t('Aufgespielt werden Programm, Rolle, WLAN-Zugang und ein neuer Schlüssel. Das WLAN-Passwort steht danach nur auf dem Pico, nicht auf dem Server. Dauer ca. 30 Sekunden; Lüfter laufen dabei mit 100 %.')));
  if (!await confirmBox(t('Pico für WLAN einrichten'), body, t('Einrichten'))) return;
  toast(t('Pico wird eingerichtet …'), 'info', 30000);
  const r = await run(() => api('/fans/wlan-setup', { method: 'POST', body: { role, port: port.value, ssid: ssid.value, password: pw.value, host: host.value } }));
  pw.value = '';
  if (!r) return;
  toast(r.ip ? t('Eingerichtet: {0} ({1}). Pico jetzt vom Server trennen und an sein eigenes Netzteil.', r.host, r.ip)
    : t('Eingerichtet: {0}. Der Pico hat sich noch nicht im WLAN gemeldet – Name und Passwort prüfen (nur 2,4 GHz).', r.host), r.ip ? 'ok' : 'error', 20000);
  renderFans();
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
/**
 * Push-Ziele: mehrere Dienste gleichzeitig (z. B. ntfy privat + Discord für die Community), je Ziel eigene
 * Bereiche und Miner. Gespeichert wird mit „Einstellungen speichern“.
 */
function pushTargetsEditor(nt, text, select, devices, save) {
  const cats = [['Offline', t('offline')], ['Overheat', t('Überhitzung')], ['Finds', t('Blockfund/Zufluss')], ['Maintenance', t('Watchdog/Automatik/Firmware')],
    ['Record', t('Rekorde')], ['LogAlerts', t('Log-Alarme')], ['Pool', t('Pool')], ['Plugs', t('Smart Plugs')], ['Health', t('Gesundheit')],
    ['DailyReport', t('Tagesbericht')], ['MonthlyReport', t('Monatsbericht')]];
  // Teile von Tages-/Monatsbericht je Dienst (Schlüssel wie Core/Config/PushTarget.cs → ReportParts)
  const parts = [['costs', t('Stromkosten')], ['income', t('Zuflüsse (Steuer)')], ['tips', t('Empfehlungen des Ratgebers')],
    ['bestDiff', t('Best-Diff-Rekord')], ['tuning', t('Tuning-Änderungen')], ['plugs', t('Steckdosen-Details')]];
  nt.targets = nt.targets || [];
  const box = h('div', { class: 'stack' });
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(4)), b => b.toString(16).padStart(2, '0')).join('');
  const draw = () => fill(box, nt.targets.length ? nt.targets.map((tg, i) => {
    tg.categories = tg.categories || [];
    tg.miners = tg.miners || [];
    const catBox = h('div', { class: 'row', style: 'flex-wrap:wrap' }, cats.map(([k, label]) => h('label', { class: 'row' },
      h('input', { type: 'checkbox', checked: tg.categories.includes(k), onchange: e => { tg.categories = e.target.checked ? [...tg.categories, k] : tg.categories.filter(x => x !== k); } }),
      h('span', {}, label))));
    const minerList = h('div', { class: 'row', style: 'flex-wrap:wrap' }, devices.filter(d => d.host).map(d => h('label', { class: 'row' },
      h('input', { type: 'checkbox', checked: tg.miners.includes(d.host), onchange: e => { tg.miners = e.target.checked ? [...tg.miners, d.host] : tg.miners.filter(x => x !== d.host); } }),
      h('span', {}, d.name))));
    minerList.style.display = tg.miners.length || tg.groups?.length ? '' : 'none';
    const allMiners = h('label', { class: 'row' },
      h('input', { type: 'checkbox', checked: !tg.miners.length && !tg.groups?.length, onchange: e => { if (e.target.checked) { tg.miners = []; tg.groups = []; draw(); } else { minerList.style.display = ''; if (groupBox) groupBox.style.display = ''; } } }),
      h('span', {}, t('alle Miner')));
    tg.reportExclude = tg.reportExclude || [];
    tg.groups = tg.groups || [];
    const allGroups = [...new Set(devices.flatMap(d => d.groups || []))].sort((a, b) => a.localeCompare(b));
    const groupBox = allGroups.length ? h('div', { class: 'row', style: 'flex-wrap:wrap' }, h('span', { class: 'small muted' }, t('oder ganze Gruppen:')), allGroups.map(g => h('label', { class: 'row' },
      h('input', { type: 'checkbox', checked: tg.groups.some(x => x.toLowerCase() === g.toLowerCase()), onchange: e => { tg.groups = e.target.checked ? [...tg.groups, g] : tg.groups.filter(x => x.toLowerCase() !== g.toLowerCase()); } }),
      h('span', {}, g)))) : null;
    const partBox = h('div', { class: 'row', style: 'flex-wrap:wrap' }, parts.map(([k, label]) => h('label', { class: 'row' },
      h('input', { type: 'checkbox', checked: !tg.reportExclude.includes(k), onchange: e => { tg.reportExclude = e.target.checked ? tg.reportExclude.filter(x => x !== k) : [...tg.reportExclude, k]; } }),
      h('span', {}, label))));
    const test = async () => {
      await save();
      const r = await run(() => api('/notifications/test', { method: 'POST', body: { targetId: tg.id } }));
      if (r) toast(r.ok ? t('Testnachricht an „{0}“ gesendet.', tg.name || tg.provider) : r.error, r.ok ? 'ok' : 'error');
    };
    return h('div', { class: 'card stack' },
      h('div', { class: 'row' },
        h('input', { value: tg.name || '', placeholder: t('Name, z. B. Privat oder Community'), style: 'flex:1', oninput: e => { tg.name = e.target.value; } }),
        checkInput(tg, 'enabled', t('aktiv')),
        h('button', { class: 'btn small', onclick: test }, t('Speichern & testen')),
        h('button', { class: 'btn small danger', onclick: () => { nt.targets.splice(i, 1); draw(); } }, t('Entfernen'))),
      notifyForm(tg, text, select, false),
      h('div', { class: 'small muted' }, t('Meldungen')), catBox,
      h('div', { class: 'small muted' }, t('Miner')), allMiners, minerList, groupBox,
      h('div', { class: 'small muted' }, t('Tages- und Monatsbericht enthalten')), partBox,
      h('div', { class: 'small muted' }, t('Mit Miner-Auswahl enthalten die Berichte nur diese Miner (Summen und Kosten nur für sie) und nie Zuflüsse.')));
  }) : h('p', { class: 'muted small' }, t('Noch kein Push-Dienst eingerichtet.')));
  draw();
  return h('div', { class: 'stack' },
    h('p', { class: 'muted small' }, t('Mehrere Dienste gleichzeitig möglich – z. B. ntfy für dich und Discord für eine Community-Gruppe. Je Dienst wählst du die Meldungen und die Miner.')),
    box,
    h('div', { class: 'row' }, h('button', { class: 'btn', onclick: () => {
      nt.targets.push({ id: newId(), name: '', enabled: true, provider: 'ntfy', ntfyServer: 'https://ntfy.sh', ntfyTopic: '',
        categories: cats.map(c => c[0]).filter(c => c !== 'Record'), miners: [] });
      draw();
    } }, t('Push-Dienst hinzufügen'))));
}

function notifyForm(nt, text, select, withNone = true) {
  const field = (provider, label, el) => { const d = h('div', { 'data-provider': provider }, h('label', {}, label), el); return d; };
  const fields = [
    field('ntfy', t('ntfy-Server'), text(nt, 'ntfyServer')), field('ntfy', t('ntfy-Topic'), text(nt, 'ntfyTopic')),
    field('telegram', t('Telegram-Bot-Token'), text(nt, 'telegramBotToken', 'password')), field('telegram', t('Telegram-Chat-ID'), text(nt, 'telegramChatId')),
    field('discord', t('Discord-Webhook-URL'), text(nt, 'discordWebhookUrl', 'password')),
    field('pushover', t('Pushover-User-Key'), text(nt, 'pushoverUserKey', 'password')), field('pushover', t('Pushover-App-Token'), text(nt, 'pushoverAppToken', 'password')),
    field('webhook', t('Webhook-URL (JSON-POST: title, message, priority)'), text(nt, 'webhookUrl')),
  ];
  const show = () => fields.forEach(f => { f.style.display = f.dataset.provider === nt.provider ? '' : 'none'; });
  const sel = select(nt, 'provider', [...(withNone ? [['none', t('aus')]] : []), ['ntfy', 'ntfy'], ['telegram', 'Telegram'], ['discord', 'Discord'], ['pushover', 'Pushover'], ['webhook', t('Eigener Webhook')]]);
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
      checkInput(f, 'enabled', t('Lüfter regeln (Pico am Server per USB oder über WLAN; er bedient auch Anzeige und Taster, wenn die Anzeige keinen eigenen Pico hat)')),
      picoConnectionInputs(f, 'fans'),
      h('div', { class: 'row' },
        h('button', { class: 'btn primary', onclick: save }, t('Speichern')),
        h('button', { class: 'btn', onclick: () => picoWlanSetup('fans') }, t('Lüfter-Pico für WLAN einrichten …')),
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
const RESTORE_WARNING = () => t('Ersetzt Einstellungen, Verlauf (history.db), Steuerdaten, Benchmark-Ergebnisse und Miner-Sicherungen durch den Stand der Sicherung. Der jetzige Stand wird vorher im Datenordner aufbewahrt (Ordner „backup-…“). Sicherungsziele, MQTT und Passwörter bleiben, wie sie sind. Die Miner selbst werden nicht verändert.');

/** Sicherung aus dem Datenordner einspielen (nach Bestätigung). */
async function restoreBackup(name) {
  if (!await confirmBox(t('Sicherung einspielen'), t('Sicherung {0} einspielen?', name) + '\n\n' + RESTORE_WARNING(), t('Einspielen'))) return;
  toast(t('Sicherung wird geprüft und eingespielt …'), 'info');
  const r = await run(() => api(`/backup/files/${encodeURIComponent(name)}/restore`, { method: 'POST', body: {} }));
  if (r) { toast(r.message, 'ok', 15000); setTimeout(() => location.reload(), 2500); }
}

/**
 * Sicherungsdatei vom PC hochladen und einspielen – z. B. nach Ausfall der SD-Karte von USB-Stick, NAS oder
 * aus „Dokumente\BitaxeTuner-Sicherungen“. Gleicher geprüfter Weg wie die Übernahme aus der Desktop-App.
 */
function restoreUploadBox() {
  const file = h('input', { type: 'file', accept: '.zip,application/zip' });
  const go = async () => {
    const f = file.files[0];
    if (!f) { toast(t('Bitte zuerst eine Sicherungsdatei (.zip) wählen.'), 'error'); return; }
    if (!await confirmBox(t('Sicherung einspielen'), t('Datei {0} einspielen?', f.name) + '\n\n' + RESTORE_WARNING(), t('Einspielen'))) return;
    toast(t('Lade hoch, prüfe und spiele ein …'), 'info');
    const r = await run(async () => {
      const res = await fetch('/api/v1/admin/import?replace=true', {
        method: 'POST', credentials: 'same-origin', body: f,
        headers: { 'Content-Type': 'application/zip', 'Accept-Language': LANG, ...(S.csrf ? { 'X-CSRF-Token': S.csrf } : {}) },
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) throw new ApiError(data?.error || t('Fehler {0}', res.status), res.status);
      return data;
    });
    if (r) { toast(r.message, 'ok', 15000); setTimeout(() => location.reload(), 2500); }
  };
  return h('div', { class: 'stack' },
    h('h3', {}, t('Sicherung einspielen')),
    h('p', { class: 'muted small' }, t('Eine Sicherungsdatei (bitaxetuner-backup-….zip) vom PC, USB-Stick oder NAS hochladen – z. B. auf einem neu aufgesetzten Server. Sie wird vor dem Einspielen vollständig geprüft.')),
    h('div', { class: 'row' }, file, h('button', { class: 'btn', onclick: go }, t('Hochladen und einspielen …'))));
}

/** HTTP ↔ HTTPS umstellen (Audit S4): Wahl speichern, Server startet neu, danach auf die neue Adresse wechseln. */
async function switchHttps(enable) {
  const text = enable
    ? t('Server auf HTTPS umstellen?\n\nDanach sind Passwort, Token und Sitzung im Heimnetz verschlüsselt. Der Server startet dabei neu (etwa 10–20 Sekunden); die Miner laufen weiter.\n\nDer Browser zeigt beim ersten Aufruf eine Warnung wegen des selbst signierten Zertifikats – „Erweitert“ → „Weiter“ ist hier in Ordnung. Die Desktop-App übernimmt die Umstellung über ihren eigenen Knopf bzw. „Verbindung testen“.')
    : t('Server wieder auf HTTP (unverschlüsselt) umstellen?\n\nPasswort, Token und Sitzung gehen dann wieder unverschlüsselt durchs Heimnetz. Der Server startet neu.');
  if (!await confirmBox(enable ? t('Auf HTTPS umstellen') : t('Auf HTTP umstellen'), text, enable ? t('Umstellen') : t('Trotzdem umstellen'), !enable)) return;
  const r = await run(() => api('/admin/https', { method: 'POST', body: { enable } }));
  if (!r?.restarting) return;
  const target = `${enable ? 'https' : 'http'}://${location.hostname}:${r.port || location.port}/`;
  toast(t('Server startet neu – in etwa 15 Sekunden geht es unter {0} weiter.', target), 'ok', 20000);
  setTimeout(() => { location.href = target; }, 15000);
}

function httpsBanner() {
  return h('div', { class: 'banner stack' },
    h('b', {}, t('Verbindung unverschlüsselt (HTTP)')),
    h('p', { class: 'small' }, t('Admin-Passwort, Token und Sitzung gehen im Heimnetz bisher unverschlüsselt. Mit HTTPS (selbst signiertes Zertifikat) sind sie verschlüsselt.')),
    h('div', { class: 'row' },
      h('button', { class: 'btn primary', onclick: () => switchHttps(true) }, t('Auf HTTPS umstellen …')),
      h('button', { class: 'btn', onclick: () => { localStorageSet('httpsHint', 'later'); route(); } }, t('Später'))));
}

/** Einstellungen → Verbindung: Stand HTTP/HTTPS, Fingerabdruck, umstellen. */
function connectionCard() {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  api('/admin/https').then(c => {
    S.https = c;
    fill(body,
      h('p', {}, c.enabled ? h('span', { class: 'ok' }, t('Verschlüsselt (HTTPS, selbst signiertes Zertifikat).')) : h('span', { class: 'warn' }, t('Unverschlüsselt (HTTP).'))),
      c.fingerprint ? h('p', { class: 'small' }, t('Fingerabdruck (SHA-256): '), h('code', {}, c.fingerprint)) : null,
      c.configurable
        ? h('div', { class: 'row' }, h('button', { class: c.enabled ? 'btn' : 'btn primary', onclick: () => switchHttps(!c.enabled) },
            c.enabled ? t('Auf HTTP umstellen …') : t('Auf HTTPS umstellen …')))
        : h('p', { class: 'muted small' }, t('Beim Start fest vorgegeben (--https bzw. BITAXETUNER_HTTPS) – hier nicht umschaltbar.')));
  }).catch(e => fill(body, h('p', { class: 'danger' }, e.message)));
  return h('div', { class: 'card stack' }, h('h2', {}, t('Verbindung')),
    h('p', { class: 'muted small' }, t('Neue Installationen starten verschlüsselt. Den Fingerabdruck kannst du mit der Warnung im Browser bzw. der Rückfrage der Desktop-App vergleichen.')),
    body);
}

/** Prometheus/Grafana: /metrics ein-/ausschalten, Token erzeugen (nur einmal sichtbar), Beispiel für prometheus.yml. */
function metricsCard() {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  const load = async () => {
    const m = await api('/metrics/settings').catch(e => { fill(body, h('p', { class: 'danger' }, e.message)); return null; });
    if (!m) return;
    const url = `${location.origin}/metrics`;
    const example = [
      'scrape_configs:',
      '  - job_name: bitaxetuner',
      `    scheme: ${location.protocol.replace(':', '')}`,
      '    metrics_path: /metrics',
      '    bearer_token: "btm_…"',
      ...(location.protocol === 'https:' ? ['    tls_config:', '      insecure_skip_verify: true   # selbst signiertes Zertifikat'] : []),
      '    static_configs:',
      `      - targets: ["${location.host}"]`,
    ].join('\n');
    const toggle = h('input', { type: 'checkbox', checked: m.enabled, onchange: async e => {
      if (await run(() => api('/metrics/settings', { method: 'PUT', body: { enabled: e.target.checked } }),
        e.target.checked ? t('Prometheus-Export eingeschaltet.') : t('Prometheus-Export ausgeschaltet.'))) load();
    } });
    const newToken = async () => {
      if (m.tokenSet && !await confirmBox(t('Neues Token'), t('Ein neues Token ersetzt das bisherige – Prometheus muss dann das neue bekommen. Fortfahren?'), t('Neues Token'))) return;
      const r = await run(() => api('/metrics/token', { method: 'POST', body: {} }));
      if (!r) return;
      await confirmBox(t('Prometheus-Token'), h('div', { class: 'stack' }, h('p', {}, t('Jetzt kopieren – es wird nicht noch einmal angezeigt:')),
        h('input', { value: r.token, readonly: true, onfocus: e => e.target.select() })), t('Fertig'));
      load();
    };
    fill(body,
      h('label', { class: 'check' }, toggle, ' ', t('Prometheus-Export einschalten')),
      h('p', { class: 'small' }, t('Adresse: '), h('code', {}, url), ' · ',
        m.tokenSet ? t('Token erzeugt am {0}', time(m.tokenCreatedUtc)) : h('span', { class: 'warn' }, t('noch kein Token – ohne Token liefert /metrics nichts'))),
      h('div', { class: 'row' }, h('button', { class: 'btn', onclick: newToken }, m.tokenSet ? t('Neues Token erzeugen …') : t('Token erzeugen'))),
      h('details', {}, h('summary', {}, t('Beispiel für prometheus.yml')), h('pre', { class: 'small' }, example)));
  };
  load();
  return h('div', { class: 'card stack' }, h('h2', {}, t('Prometheus / Grafana')),
    h('p', { class: 'muted small' }, t('Messwerte für eigene Grafana-Dashboards: Hashrate, Temperaturen, Leistung, Lüfter, Shares, Smart Plugs und Kosten je Miner. Ohne IP- und Wallet-Adressen; Miner erscheinen mit Namen. Standardmäßig aus, Zugriff nur mit Token.')),
    body);
}

/** Eigene Ansicht-Zugänge: PIN je Person, optional nur bestimmte Gruppen; einzeln widerrufbar. */
function viewersCard(groups) {
  const body = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Lade …')));
  const load = async () => {
    const list = await api('/viewers').catch(e => { fill(body, h('p', { class: 'danger' }, e.message)); return null; });
    if (!list) return;
    const name = h('input', { placeholder: t('z. B. Werkstatt') });
    const pin = h('input', { type: 'password', inputmode: 'numeric', autocomplete: 'new-password', placeholder: t('mind. 6 Ziffern') });
    const picks = groups.map(g => ({ g, box: h('input', { type: 'checkbox' }) }));
    const add = async () => {
      const chosen = picks.filter(p => p.box.checked).map(p => p.g);
      const r = await run(() => api('/viewers', { method: 'POST', body: { name: name.value, pin: pin.value, groups: chosen } }),
        t('Ansicht-Zugang angelegt.'));
      if (r) load();
    };
    const revoke = async v => {
      if (!await confirmBox(t('Zugang widerrufen'), t('Ansicht-Zugang „{0}“ widerrufen? Wer damit angemeldet ist, wird sofort abgemeldet.', v.name), t('Widerrufen'), true)) return;
      if (await run(() => api(`/viewers/${encodeURIComponent(v.id)}`, { method: 'DELETE' }), t('Zugang widerrufen.'))) load();
    };
    fill(body,
      list.length ? h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, [t('Name'), t('sieht'), t('erstellt'), t('zuletzt benutzt'), ''].map(x => h('th', {}, x)))),
        h('tbody', {}, list.map(v => h('tr', {},
          h('td', {}, v.name),
          h('td', {}, v.groups.length ? v.groups.join(', ') : t('alle Miner')),
          h('td', { class: 'nowrap' }, time(v.createdUtc)),
          h('td', { class: 'nowrap' }, v.lastUsedUtc ? time(v.lastUsedUtc) : t('noch nie')),
          h('td', {}, h('button', { class: 'btn small danger', onclick: () => revoke(v) }, t('Widerrufen')))))))) : h('p', { class: 'muted small' }, t('Noch keine eigenen Zugänge.')),
      h('div', { class: 'form' }, h('div', {}, h('label', {}, t('Name')), name), h('div', {}, h('label', {}, t('PIN')), pin)),
      groups.length
        ? h('div', { class: 'stack' }, h('label', {}, t('Nur diese Gruppen (keine = alle Miner)')),
            h('div', { class: 'row' }, picks.map(p => h('label', { class: 'check' }, p.box, ' ', p.g))))
        : h('p', { class: 'muted small' }, t('Ohne Gruppen sieht der Zugang alle Miner. Gruppen legst du je Miner unter Einstellungen → Geräte fest.')),
      h('div', { class: 'row' }, h('button', { class: 'btn', onclick: add }, t('Zugang anlegen'))));
  };
  load();
  return h('div', { class: 'card stack' }, h('h2', {}, t('Ansicht-Zugänge')),
    h('p', { class: 'muted small' }, t('Eigene PIN je Person, nur zum Ansehen. Mit Gruppen sieht der Zugang nur diese Miner – ohne Summen, Verläufe und E-Paper über alle Miner. Die allgemeine PIN oben sieht weiterhin alles. Anmelden, Anlegen und Widerrufen stehen im Protokoll.')),
    body);
}

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
              h('td', { class: 'row' },
                h('a', { class: 'btn small', href: `/api/v1/backup/files/${encodeURIComponent(f.name)}`, download: f.name }, t('Herunterladen')),
                h('button', { class: 'btn small', onclick: () => restoreBackup(f.name) }, t('Einspielen …'))))))))
        : null,
      restoreUploadBox());
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
    u.latest && u.canInstall ? h('label', { class: 'check' }, download, t('Sicherung vorher auf diesen PC herunterladen')) : null,
    h('div', { class: 'row' },
      h('button', { class: 'btn', onclick: async () => { const r = await run(() => api('/admin/update/check', { method: 'POST', body: {} })); if (r) load(); } }, t('Nach Updates suchen')),
      u.latest && u.canInstall ? h('button', {
        class: 'btn primary', onclick: async () => {
          if (!await confirmBox(t('Server aktualisieren'), t('BitaxeTuner-Server {0} installieren?\n\nVorher wird eine geprüfte Sicherung erstellt (Datenordner und eingerichtete Ziele wie USB-Stick oder NAS) – schlägt sie fehl, wird nichts installiert. Die Datei wird gegen die veröffentlichte SHA-256-Prüfsumme geprüft. Laufende Benchmarks werden gestoppt (Einstellungen wiederhergestellt), danach startet der Server neu. Die Seite verbindet sich anschließend von selbst wieder.', u.latest), t('Installieren'))) return;
          localStorageSet('updateDownload', download.checked ? '1' : '0');
          toast(t('Sicherung wird erstellt …'), 'info');
          const b = await run(() => api('/backup/run', { method: 'POST', body: {} }));
          if (!b) return;
          if (!b.lastOk || !b.lastFile) { toast(t('Update abgebrochen: Sicherung mit Fehlern – siehe Einstellungen → Sicherung.'), 'error', 15000); return; }
          if (download.checked) {
            // Vor dem Neustart herunterladen (in der Desktop-App landet sie im Sicherungsordner der App)
            h('a', { href: `/api/v1/backup/files/${encodeURIComponent(b.lastFile)}`, download: b.lastFile }).click();
            await new Promise(res => setTimeout(res, 4000));
          }
          const r = await run(() => api('/admin/update/install', { method: 'POST', body: {} }));
          if (r) toast(r.message, 'ok', 15000);
        },
      }, t('Update {0} installieren', u.latest)) : null));
  const download = h('input', { type: 'checkbox', checked: localStorageGet('updateDownload') !== '0' });
  const load = () => api('/admin/update').then(u => { S.update = u; render(u); }).catch(e => toast(e.message, 'error'));
  load();
  return h('div', { class: 'card stack' }, h('h2', {}, t('Server-Update')), body);
}

boot().catch(e =>mount(h('div', { class: 'card danger' }, t('Server nicht erreichbar: '), e.message)));
