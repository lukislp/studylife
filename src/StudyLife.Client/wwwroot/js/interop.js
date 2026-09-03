// Diagnostic banner for unhandled JS/.NET errors: Blazor's own blazor-error-ui shows
// only the generic text "An unhandled error has occurred." without any detail -
// in the native app's WKWebView there is additionally no access to the JS console
// from the outside (no Safari Web Inspector connection possible over SSH). Registered as
// early as possible (before any other script), so that even very early boot errors are
// captured too. Same idea as the existing Live Activity DOM banner (showLiveActivityDiag).
(function () {
    function showDiag(text) {
        var el = document.getElementById('js-error-diag');
        if (!el) {
            el = document.createElement('div');
            el.id = 'js-error-diag';
            el.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:99999;' +
                'background:#7a1f1f;color:#fff;font:12px monospace;padding:8px;' +
                'max-height:40vh;overflow:auto;white-space:pre-wrap;word-break:break-all;';
            var dismiss = document.createElement('div');
            dismiss.textContent = '✕ schließen';
            dismiss.style.cssText = 'text-align:right;cursor:pointer;font-weight:bold;';
            dismiss.onclick = function () { el.remove(); };
            el.appendChild(dismiss);
            var content = document.createElement('div');
            content.id = 'js-error-diag-content';
            el.appendChild(content);
            document.body.appendChild(el);
        }
        var content = document.getElementById('js-error-diag-content');
        content.textContent += (content.textContent ? '\n---\n' : '') + text;
    }
    window.addEventListener('error', function (e) {
        showDiag('error: ' + (e.message || e) + '\n' + (e.filename || '') + ':' + (e.lineno || '') +
            (e.error && e.error.stack ? '\n' + e.error.stack : ''));
    });
    window.addEventListener('unhandledrejection', function (e) {
        var reason = e.reason;
        showDiag('unhandledrejection: ' + (reason && reason.message ? reason.message : reason) +
            (reason && reason.stack ? '\n' + reason.stack : ''));
    });
})();

function scrollElementToTop(id) {
    var el = document.getElementById(id);
    if (el) el.scrollTop = 0;
}

function scrollElementToBottom(id) {
    var el = document.getElementById(id);
    if (el) el.scrollTop = el.scrollHeight;
}

function scrollElementToRight(id) {
    var el = document.getElementById(id);
    if (el) el.scrollLeft = el.scrollWidth;
}

function initCalendarSwipe(elementId, dotnetRef) {
    // Week-swipe gesture on the calendar grid. Passive listeners only (never
    // preventDefault), so native horizontal/vertical scrolling stays untouched.
    // A week change only fires when the horizontal scroll container was already
    // at the corresponding edge when the gesture STARTED (carousel pattern:
    // panning within scrollable content scrolls; a swipe at the edge pages).
    var el = document.getElementById(elementId);
    if (!el) return;
    if (el._calendarSwipeCleanup) el._calendarSwipeCleanup();
    var startX = null, startY = 0, wasAtLeft = false, wasAtRight = false;
    var EDGE = 8, MIN_DIST = 60, AXIS_RATIO = 1.5;
    var onStart = function (e) {
        if (e.touches.length !== 1) { startX = null; return; } // multi-touch (pinch) => not a swipe
        startX = e.touches[0].clientX;
        startY = e.touches[0].clientY;
        var max = el.scrollWidth - el.clientWidth;
        wasAtLeft = el.scrollLeft <= EDGE;
        wasAtRight = el.scrollLeft >= max - EDGE;
    };
    var onEnd = function (e) {
        if (startX === null || e.changedTouches.length === 0) return;
        var dx = e.changedTouches[0].clientX - startX;
        var dy = e.changedTouches[0].clientY - startY;
        startX = null;
        // Minimum distance + dominant-axis check: vertical scrolling through the
        // day (or a simple tap on a session) must never trigger a week change.
        if (Math.abs(dx) < MIN_DIST || Math.abs(dx) <= Math.abs(dy) * AXIS_RATIO) return;
        if (dx < 0 && wasAtRight) {
            el.scrollLeft = 0; // next week: snap back so Monday is visible
            dotnetRef.invokeMethodAsync('SwipeNextWeek').catch(function () { });
        } else if (dx > 0 && wasAtLeft) {
            el.scrollLeft = el.scrollWidth - el.clientWidth; // prev week: land on Sunday (continuous timeline)
            dotnetRef.invokeMethodAsync('SwipePrevWeek').catch(function () { });
        }
    };
    el.addEventListener('touchstart', onStart, { passive: true });
    el.addEventListener('touchend', onEnd, { passive: true });
    el._calendarSwipeCleanup = function () {
        el.removeEventListener('touchstart', onStart);
        el.removeEventListener('touchend', onEnd);
        delete el._calendarSwipeCleanup;
    };
}

