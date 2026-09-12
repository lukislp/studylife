// Collocated module for MainLayout (same pattern as Focus.razor.js/SetupBackupCard.razor.js).
//
// Client update flow (see also CheckForClientUpdateAsync/ReloadForUpdateAsync in MainLayout.razor.cs):
//
// 1. .NET notices that the server runs a newer version than the WASM bundle in the browser.
// 2. prepareUpdate() nudges the service worker registration (reg.update()) and waits until the
//    NEW worker has installed its precache and taken control (skipWaiting + claim in
//    service-worker.published.js fire `controllerchange`). Only then does the toast appear, so
//    a click normally reloads into fully precached assets in well under a second.
// 3. reloadForUpdate() on click: if a new worker already controls the page, reload at once.
//    If one is still installing, wait a bounded time for it. If the registration reports
//    nothing new although the server version differs (an update the browser could not see,
//    an install that failed or takes too long), fall back to what Ctrl+F5 does by hand:
//    unregister the worker, drop its caches, reload - the page comes back uncontrolled,
//    index.html and the framework come straight from the network, and the worker re-registers
//    and precaches in the background. The old flow reloaded under the old worker in that case,
//    which showed the stale bundle plus the same toast again, and only a hard reload helped.
//
// Reports every time the page becomes visible (tab focus, PWA returning from the
// background) to .NET - more reliable than the earlier poll comparison of two
// isPageHidden calls, which could miss the transition (iOS often freezes JS before a
// tick sees hidden=true).
export function registerVisibilityCallback(dotNetRef) {
    const handler = () => {
        if (!document.hidden) {
            dotNetRef.invokeMethodAsync('OnPageBecameVisible').catch(() => { });
        }
    };
    document.addEventListener('visibilitychange', handler);
    return {
        dispose: () => document.removeEventListener('visibilitychange', handler),
    };
}

// Set once a new worker has taken control of this page (whether prepareUpdate() waited for it
// or claim fired on its own): from then on a plain reload lands on the new bundle.
let newWorkerInControl = false;
if ('serviceWorker' in navigator) {
    const controllerAtLoad = navigator.serviceWorker.controller;
    navigator.serviceWorker.addEventListener('controllerchange', () => {
        if (navigator.serviceWorker.controller !== controllerAtLoad) newWorkerInControl = true;
    });
}

async function getRegistration() {
    if (!('serviceWorker' in navigator)) return null;
    try { return await navigator.serviceWorker.getRegistration(); } catch { return null; }
}

// Resolves true as soon as a worker other than the current controller takes over, false after
// maxWaitMs. Registered before whatever may trigger the change, so a fast install cannot slip
// through between "check" and "listen".
function waitForControllerChange(maxWaitMs) {
    const initialController = navigator.serviceWorker.controller;
    return new Promise(resolve => {
        let timer = null;
        const onChange = () => {
            if (navigator.serviceWorker.controller === initialController) return;
            clearTimeout(timer);
            navigator.serviceWorker.removeEventListener('controllerchange', onChange);
            resolve(true);
        };
        navigator.serviceWorker.addEventListener('controllerchange', onChange);
        timer = setTimeout(() => {
            navigator.serviceWorker.removeEventListener('controllerchange', onChange);
            resolve(false);
        }, maxWaitMs);
    });
}

// Asks the browser to re-fetch the worker script and resolves true if that started an
// installation (or one was already under way), false if the registration is up to date as far
// as the browser can tell.
async function triggerUpdate(reg, maxWaitMs) {
    if (reg.installing || reg.waiting) return true;
    const found = new Promise(resolve => {
        const onFound = () => { reg.removeEventListener('updatefound', onFound); resolve(true); };
        reg.addEventListener('updatefound', onFound);
        setTimeout(() => { reg.removeEventListener('updatefound', onFound); resolve(false); }, maxWaitMs);
    });
    try { await reg.update(); } catch { /* offline or blocked - treated as "nothing found" below */ }
    if (reg.installing || reg.waiting) return true;
    return found;
}

// Called by .NET once it knows the server is newer. Returns 'ready' when a new worker controls
// the page (reload is instant and safe), 'pending' when one is still installing after
// maxWaitMs, 'unknown' when the browser sees no new worker at all, 'none' without a service
// worker. The toast is shown in every case; the value only decides how the click behaves.
export async function prepareUpdate(maxWaitMs) {
    const reg = await getRegistration();
    if (!reg) return 'none';
    const controllerChanged = waitForControllerChange(maxWaitMs);
    if (reg.waiting) reg.waiting.postMessage('SKIP_WAITING');
    const installing = await triggerUpdate(reg, 8000);
    if (!installing) return 'unknown';
    return (await controllerChanged) ? 'ready' : 'pending';
}

async function hardReset(reg) {
    try { await reg.unregister(); } catch { /* the reload below still bypasses nothing worse than today */ }
    try {
        const keys = await caches.keys();
        await Promise.all(keys.map(key => caches.delete(key)));
    } catch { /* no Cache API access - the uncontrolled reload alone already fetches fresh */ }
}

export async function reloadForUpdate(maxWaitMs) {
    try {
        const reg = await getRegistration();
        if (reg && newWorkerInControl) {
            // prepareUpdate() (or claim on its own) already switched this page to the new
            // worker - its precache is complete, one reload is all it takes.
            location.reload();
            return;
        }
        if (reg) {
            const controllerChanged = waitForControllerChange(maxWaitMs);
            if (reg.waiting) reg.waiting.postMessage('SKIP_WAITING');
            const installing = await triggerUpdate(reg, 3000);
            if (installing && await controllerChanged) {
                location.reload();
                return;
            }
            // Either nothing new was found although the server is newer, or the install did
            // not finish in time: reloading under the current worker would only show the old
            // bundle again. Do what Ctrl+F5 does instead.
            await hardReset(reg);
        }
    } catch { /* worst case: the plain reload below, which is what the old flow always did */ }
    location.reload();
}
