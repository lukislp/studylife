// Curated accent color (setup page) - its own JS module instead of global in index.html, because
// index.html is intentionally left untouched. Dynamic
// import() from AppStateService (IJSObjectReference) instead of a <script> tag, so it works
// without registration in index.html. Mirrors exactly the existing applyTheme mechanism
// (data attribute on <html>, see base.css).
export function applyAccent(accentColor) {
    // "coral" is the original default hue, already baked into --accent without the attribute.
    if (accentColor && accentColor !== 'coral') {
        document.documentElement.setAttribute('data-accent', accentColor);
    } else {
        document.documentElement.removeAttribute('data-accent');
    }
}