function disposeCalendarSwipe(elementId) {
    var el = document.getElementById(elementId);
    if (el && el._calendarSwipeCleanup) el._calendarSwipeCleanup();
}

function printPage() {
    // window.print() is undefined or throws in some standalone/installed-PWA webviews
    // (notably on some Android/iOS home-screen setups) - never let that surface as an
    // unhandled JS interop exception in Blazor, just no-op if printing isn't available.
    try {
        if (typeof window.print === 'function') {
            window.print();
            return true;
        }
    } catch (e) { /* printing unsupported in this webview, ignore */ }
    return false;
}

function setPageTitle(title) {
    document.title = title;
}

function isPageHidden() {
    return document.hidden;
}

// Lets an installed browser extension (studylife-focusguard, studylife-focustunes) react to a
// timer start/pause/reset the instant it happens, instead of waiting for its own periodic poll -
// those extensions register a content script (scoped to exactly the one origin the user connected
// with) that listens for this event and, on hearing it, just re-polls its own authenticated
// GET /api/timerstate rather than trusting this payload directly; the actual DTO here only exists
// to save that immediate re-poll from being a total guess about what changed.
function dispatchTimerStateChanged(state) {
    window.dispatchEvent(new CustomEvent('studylife:timerstate-changed', { detail: state }));
}

// ── Cross-tab write-queue lock (Web Locks API) ───────────────────────────
// Every tab is its own Blazor WASM runtime with its own in-memory copy of
// AppStateService's offline write queue, all persisting to the same
// localStorage key - navigator.locks lets tabs coordinate a named critical
// section instead of blindly clobbering each other. .NET's work happens in
// a separate invokeMethodAsync round trip from the lock's own JS callback,
// so the lock is kept held via a deferred promise: the callback returns a
// promise that only resolves once studylifeLockRelease(handle) is called
// with the handle acquire handed back. navigator.locks is undefined in some
// exotic WebViews - callers must treat a 0 handle as "no lock available"
// and fall back to running unguarded (today's single-tab-only behavior).
let _lockHandleSeq = 0;
const _lockResolvers = {};

// Blocking acquire: waits up to timeoutMs. Resolves to a handle (>= 1) once
// acquired - pass it to studylifeLockRelease when done - or 0 if
// navigator.locks doesn't exist or the wait timed out (both cases: proceed
// unguarded rather than lose or block the write forever).
function studylifeLockAcquire(name, timeoutMs) {
    if (!('locks' in navigator)) return Promise.resolve(0);
    return new Promise(function (resolveAcquire) {
        navigator.locks.request(name, { signal: AbortSignal.timeout(timeoutMs) }, function () {
            return new Promise(function (resolveHold) {
                var handle = ++_lockHandleSeq;
                _lockResolvers[handle] = resolveHold;
                resolveAcquire(handle);
            });
        }).catch(function () { resolveAcquire(0); });
    });
}

// Non-blocking try-acquire (one replay owner across tabs at a time).
// Resolves to a handle if acquired, null if another tab already holds the
// lock right now (caller should skip this cycle), or 0 if navigator.locks
// doesn't exist (proceed unguarded, same as studylifeLockAcquire).
function studylifeLockTryAcquire(name) {
    if (!('locks' in navigator)) return Promise.resolve(0);
    return new Promise(function (resolveAcquire) {
        navigator.locks.request(name, { ifAvailable: true }, function (lock) {
            if (!lock) { resolveAcquire(null); return; }
            return new Promise(function (resolveHold) {
                var handle = ++_lockHandleSeq;
                _lockResolvers[handle] = resolveHold;
                resolveAcquire(handle);
            });
        }).catch(function () { resolveAcquire(null); });
    });
}

function studylifeLockRelease(handle) {
    var resolve = _lockResolvers[handle];
    if (resolve) {
        delete _lockResolvers[handle];
        resolve();
    }
}

function playCompletionSound() {
    try {
        const ctx = new (window.AudioContext || window.webkitAudioContext)();
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.connect(gain);
        gain.connect(ctx.destination);
        osc.type = 'sine';
        osc.frequency.setValueAtTime(880, ctx.currentTime);
        osc.frequency.exponentialRampToValueAtTime(1318.5, ctx.currentTime + 0.15);
        gain.gain.setValueAtTime(0.001, ctx.currentTime);
        gain.gain.exponentialRampToValueAtTime(0.2, ctx.currentTime + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.5);
        osc.start(ctx.currentTime);
        osc.stop(ctx.currentTime + 0.55);
    } catch (e) { /* audio blocked or unsupported, ignore */ }

    try {
        if (navigator.vibrate) navigator.vibrate(200);
    } catch (e) { /* vibration unsupported, ignore */ }
}

