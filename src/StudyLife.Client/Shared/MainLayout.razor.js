// Collocated module for MainLayout (same pattern as Focus.razor.js/SetupBackupCard.razor.js).
//
// reloadForUpdate: one-click update instead of the double-reload dance. An immediate
// location.reload() still runs under the OLD service worker (the new one only installs
// its assets first) - the page comes back stale, the update toast appears again, and only
// the second reload takes effect. Instead, here: kick off the update, wait until the new
// worker has installed AND taken control via controllerchange (skipWaiting+claim in
// service-worker.published.js), THEN reload exactly once. If the new worker already has
// control (claim also fires without a reload), no installation starts - then it reloads
// immediately and the assets already come from the fresh cache.
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

export async function reloadForUpdate(maxWaitMs) {
    try {
        const reg = 'serviceWorker' in navigator
            ? await navigator.serviceWorker.getRegistration()
            : null;
        if (reg) {
            const controllerChanged = new Promise(resolve =>
                navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true }));

            reg.update().catch(() => { });

            // Briefly observe whether an installation starts (or a worker is already waiting).
            const installStarted = await new Promise(resolve => {
                const startedAt = Date.now();
                const poll = setInterval(() => {
                    if (reg.installing || reg.waiting) { clearInterval(poll); resolve(true); }
                    else if (Date.now() - startedAt > 4000) { clearInterval(poll); resolve(false); }
                }, 200);
            });

            if (installStarted) {
                await Promise.race([
                    controllerChanged,
                    new Promise(resolve => setTimeout(resolve, maxWaitMs)),
                ]);
            }
        }
    } catch { /* doesn't matter - worst case the reload behaves as before */ }
    location.reload();
}
