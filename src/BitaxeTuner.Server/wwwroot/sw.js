// BitaxeTuner Service Worker (0.9.12): installierbare App und Push im Browser.
// Die Oberfläche kommt immer frisch vom Server (Netz zuerst) – nur ohne Netz die zuletzt geladene Fassung,
// damit nach einem Update nie eine alte Oberfläche zur neuen API passt.
const CACHE = 'bitaxetuner-shell';
const SHELL = ['./', 'index.html', 'app.js', 'app.css', 'icon.svg', 'icon-192.png', 'icon-512.png', 'manifest.webmanifest'];

self.addEventListener('install', e => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL)).catch(() => {}).then(() => self.skipWaiting()));
});

self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));

self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  // API und Live-Ereignisse nie aus dem Zwischenspeicher
  if (e.request.method !== 'GET' || url.origin !== location.origin || url.pathname.startsWith('/api/')) return;
  e.respondWith(fetch(e.request).then(r => {
    if (r.ok && SHELL.some(p => url.pathname.endsWith(p.replace('./', '/')))) {
      const copy = r.clone();
      caches.open(CACHE).then(c => c.put(e.request, copy)).catch(() => {});
    }
    return r;
  }).catch(() => caches.match(e.request).then(r => r || caches.match('index.html'))));
});

self.addEventListener('push', e => {
  let d = {};
  try { d = e.data ? e.data.json() : {}; } catch { d = { title: 'BitaxeTuner', body: e.data ? e.data.text() : '' }; }
  e.waitUntil(self.registration.showNotification(d.title || 'BitaxeTuner', {
    body: d.body || '', tag: d.tag, icon: 'icon-192.png', badge: 'icon-192.png', data: { url: d.url || './' },
  }));
});

self.addEventListener('notificationclick', e => {
  e.notification.close();
  const target = new URL(e.notification.data?.url || './', self.registration.scope).href;
  e.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(list => {
    const open = list.find(c => c.url.startsWith(self.registration.scope));
    return open ? open.focus() : self.clients.openWindow(target);
  }));
});