// Screen Wake Lock: keeps the display on while a focus timer is running.
// The browser auto-releases the lock when the tab is hidden, so we track
// intent in wakeLockWanted and re-acquire on visibilitychange.
let wakeLockSentinel = null;
let wakeLockWanted = false;

async function requestWakeLock() {
    wakeLockWanted = true;
    if (!('wakeLock' in navigator)) return false;
    try {
        wakeLockSentinel = await navigator.wakeLock.request('screen');
        return true;
    } catch (e) {
        // Can reject e.g. on low battery or when the document is hidden.
        return false;
    }
}

async function releaseWakeLock() {
    wakeLockWanted = false;
    try {
        if (wakeLockSentinel) await wakeLockSentinel.release();
    } catch (e) { /* already released, ignore */ }
    wakeLockSentinel = null;
}

document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && wakeLockWanted) {
        requestWakeLock();
    }
});

function applyTheme(theme) {
    // theme: "dark" | "light" | "system". "system" removes the override so the
    // prefers-color-scheme media query in base.css takes over again.
    if (theme === 'dark' || theme === 'light') {
        document.documentElement.setAttribute('data-theme', theme);
    } else {
        document.documentElement.removeAttribute('data-theme');
    }
}

function setDocumentLanguage(lang) {
    // Keeps <html lang> in sync with the app's own i18n language - Chromium browsers
    // base the formatting of native form controls (especially <input type="datetime-local">,
    // AM/PM vs. 24h) on this, NOT on navigator.language. Used to be hardcoded to "en",
    // which meant the calendar time picker showed AM/PM even with a German UI.
    document.documentElement.setAttribute('lang', lang);
}

async function requestNotificationPermission() {
    if (!('Notification' in window)) return 'unsupported';
    if (Notification.permission === 'granted') return 'granted';
    if (Notification.permission === 'denied') return 'denied';
    const result = await Notification.requestPermission();
    return result;
}

function getNotificationPermissionStatus() {
    // On iOS the Notification API only runs in installed PWA mode (standalone).
    // In a normal browser tab, 'Notification' is not defined.
    const isIos = /iphone|ipad|ipod/i.test(navigator.userAgent);
    const isStandalone = window.navigator.standalone === true
        || window.matchMedia('(display-mode: standalone)').matches;

    if (isIos && !isStandalone) return 'ios-browser'; // special case: tab, not PWA
    if (!('Notification' in window)) return 'unsupported';
    return Notification.permission; // 'default' | 'granted' | 'denied'
}

async function sendTestNotification() {
    if (!('serviceWorker' in navigator)) return;
    const reg = await navigator.serviceWorker.ready;
    reg.showNotification('StudyLife ✦', {
        body: 'Benachrichtigungen funktionieren!',
        icon: '/icons/icon-192.png',
        badge: '/icons/icon-192.png',
        tag: 'studylife-test',
        renotify: true
    });
}

function showLocalNotification(title, body) {
    if (!('serviceWorker' in navigator)) return;
    navigator.serviceWorker.ready.then(reg => {
        reg.showNotification(title, {
            body: body,
            icon: '/icons/icon-192.png',
            badge: '/icons/icon-192.png',
            tag: 'studylife-timer',
            renotify: true
        });
    });
}

function showSessionNotification(title, body) {
    if (!('Notification' in window) || Notification.permission !== 'granted') return;
    const n = new Notification(title, {
        body: body,
        icon: '/icons/icon-512.png',
        badge: '/icons/icon-512.png',
        tag: 'studylife-session',
        renotify: true
    });
    n.onclick = function () { window.focus(); n.close(); };
}

function setAppBadge(count) {
    // Badging API: shows a number on the installed PWA's app icon.
    // No-op where unsupported (most desktop browser tabs).
    try {
        if (!('setAppBadge' in navigator)) return;
        if (count > 0) {
            navigator.setAppBadge(count).catch(() => { /* ignore */ });
        } else {
            navigator.clearAppBadge().catch(() => { /* ignore */ });
        }
    } catch (e) { /* badging unsupported, ignore */ }
}

function urlBase64ToUint8Array(base64String) {
    const padding = '='.repeat((4 - base64String.length % 4) % 4);
    const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
    const rawData = window.atob(base64);
    return Uint8Array.from([...rawData].map(c => c.charCodeAt(0)));
}

