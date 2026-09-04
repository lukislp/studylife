// Manual Blazor start (index.html sets autostart="false" on the blazor.webassembly.js tag) -
// the ONLY reason for this file: loadBootResource is the sole hook into the WASM download/
// runtime-init phase, needed for the boot timeline telemetry (docs/ARCHITECTURE.md "Telemetry",
// phase 2, read once via js/interop.js's studylifeGetBootMarks). Must run AFTER
// blazor.webassembly.js (which only DEFINES window.Blazor.start when autostart is off) and
// BEFORE interop.js needs any Blazor/.NET interop - script order in index.html keeps that.
//
// Second job (2026-09): fetch every framework asset with retries. The web runs as several pods
// behind one gateway and rolls out one pod at a time (maxSurge 1), so for a few minutes old and
// new versions answer side by side. A page that already got the NEW index.html/boot manifest
// then asks for fingerprinted files an OLD pod does not have - it answers 404 with an empty
// body, the integrity check fails (SHA-256 of "" is 47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=)
// and the app is dead until the next manual reload. Retrying with backoff rides out that window:
// each retry is routed again and, as the rollout progresses, lands on a new pod.
(function () {
    var wasmDownloadMarked = false;
    var RETRY_DELAYS_MS = [1000, 2000, 4000, 8000, 15000, 30000, 30000, 30000]; // ~2 min total

    function fetchWithRetry(uri, integrity) {
        var attempt = 0;
        function once() {
            var init = { cache: 'no-cache', credentials: 'same-origin' };
            if (integrity) init.integrity = integrity;
            return fetch(uri, init).then(function (response) {
                // fetch() only rejects on network-level failures; a 404 from an old pod is a
                // resolved response with an empty body, which would then fail integrity.
                if (!response.ok) throw new Error('boot resource ' + uri + ' -> HTTP ' + response.status);
                return response;
            }).catch(function (err) {
                if (attempt >= RETRY_DELAYS_MS.length) throw err;
                var delay = RETRY_DELAYS_MS[attempt++];
                return new Promise(function (resolve) { setTimeout(resolve, delay); }).then(once);
            });
        }
        return once();
    }

    Blazor.start({
        loadBootResource: function (type, name, defaultUri, integrity) {
            // dotnetjs is the first resource requested once boot resource resolution starts -
            // a reasonable proxy for "wasm download begins" without needing to track every
            // individual framework asset.
            if (type === 'dotnetjs' && !wasmDownloadMarked) {
                wasmDownloadMarked = true;
                try { performance.mark('sl-wasm-download-start'); } catch (e) { /* Performance API unsupported */ }
            }
            // The JS runtime module has to be loaded by the browser's module loader from a URI
            // (Blazor only accepts a string or null for this type); everything else may be
            // handed over as a Response, which is what makes the retry possible.
            if (type === 'dotnetjs') return defaultUri;
            return fetchWithRetry(defaultUri, integrity);
        }
    }).then(function () {
        try { performance.mark('sl-runtime-ready'); } catch (e) { /* Performance API unsupported */ }
    });
})();
