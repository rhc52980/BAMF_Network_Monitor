// BAMF's service worker. It does one job: when the server can't be reached,
// a page opened from the home screen shows "can't reach BAMF" instead of the
// browser's error page. It never answers for the dashboard, the API or the
// sign-in: those always go to the server, so nothing here can show stale
// devices or keep a signed-out session looking signed in.
//
// Change CACHE when a file in SHELL changes, so the old copies are dropped.
const CACHE = "bamf-shell-1";
const OFFLINE = "/offline.html";
const SHELL = [OFFLINE, "/bamf-icon.svg", "/fonts.css", "/icon-192.png"];

self.addEventListener("install", e => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", e => {
  e.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", e => {
  const req = e.request;
  // Only a page being opened. Everything else (scripts, /api, POSTs, the
  // sign-in) is left alone and goes straight to the network.
  if (req.method !== "GET" || req.mode !== "navigate") return;
  e.respondWith(fetch(req).catch(() => caches.match(OFFLINE)));
});