async function subscribePush(vapidPublicKey) {
    if (!('serviceWorker' in navigator) || !('PushManager' in window)) return null;
    try {
        const reg = await navigator.serviceWorker.ready;
        let sub = await reg.pushManager.getSubscription();
        if (!sub) {
            sub = await reg.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: urlBase64ToUint8Array(vapidPublicKey)
            });
        }
        const json = sub.toJSON();
        return JSON.stringify({
            endpoint: sub.endpoint,
            p256dh: json.keys.p256dh,
            auth: json.keys.auth
        });
    } catch (e) {
        console.warn('Push subscribe failed:', e);
        return null;
    }
}

async function unsubscribePush() {
    if (!('serviceWorker' in navigator)) return;
    const reg = await navigator.serviceWorker.ready;
    const sub = await reg.pushManager.getSubscription();
    if (sub) await sub.unsubscribe();
}

// Service worker for PWA
if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('service-worker.js');
}

function initGlobalSearchHotkey(dotnetRef) {
    // Ctrl+K / Cmd+K opens the global search overlay (wired from MainLayout).
    // Single document-level listener; a re-init (e.g. Blazor reconnect) replaces
    // the previous one instead of stacking handlers.
    if (window._globalSearchHotkeyCleanup) window._globalSearchHotkeyCleanup();
    var handler = function (e) {
        if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('ToggleGlobalSearch').catch(function () { });
        }
    };
    document.addEventListener('keydown', handler);
    window._globalSearchHotkeyCleanup = function () {
        document.removeEventListener('keydown', handler);
        delete window._globalSearchHotkeyCleanup;
    };
}

function isNarrowViewport() { return window.matchMedia('(max-width: 768px)').matches; }

function scrollElementToCurrentTime(scrollContainerId) {
    // Scrolls the calendar scroll container (#cal-outer, survives week/day
    // switching unchanged - see initCalendarSwipe) so that the "now" line
    // (.cal-now-line) is directly visible instead of midnight. .cal-now-line only exists
    // if today falls within the currently visible range (see CalendarDayColumn.razor);
    // if it's not in the DOM, this is a no-op (not an error).
    var container = document.getElementById(scrollContainerId);
    if (!container) return;
    var nowLine = container.querySelector('.cal-now-line');
    if (!nowLine) return;

    // getBoundingClientRect() instead of hardcoded hour pixel values: returns the
    // actual rendered position independent of row height, sticky header height,
    // etc., so it stays correct even with future CSS adjustments to .cal-hours/.hour-label.
    // Both rects already reflect the current scroll state, so the difference plus
    // scrollTop gives the position within the scrollable content.
    var containerRect = container.getBoundingClientRect();
    var lineRect = nowLine.getBoundingClientRect();
    var lineOffsetInContent = (lineRect.top - containerRect.top) + container.scrollTop;

    // Goal: position the line at ~30% of the visible height, not at the very top -
    // this keeps the context shortly before "now" visible instead of pinning the
    // line to the edge.
    var target = lineOffsetInContent - container.clientHeight * 0.3;
    var maxScroll = container.scrollHeight - container.clientHeight;
    container.scrollTop = Math.max(0, Math.min(target, maxScroll));
}

// "Read note aloud" fallback for languages without a server-side Piper voice: the
// browser's own built-in synthesis, so the feature still works everywhere, just with
// whatever voice quality the OS/browser ships - no server round-trip, no audio bytes.
function speakText(text, lang) {
    if (!window.speechSynthesis) return false;
    window.speechSynthesis.cancel(); // don't stack utterances if clicked again mid-playback
    var utterance = new SpeechSynthesisUtterance(text);
    utterance.lang = lang;
    window.speechSynthesis.speak(utterance);
    return true;
}

// Voice dictation ("speak a note instead of typing it") - records via MediaRecorder in
// whatever format the browser natively supports (webm/opus, ogg/opus, ...), then
// resamples/encodes to 16 kHz mono PCM WAV via the Web Audio API before upload, since
// that's what DictationController/StudyLife.Stt (Whisper.net) expects - the same "do the
// format conversion in the browser instead of adding a server-side transcoding
// dependency" choice already made for ExtractPlainTextForSpeech (Notes.razor).
let _dictationRecorder = null;
let _dictationChunks = [];

async function startDictationRecording() {
    if (!navigator.mediaDevices || !window.MediaRecorder) return false;
    try {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        _dictationChunks = [];
        _dictationRecorder = new MediaRecorder(stream);
        _dictationRecorder.ondataavailable = function (e) {
            if (e.data.size > 0) _dictationChunks.push(e.data);
        };
        _dictationRecorder.start();
        return true;
    } catch {
        return false; // permission denied, no microphone, or an insecure (non-HTTPS) context
    }
}

