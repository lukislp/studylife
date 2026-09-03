// Manual Blazor start (index.html sets autostart="false" on the blazor.webassembly.js tag) -
// the ONLY reason for this file: loadBootResource is the sole hook into the WASM download/
// runtime-init phase, needed for the boot timeline telemetry (docs/ARCHITECTURE.md "Telemetry",
// phase 2, read once via js/interop.js's studylifeGetBootMarks). Must run AFTER
// blazor.webassembly.js (which only DEFINES window.Blazor.start when autostart is off) and
// BEFORE interop.js needs any Blazor/.NET interop - script order in index.html keeps that.
(function () {
    var wasmDownloadMarked = false;
    Blazor.start({
        loadBootResource: function (type, name, defaultUri, integrity) {
            // dotnetjs is the first resource requested once boot resource resolution starts -
            // a reasonable proxy for "wasm download begins" without needing to track every
            // individual framework asset.
            if (type === 'dotnetjs' && !wasmDownloadMarked) {
                wasmDownloadMarked = true;
                try { performance.mark('sl-wasm-download-start'); } catch (e) { /* Performance API unsupported */ }
            }
            return defaultUri; // no override - just observing, not changing what's fetched
        }
    }).then(function () {
        try { performance.mark('sl-runtime-ready'); } catch (e) { /* Performance API unsupported */ }
    });
})();
