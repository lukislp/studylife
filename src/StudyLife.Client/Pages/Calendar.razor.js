// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

// Collocated JS isolation module for Calendar.razor (index.html stays untouched). It only reports
// DOM facts; which key or gesture means which navigation is decided in C#
// (Services/CalendarNavigation.cs). Touch swiping lives in interop.js (initCalendarSwipe).

const EDITABLE = 'input, textarea, select, [contenteditable=""], [contenteditable="true"]';
const DIALOG = '[role="dialog"], [aria-modal="true"], .modal-overlay';

function keyIsBlocked() {
    const active = document.activeElement;
    if (active && active.closest && active.closest(EDITABLE)) return true;
    return !!document.querySelector(DIALOG);
}

// The page's declared interest in the bare Left/Right arrow keys, as an attribute on <html>
// (data-page-arrow-keys="horizontal") while the calendar is shown and no text field or dialog
// has them. The browser ignores it. The native app reads it: on the iPad, UIKit keeps the
// hardware arrow keys for itself (system keyboard navigation) before they reach the WebView's
// page, while letters such as T arrive; the app therefore claims Left/Right only while a page
// declares this and hands them to the page as keydown events (studylife-app, HardwareArrowKeys).
const ARROW_INTENT = 'pageArrowKeys';

function declareArrowIntent() {
    if (keyIsBlocked()) delete document.documentElement.dataset[ARROW_INTENT];
    else document.documentElement.dataset[ARROW_INTENT] = 'horizontal';
}

function withdrawArrowIntent() {
    delete document.documentElement.dataset[ARROW_INTENT];
}

// A page is only ever mounted once, but the module instance is shared between visits, so
// the listeners are tracked here and removed on dispose.
let cleanup = null;

export function attach(dotnetRef, scrollerId, wheelThreshold) {
    detach();

    const onKeyDown = (e) => {
        if (e.defaultPrevented || e.repeat) return;
        if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight' && e.key !== 't' && e.key !== 'T') return;
        dotnetRef.invokeMethodAsync('OnShortcutKey', e.key, e.ctrlKey, e.metaKey, e.altKey, e.shiftKey, keyIsBlocked())
            .then((handled) => { if (handled) e.preventDefault(); })
            .catch(() => { });
    };

    // Two-finger horizontal trackpad scroll. One gesture = one step: the delta is accumulated, a
    // step fires once the threshold is crossed, and further wheel events (inertia included) are
    // swallowed until the stream has been quiet for a moment. Never preventDefault: vertical
    // scrolling and horizontal panning inside the grid stay native.
    const scroller = document.getElementById(scrollerId);
    let accX = 0, accY = 0, locked = false, quietTimer = null;
    const QUIET_MS = 220;
    const onWheel = (e) => {
        if (e.ctrlKey) return; // pinch-zoom is reported as ctrl+wheel
        clearTimeout(quietTimer);
        quietTimer = setTimeout(() => { accX = 0; accY = 0; locked = false; }, QUIET_MS);
        if (locked) return;
        // Panning inside a horizontally scrollable grid keeps priority over paging until the
        // edge is reached (same rule as the touch swipe).
        if (scroller && scroller.scrollWidth > scroller.clientWidth + 1) {
            const max = scroller.scrollWidth - scroller.clientWidth;
            const towardsLater = e.deltaX > 0;
            const canPan = towardsLater ? scroller.scrollLeft < max - 8 : scroller.scrollLeft > 8;
            if (canPan) { accX = 0; accY = 0; return; }
        }
        accX += e.deltaX;
        accY += e.deltaY;
        if (Math.abs(accX) < wheelThreshold) return;
        locked = true;
        const dx = accX, dy = accY;
        accX = 0; accY = 0;
        dotnetRef.invokeMethodAsync('OnHorizontalGesture', dx, dy).catch(() => { });
    };

    // Keep the declared arrow-key interest in step with focus and dialogs: re-evaluated once per
    // frame after a focus change or a DOM change (a dialog opening or closing), never per key.
    let intentFrame = 0;
    const scheduleIntent = () => {
        if (intentFrame) return;
        intentFrame = requestAnimationFrame(() => { intentFrame = 0; declareArrowIntent(); });
    };
    const dialogWatch = new MutationObserver(scheduleIntent);
    dialogWatch.observe(document.body, { childList: true, subtree: true });

    document.addEventListener('keydown', onKeyDown);
    document.addEventListener('focusin', scheduleIntent);
    document.addEventListener('focusout', scheduleIntent);
    const target = scroller ?? document;
    target.addEventListener('wheel', onWheel, { passive: true });
    declareArrowIntent();
    cleanup = () => {
        document.removeEventListener('keydown', onKeyDown);
        document.removeEventListener('focusin', scheduleIntent);
        document.removeEventListener('focusout', scheduleIntent);
        target.removeEventListener('wheel', onWheel);
        dialogWatch.disconnect();
        cancelAnimationFrame(intentFrame);
        clearTimeout(quietTimer);
        withdrawArrowIntent();
    };
}

export function detach() {
    if (cleanup) { cleanup(); cleanup = null; }
}