// Resolves to null (instead of a WAV) when the recording is essentially silent - found
// live: Whisper doesn't reliably flag this itself (WithNoSpeechThreshold/segment
// probabilities measured directly against real silence AND low-level mic hiss - both
// still came back as a confidently "recognized" hallucination, e.g. " * Musik *", so
// Whisper's own confidence score can't be trusted to catch this). Cheaper too: skips the
// upload and a doomed transcription on already CPU-tight Pi hardware entirely.
function stopDictationRecording() {
    return new Promise(function (resolve, reject) {
        if (!_dictationRecorder) { reject('not recording'); return; }
        _dictationRecorder.onstop = async function () {
            _dictationRecorder.stream.getTracks().forEach(function (t) { t.stop(); });
            _dictationRecorder = null;
            try {
                const blob = new Blob(_dictationChunks, { type: 'audio/webm' });
                resolve(await blobToWavBase64(blob));
            } catch (err) {
                reject(String(err));
            }
        };
        _dictationRecorder.stop();
    });
}

// Stops and discards an in-progress recording without encoding/uploading anything -
// used when the user navigates away from the note being dictated into mid-recording.
function cancelDictationRecording() {
    if (!_dictationRecorder) return;
    _dictationRecorder.onstop = null;
    _dictationRecorder.stream.getTracks().forEach(function (t) { t.stop(); });
    _dictationRecorder.stop();
    _dictationRecorder = null;
    _dictationChunks = [];
}

// Dictation fallback for browsers without MediaRecorder (or where getUserMedia fails -
// no mic, denied permission, insecure context): the browser's own SpeechRecognition API,
// mirroring speakText's "browser's own facility as a last resort" choice above. Real
// trade-off worth being explicit about: unlike everything else in this pipeline (Piper,
// Whisper, both self-hosted), most browsers' SpeechRecognition implementation - Chrome's
// included - sends the audio to the vendor's own cloud speech service to recognize it.
// Only reached when local recording isn't possible at all, and only with the same
// "otherwise the feature simply doesn't work here" justification already accepted for
// speakText's browser-voice fallback.
let _activeSpeechRecognition = null;

function startBrowserSpeechRecognition(lang) {
    return new Promise(function (resolve, reject) {
        const SpeechRecognitionApi = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!SpeechRecognitionApi) { reject('not supported'); return; }
        const recognition = new SpeechRecognitionApi();
        recognition.lang = lang;
        recognition.interimResults = false;
        recognition.continuous = true;
        let finalTranscript = '';
        let started = false;
        recognition.onresult = function (event) {
            for (let i = event.resultIndex; i < event.results.length; i++) {
                if (event.results[i].isFinal) finalTranscript += event.results[i][0].transcript + ' ';
            }
        };
        recognition.onstart = function () { started = true; resolve(true); };
        recognition.onerror = function (event) {
            if (!started) resolve(false); // couldn't even start (permission denied, etc.)
        };
        recognition.onend = function () {
            _activeSpeechRecognition = null;
            window._lastSpeechRecognitionResult = finalTranscript.trim();
        };
        _activeSpeechRecognition = recognition;
        recognition.start();
    });
}

// Stops listening and returns whatever was recognized so far - recognition.onend fires
// asynchronously after stop(), so this polls briefly for _lastSpeechRecognitionResult
// instead of assuming it's already there the instant stop() returns.
async function stopBrowserSpeechRecognition() {
    if (!_activeSpeechRecognition) return window._lastSpeechRecognitionResult || '';
    _activeSpeechRecognition.stop();
    for (let i = 0; i < 20 && _activeSpeechRecognition !== null; i++) {
        await new Promise(function (r) { setTimeout(r, 100); });
    }
    const result = window._lastSpeechRecognitionResult || '';
    window._lastSpeechRecognitionResult = undefined;
    return result;
}

