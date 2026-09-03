// Telemetry boot timeline (docs/ARCHITECTURE.md "Telemetry", phase 2): this script is the
// earliest point any of our own JS runs, right after the initial HTML parse - a reasonable proxy
// for "html ready" until boot-start.js's manual Blazor.start() picks up the wasm/runtime phases
// and MainLayout marks the first real render. Read once via js/interop.js's
// studylifeGetBootMarks (TelemetryService.RecordBootFromMarksAsync).
try { performance.mark('sl-html-ready'); } catch (e) { /* Performance API unsupported */ }

// Public demo instances (DEMO_MODE=true): the WASM boot itself takes several seconds
// before Login.razor's own demo-mode check ever runs - without an explanation, that
// silent wait reads as the app being stuck, right before it auto-signs the visitor in.
// Fired in parallel with the WASM download below, not blocking it. Same endpoint
// Login.razor queries later for the real auto-login - this is purely a loading-screen
// label swap, nothing about the actual demo/auth flow. Any failure (older server without
// the endpoint, network hiccup, normal non-demo instance) leaves the default text as-is.
fetch('api/auth/demo').then(function (r) { return r.json(); }).then(function (d) {
    if (d && d.demo) {
        var el = document.getElementById('boot-loading-text');
        if (el) el.textContent = 'Loading demo — you’ll be signed in automatically';
    }
}).catch(function () { /* normal instance / offline - default text stays */ });

// Page stylesheets, loaded without blocking the first paint (see index.html). A stylesheet
// link appended from script is not render-blocking; by the time Blazor has downloaded and
// started the runtime (seconds), these few kilobytes have long arrived, so the first rendered
// page is fully styled. Order preserved as in the former <link> list so cascade precedence is
// unchanged.
(function () {
    var sheets = ['dashboard', 'stats', 'calendar', 'focus', 'setup', 'notes', 'planner', 'progressshare'];
    var head = document.head;
    sheets.forEach(function (name) {
        var link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = 'css/' + name + '.css';
        head.appendChild(link);
    });
})();

try { performance.mark('sl-boot-script-done'); } catch (e) { /* Performance API unsupported */ }
