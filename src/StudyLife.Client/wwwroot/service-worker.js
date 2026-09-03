// Service worker for StudyLife PWA (dev build - dotnet run, not dotnet publish).
// Network-first for anything that isn't a hash-versioned build asset, so local code
// changes to index.html/routed pages are always picked up on reload; only falls back
// to the cache when genuinely offline. Mirrors service-worker.published.js - the
// previous cache-first-forever strategy here meant a browser that had ever loaded the
// app once would keep serving that exact index.html indefinitely, even after
// rebuilding, since nothing ever invalidated the cache entry. Hash-versioned assets
// (see importScripts below) don't have that problem: a rebuild that changes their
// content also changes their URL, so precaching them can't go stale the same way.
// Bumped to v4: see service-worker.published.js for why (the fetch handler used to cache
// navigation responses regardless of HTTP status, so a single transient 502/500 could get
// stuck as the permanent offline fallback for "/").
const CACHE_NAME = 'studylife-dev-cache-v5';

// Blazor's build generates service-worker-assets.js (self.assetsManifest) even for dev
// builds - see service-worker.published.js for the full rationale on why importing it
// to precache hash-versioned assets at install time is safe here too.
self.importScripts('service-worker-assets.js');

// Not everything in the manifest is worth downloading on install. The manifest lists all 26
// languages' i18n tables (416 JSON files, ~1.7 MB raw) plus debug symbols and source maps, and
// cache.addAll fetches them all while the WASM runtime itself is still downloading - on a first
// visit that is ~400 extra requests competing for the same connection. Only the tables of the
// default language and the English fallback are precached; every other language still loads
// normally from the network when selected (network-first below), it just isn't available
// offline. Bumped CACHE_NAME to v5 so existing installs drop the old, larger precache.
const OFFLINE_I18N_LANGUAGES = ['de', 'en'];
function shouldPrecache(asset) {
    if (/\.(pdb|map)$/i.test(asset.url)) return false;
    const i18n = asset.url.match(/_content\/i18ntext\/.*\.([a-z]{2})\.json$/);
    if (i18n) return OFFLINE_I18N_LANGUAGES.includes(i18n[1]);
    return true;
}

self.addEventListener('install', event => event.waitUntil(
    caches.open(CACHE_NAME)
        .then(cache => cache.addAll(self.assetsManifest.assets.filter(shouldPrecache).map(
            asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' })
        )))
        .then(() => self.skipWaiting())
));
self.addEventListener('activate', event => event.waitUntil(
    Promise.all([
        self.clients.claim(),
        caches.keys().then(keys => Promise.all(
            keys.filter(key => key !== CACHE_NAME).map(key => caches.delete(key))
        )),
    ])
));

// See service-worker.published.js for the full rationale: precached hash-versioned
// assets are served straight from cache, everything else (API calls, navigation) is
// network-first with no API-response caching, and only navigation gets an offline
// cache fallback.
self.addEventListener('fetch', event => {
    if (event.request.method !== 'GET') return;

    event.respondWith(
        caches.open(CACHE_NAME).then(async cache => {
            const precached = await cache.match(event.request);
            if (precached) return precached;

            const isNavigation = event.request.mode === 'navigate';
            try {
                const response = await fetch(event.request);
                // fetch() only rejects on network-level failures - an HTTP error status
                // is a "successful" fetch as far as it's concerned. Caching one of those
                // as the offline fallback would mean a transient server hiccup gets
                // served back forever.
                if (isNavigation && response.ok) cache.put(event.request, response.clone());
                return response;
            } catch (err) {
                if (isNavigation) {
                    const fallback = await cache.match(event.request);
                    if (fallback) return fallback;
                }
                throw err;
            }
        })
    );
});

self.addEventListener('push', event => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch { data = { title: 'StudyLife', body: event.data ? event.data.text() : '' }; }

    // iOS Safari (16.4+) strictly requires a visible notification per push event
    const title = data.title || 'StudyLife';
    const options = {
        body: data.body || '',
        icon: '/icons/icon-192.png',
        badge: '/icons/icon-192.png',
        tag: 'studylife-session',
        renotify: true,
        requireInteraction: false,
        data: data.url ? { url: data.url } : {}
    };

    event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(clients => {
            if (clients.length > 0) return clients[0].focus();
            return self.clients.openWindow('/');
        })
    );
});