async function blobToWavBase64(blob) {
    const arrayBuffer = await blob.arrayBuffer();
    const decodeCtx = new (window.AudioContext || window.webkitAudioContext)();
    const decoded = await decodeCtx.decodeAudioData(arrayBuffer);
    decodeCtx.close();

    // OfflineAudioContext resamples automatically: an AudioBufferSourceNode's buffer is
    // rendered at the context's own sample rate, whatever the buffer's native rate was.
    const targetRate = 16000;
    const offlineCtx = new OfflineAudioContext(1, Math.ceil(decoded.duration * targetRate), targetRate);
    const source = offlineCtx.createBufferSource();
    source.buffer = decoded;
    source.connect(offlineCtx.destination);
    source.start();
    const rendered = await offlineCtx.startRendering();
    const samples = rendered.getChannelData(0);

    // Root-mean-square amplitude as a plain loudness gate - a pragmatic heuristic, not a
    // real voice-activity detector, but real speech at normal mic gain sits well above
    // this even quietly, while true silence/room tone doesn't. 0.01 threshold: measured
    // directly, low-level mic hiss (RMS ~0.006) already fooled Whisper into a confident
    // hallucination, so this only needs to catch what's quieter than that.
    let sumSquares = 0;
    for (let i = 0; i < samples.length; i++) sumSquares += samples[i] * samples[i];
    const rms = Math.sqrt(sumSquares / samples.length);
    if (rms < 0.01) return null;

    const wavBytes = encodeWavPcm16(samples, targetRate);
    let binary = '';
    for (let i = 0; i < wavBytes.length; i++) binary += String.fromCharCode(wavBytes[i]);
    return window.btoa(binary);
}

// Minimal 16-bit PCM mono WAV encoder - just the fixed 44-byte header plus the samples,
// no exotic chunk types needed for what Whisper.net's WaveParser reads back server-side.
function encodeWavPcm16(samples, sampleRate) {
    const buffer = new ArrayBuffer(44 + samples.length * 2);
    const view = new DataView(buffer);
    function writeString(offset, str) {
        for (let i = 0; i < str.length; i++) view.setUint8(offset + i, str.charCodeAt(i));
    }
    writeString(0, 'RIFF');
    view.setUint32(4, 36 + samples.length * 2, true);
    writeString(8, 'WAVE');
    writeString(12, 'fmt ');
    view.setUint32(16, 16, true);       // fmt chunk size
    view.setUint16(20, 1, true);        // PCM
    view.setUint16(22, 1, true);        // mono
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, sampleRate * 2, true); // byte rate (sampleRate * blockAlign)
    view.setUint16(32, 2, true);        // block align (channels * bytesPerSample)
    view.setUint16(34, 16, true);       // bits per sample
    writeString(36, 'data');
    view.setUint32(40, samples.length * 2, true);
    let offset = 44;
    for (let i = 0; i < samples.length; i++, offset += 2) {
        const s = Math.max(-1, Math.min(1, samples[i]));
        view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7fff, true);
    }
    return new Uint8Array(buffer);
}

// ---- Server-sent change stream (AppStateService.StartChangeStreamAsync) ----------------------
// Holds ONE GET api/events open (EventsController) and calls back into .NET with the kind of
// data that changed, so the app refetches right away instead of waiting for its 30s poll. Plain
// fetch + ReadableStream instead of EventSource: EventSource cannot send the X-Session-Token
// header, and the token must never travel in a URL. Reconnects with exponential backoff (2s ->
// 60s) on any failure, gives up after several failures that never produced a connection (a host
// that cannot stream at all - the poll keeps working there), and stops for good on 401/403 (the
// session is gone; the regular 401 handling on the next poll takes over).
let changeStreamAbort = null;

async function startChangeStream(url, token, dotnetRef) {
    stopChangeStream();
    const controller = new AbortController();
    changeStreamAbort = controller;
    let backoffMs = 2000;
    let failuresWithoutConnection = 0;
    while (!controller.signal.aborted) {
        const attemptStart = performance.now();
        try {
            const response = await fetch(url, {
                headers: { 'X-Session-Token': token, 'Accept': 'text/event-stream' },
                cache: 'no-store',
                signal: controller.signal
            });
            if (response.status === 401 || response.status === 403) return;
            if (!response.ok || !response.body) throw new Error('change stream status ' + response.status);
            failuresWithoutConnection = 0;
            backoffMs = 2000;
            // Telemetry (docs/ARCHITECTURE.md "Telemetry"): reuses the same dotnetRef as
            // OnServerChange - AppStateService owns the ref, TelemetryService listens via its
            // OnSseLifecycleEventRaised event (no new cross-service dependency needed).
            dotnetRef.invokeMethodAsync('OnSseLifecycle', 'connected', performance.now() - attemptStart).catch(function () { });
            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';
            while (true) {
                const { value, done } = await reader.read();
                if (done) break;
                buffer += decoder.decode(value, { stream: true });
                let end;
                while ((end = buffer.indexOf('\n\n')) >= 0) {
                    const block = buffer.slice(0, end);
                    buffer = buffer.slice(end + 2);
                    const data = block.match(/^data: (.*)$/m);
                    if (data) dotnetRef.invokeMethodAsync('OnServerChange', data[1]).catch(function () { });
                }
            }
        } catch (err) {
            if (controller.signal.aborted) return;
            if (++failuresWithoutConnection >= 6) {
                dotnetRef.invokeMethodAsync('OnSseLifecycle', 'fallback_poll', performance.now() - attemptStart).catch(function () { });
                return;
            }
        }
        dotnetRef.invokeMethodAsync('OnSseLifecycle', 'reconnect', backoffMs).catch(function () { });
        await new Promise(resolve => setTimeout(resolve, backoffMs));
        backoffMs = Math.min(backoffMs * 2, 60000);
    }
}

