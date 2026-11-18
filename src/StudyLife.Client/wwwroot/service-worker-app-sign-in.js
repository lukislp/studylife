// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

// Native app sign-in pages (?app=1, see AppReturnContext) and the service worker.
//
// The app opens /login?app=1&appchallenge=... (or /register, /link) in the system browser, which
// shares this origin's service worker with every earlier visit. The worker serves its precached
// bundle first, so after a server update the sign-in page ran the client the browser last
// precached, not the one the server now speaks with. Device test round 8 (2026-11-17) hit exactly
// that: a 3.34.7 bundle sent its handoff in the pre-audit shape (token in the body, no session
// header) to a 3.34.20 server, and every passkey and password sign-in ended in a 401. The main app
// has its own update flow (Shared/MainLayout.razor.js); these pages have none, they live for one
// sign-in and need the server anyway.
//
// Two rules, used by both workers (service-worker.js, service-worker.published.js):
// - The page and everything it loads come from the network, never from the precache, so the page
//   always runs the bundle that matches the server. The worker remembers which pages it served
//   that way (APP_SIGN_IN_FRESH_CACHE).
// - On activation, a new worker reloads every sign-in page an older worker served from its
//   precache, before the user can complete a sign-in with the stale bundle. That also covers the
//   workers released before these rules: their pages carry no fresh-mark.

const APP_SIGN_IN_FRESH_CACHE = 'studylife-app-sign-in-fresh';

function isAppSignInUrl(url) {
    try {
        return new URL(url).searchParams.get('app') === '1';
    } catch {
        return false;
    }
}

function appSignInFreshKey(clientId) {
    return new Request('app-sign-in-fresh/' + encodeURIComponent(clientId));
}

// True when the request is the navigation to a sign-in page or a request that page makes.
async function isAppSignInRequest(event) {
    if (event.request.mode === 'navigate') return isAppSignInUrl(event.request.url);
    if (!event.clientId) return false;
    const client = await self.clients.get(event.clientId);
    return client !== undefined && isAppSignInUrl(client.url);
}

// Network only; a navigation marks the page it creates as served fresh.
async function fetchForAppSignIn(event) {
    if (event.request.mode === 'navigate' && event.resultingClientId) {
        const fresh = await caches.open(APP_SIGN_IN_FRESH_CACHE);
        await fresh.put(appSignInFreshKey(event.resultingClientId), new Response(''));
    }
    return fetch(event.request);
}

// Call in `activate` BEFORE clients.claim(): until then matchAll() lists only the pages a previous
// worker controls (pages that loaded without any worker came from the network anyway).
async function findStaleAppSignInPages() {
    const pages = (await self.clients.matchAll({ type: 'window' })).filter(client => isAppSignInUrl(client.url));
    if (pages.length === 0) return [];
    const fresh = await caches.open(APP_SIGN_IN_FRESH_CACHE);
    const stale = [];
    for (const page of pages) {
        if (!(await fresh.match(appSignInFreshKey(page.id)))) stale.push(page);
    }
    return stale;
}

// Call AFTER clients.claim(): navigate() only works on pages this worker controls.
function reloadAppSignInPages(pages) {
    return Promise.all(pages.map(page => page.navigate(page.url).catch(() => { })));
}
