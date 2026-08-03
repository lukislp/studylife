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
const CACHE_NAME = 'studylife-cache-v4';

// Blazor's build generates service-worker-assets.js (self.assetsManifest) listing every
// build-output static asset for this exact deployed version, most of them content-hashed
// into their own URL. Importing it lets us precache them once at install time so they
// can be served straight from cache afterwards with zero network round-trip: a content
// change always bakes a new hash into the URL, so a cached entry here can never go stale.
self.importScripts('service-worker-assets.js');

self.addEventListener('install', event => event.waitUntil(
    caches.open(CACHE_NAME)
        .then(cache => cache.addAll(self.assetsManifest.assets.map(
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
