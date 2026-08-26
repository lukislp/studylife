// Account-scoped offline cache purge (audit S7) - its own JS module instead of global in
// index.html, because index.html is intentionally left untouched (same reasoning as accent.js).
// Dynamic import() from AppStateService (IJSObjectReference).
//
// Enumerates every localStorage key ONCE and removes any whose name starts with one of the
// given prefixes. A plain removeItem per known key isn't enough here: GetJsonCachedAsync
// (AppStateService) builds one cache key PER URL a page has ever fetched (dashboard history,
// course goals, notes, session templates, ...) - the exact set of keys that exist at purge time
// is unbounded and unknown to the caller. This is used both on logout (wipe every trace of the
// outgoing account before a different one can log in on the same browser) and when namespace
// resolution detects a DIFFERENT account's leftover marker (see AppStateService.ResolveNamespaceAsync).
export function removeKeysWithPrefixes(prefixes) {
    const toRemove = [];
    for (let i = 0; i < localStorage.length; i++) {
        const key = localStorage.key(i);
        if (key && prefixes.some(p => key.startsWith(p))) {
            toRemove.push(key);
        }
    }
    toRemove.forEach(k => localStorage.removeItem(k));
}