function stopChangeStream() {
    if (changeStreamAbort) {
        changeStreamAbort.abort();
        changeStreamAbort = null;
    }
}

// ---- Client telemetry (TelemetryService, phase 2 - docs/ARCHITECTURE.md "Telemetry") ----------
// The 20s/25-event flush itself is a normal awaited POST from .NET. Two things a normal async
// call can't do reliably live here instead: (1) the final flush on pagehide/visibilitychange,
// via sendBeacon + a SYNCHRONOUS DotNet.invokeMethod (an async round trip started that late
// routinely never resolves before the tab is actually gone - see MDN's pagehide guidance);
// (2) Web Vitals, which only finalize once the page is backgrounded (LCP/CLS/INP keep changing
// until then).

var studylifeVitals = { ttfb: null, fcp: null, lcp: null, cls: 0, inp: null };
var studylifeVitalsReported = false;

function studylifeCollectStaticVitals() {
    try {
        var nav = performance.getEntriesByType('navigation')[0];
        if (nav) studylifeVitals.ttfb = nav.responseStart;
    } catch (e) { /* Navigation Timing L2 unsupported */ }
    try {
        var paintEntries = performance.getEntriesByType('paint');
        for (var i = 0; i < paintEntries.length; i++) {
            if (paintEntries[i].name === 'first-contentful-paint') { studylifeVitals.fcp = paintEntries[i].startTime; break; }
        }
    } catch (e) { /* Paint Timing unsupported */ }
}

function studylifeObserveVitals() {
    if (typeof PerformanceObserver === 'undefined') return;
    try {
        new PerformanceObserver(function (list) {
            var entries = list.getEntries();
            var last = entries[entries.length - 1];
            if (last) studylifeVitals.lcp = last.renderTime || last.loadTime || last.startTime;
        }).observe({ type: 'largest-contentful-paint', buffered: true });
    } catch (e) { /* not supported in this browser */ }
    try {
        new PerformanceObserver(function (list) {
            list.getEntries().forEach(function (entry) {
                if (!entry.hadRecentInput) studylifeVitals.cls += entry.value;
            });
        }).observe({ type: 'layout-shift', buffered: true });
    } catch (e) { /* not supported */ }
    try {
        // Simplified proxy for INP (the real spec takes roughly the 98th percentile across the
        // whole session) - the single slowest interaction is a reasonable first-cut signal and
        // needs no session-long percentile bookkeeping.
        new PerformanceObserver(function (list) {
            list.getEntries().forEach(function (entry) {
                if (studylifeVitals.inp === null || entry.duration > studylifeVitals.inp) studylifeVitals.inp = entry.duration;
            });
        }).observe({ type: 'event', durationThreshold: 40, buffered: true });
    } catch (e) { /* not supported */ }
}

function studylifeReportVitalsOnce() {
    if (studylifeVitalsReported) return;
    studylifeVitalsReported = true;
    try {
        DotNet.invokeMethod('StudyLife.Client', 'ReportVitals',
            studylifeVitals.ttfb, studylifeVitals.fcp, studylifeVitals.lcp, studylifeVitals.inp, studylifeVitals.cls);
    } catch (e) { /* best effort */ }
}

function studylifeGetConnectionType() {
    try {
        var c = navigator.connection || navigator.mozConnection || navigator.webkitConnection;
        if (c) {
            if (c.type === 'wifi') return 'wifi';
            if (c.type === 'ethernet') return 'ethernet';
            if (c.type === 'cellular') return 'cellular';
        }
    } catch (e) { /* NetworkInformation unsupported (Firefox/Safari) */ }
    return 'unknown';
}

