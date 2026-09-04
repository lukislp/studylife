// Published service worker
// CACHE_NAME is versioned so that a stale cache entry (served once as a fallback after a
// failed network fetch - common on flaky mobile connections) can't persist indefinitely:
// bumping this string forces a byte-level SW update, which purges every other cache key
// in `activate` below, instead of relying on individual entries ever being refreshed.
// Bumped to v4: the fetch handler used to cache.put() a navigation response regardless of
// its HTTP status - fetch() only rejects on network-level failures, not on 4xx/5xx, so a
// single transient 502/500 (e.g. during a deploy) got cached as the permanent offline
// fallback for "/" and kept being served forever afterwards, even once the server was
// healthy again. The version bump purges that bad entry for anyone who already hit it.
const CACHE_NAME = 'studylife-cache-v5';

// Blazor's build generates service-worker-assets.js (self.assetsManifest) listing every
// build-output static asset for this exact deployed version, most of them content-hashed
// into their own URL. Importing it lets us precache them once at install time so they
// can be served straight from cache afterwards with zero network round-trip: a content
// change always bakes a new hash into the URL, so a cached entry here can never go stale.
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

// Precache with retries instead of one cache.addAll: during a rolling deployment old and new
// pods answer side by side for a few minutes, and an asset listed in the NEW manifest gets a
// 404 (empty body -> integrity failure) from an OLD pod. addAll would fail the whole install on
// the first such asset and the update would silently never arrive; a retry with backoff lets
// each asset be re-routed until it reaches a new pod (js/boot-start.js does the same for the
// page's own framework downloads).
const PRECACHE_RETRY_DELAYS_MS = [1000, 2000, 4000, 8000, 15000, 30000, 30000, 30000];

async function fetchForPrecache(asset) {
    const request = new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' });
    for (let attempt = 0; ; attempt++) {
        try {
            const response = await fetch(request.clone());
            if (response.ok) return { request, response };
            if (attempt >= PRECACHE_RETRY_DELAYS_MS.length) throw new Error('precache ' + asset.url + ' -> HTTP ' + response.status);
        } catch (err) {
            if (attempt >= PRECACHE_RETRY_DELAYS_MS.length) throw err;
        }
        await new Promise(resolve => setTimeout(resolve, PRECACHE_RETRY_DELAYS_MS[attempt]));
    }
}

self.addEventListener('install', event => event.waitUntil(
    caches.open(CACHE_NAME)
        .then(cache => Promise.all(self.assetsManifest.assets.filter(shouldPrecache).map(
            asset => fetchForPrecache(asset).then(({ request, response }) => cache.put(request, response))
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

// Two different strategies depending on what's being requested:
// - Assets covered by the precache above (static, hash-versioned build output): serve
//   straight from cache, no network involved at all - see the WHY-comment above
//   importScripts.
// - Everything else (api/* calls, navigation requests for index.html/routed pages):
//   network-first, so the app always shows fresh data/markup while online. API
//   responses are deliberately never cached - if a fetch fails while offline it must
//   surface as a failure, not silently hand back stale JSON with no indication it's out
//   of date. Only navigation requests get a cache fallback for genuinely-offline use, so
//   the app shell still opens to something instead of a bare network error.
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
                // (502 from a mid-deploy nginx, 500, etc.) is a "successful" fetch as far
                // as it's concerned. Caching one of those as the offline fallback would
                // mean a single transient server hiccup gets served back forever.
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