/// Read once, from MainLayout after first render (TelemetryService.RecordBootFromMarksAsync) -
/// the phase marks themselves come from boot-loading.js (sl-html-ready/sl-boot-script-done),
/// boot-start.js (sl-wasm-download-start/sl-runtime-ready, around the manual Blazor.start() call)
/// and studylifeMarkFirstRender below (sl-first-render, MainLayout.OnAfterRenderAsync).
function studylifeGetBootMarks() {
    function markTime(name) {
        var entries = performance.getEntriesByName(name, 'mark');
        return entries.length ? entries[0].startTime : null;
    }
    var htmlReady = markTime('sl-html-ready');
    var bootScriptDone = markTime('sl-boot-script-done');
    var wasmStart = markTime('sl-wasm-download-start');
    var runtimeReady = markTime('sl-runtime-ready');
    var firstRender = markTime('sl-first-render');

    var downloadBytes = 0;
    var swCacheHit = false;
    try {
        performance.getEntriesByType('resource').forEach(function (entry) {
            if (entry.name.indexOf('_framework/') === -1) return;
            downloadBytes += entry.transferSize || 0;
            // transferSize 0 with a non-zero body means the resource was served from the service
            // worker's cache (or an HTTP 304/disk cache) rather than downloaded over the network.
            if (entry.transferSize === 0 && entry.decodedBodySize > 0) swCacheHit = true;
        });
    } catch (e) { /* Resource Timing unsupported */ }

    var cold = true;
    try { cold = !sessionStorage.getItem('studylife-booted-before'); sessionStorage.setItem('studylife-booted-before', '1'); }
    catch (e) { /* private mode - default to reporting every boot as cold */ }

    return {
        cold: cold,
        htmlMs: htmlReady,
        bootScriptMs: (htmlReady !== null && bootScriptDone !== null) ? (bootScriptDone - htmlReady) : null,
        wasmDownloadMs: (wasmStart !== null && runtimeReady !== null) ? (runtimeReady - wasmStart) : null,
        runtimeReadyMs: runtimeReady,
        firstRenderMs: firstRender,
        downloadBytes: downloadBytes,
        swCacheHit: swCacheHit
    };
}

function studylifeMarkFirstRender() {
    try { performance.mark('sl-first-render'); } catch (e) { /* Performance API unsupported */ }
}

function studylifeTelemetryFlush() {
    studylifeReportVitalsOnce();
    try {
        var json = DotNet.invokeMethod('StudyLife.Client', 'GetPendingTelemetryJson');
        if (!json) return;
        // fetch+keepalive instead of navigator.sendBeacon: a beacon cannot carry the session
        // header, so every unload flush was answered 401 by the SessionOnly policy (seen as a
        // console error on the web on 2026-09-04). keepalive requests survive pagehide like a
        // beacon does (body limit 64 KB - batches are capped at 32 KB by the server anyway).
        var token = null;
        try { token = localStorage.getItem('studylife-session-token'); } catch (e) { /* storage unavailable */ }
        if (!token) return;
        fetch('api/telemetry', {
            method: 'POST',
            keepalive: true,
            headers: { 'Content-Type': 'application/json', 'X-Session-Token': token },
            body: json
        }).catch(function () { /* best effort */ });
    } catch (e) { /* best effort - a lost final flush is not worth surfacing */ }
}

function studylifeSanitizeErrorStack(stack) {
    if (!stack) return '';
    // Keeps only lines that look like a real stack frame (file:line:col) - drops the leading
    // "ErrorType: message" line every JS Error.stack starts with, since the message must never
    // leave the device (contract: "no message text").
    return stack.split('\n').filter(function (l) { return /:\d+:\d+/.test(l); }).join('\n').substring(0, 4000);
}

function studylifeTelemetryInit() {
    studylifeCollectStaticVitals();
    studylifeObserveVitals();
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'hidden') studylifeTelemetryFlush();
    });
    window.addEventListener('pagehide', studylifeTelemetryFlush);

    // Separate from the diagnostics banner listeners at the top of this file (different purpose:
    // this reports a sanitized type+stack to the server for the ClientError log/errors counter,
    // that one shows the raw message locally for on-device debugging) - both run independently
    // on the same browser events.
    window.addEventListener('error', function (e) {
        var stack = studylifeSanitizeErrorStack(e.error && e.error.stack);
        DotNet.invokeMethodAsync('StudyLife.Client', 'ReportJsError',
            (e.error && e.error.name) || 'Error', stack, true, location.pathname).catch(function () { });
    });
    window.addEventListener('unhandledrejection', function (e) {
        var reason = e.reason;
        var stack = studylifeSanitizeErrorStack(reason && reason.stack);
        DotNet.invokeMethodAsync('StudyLife.Client', 'ReportJsError',
            (reason && reason.name) || 'UnhandledRejection', stack, false, location.pathname).catch(function () { });
    });
}
