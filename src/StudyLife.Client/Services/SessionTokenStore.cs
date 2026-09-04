using Microsoft.JSInterop;

namespace StudyLife.Client.Services;

/// <summary>
/// Holds the passkey session token (phase 2) in memory and persists it to localStorage -
/// same persistence pattern as the offline write queue in AppStateService. The token is
/// loaded ONCE at app start (Program.cs, before RunAsync), after that the SessionHandler
/// only reads the synchronous Token property - a DelegatingHandler must not do a
/// JS interop roundtrip on every request.
///
/// "No pointless logout": the token is discarded EXCLUSIVELY via NotifySessionInvalidated,
/// and the SessionHandler only calls that on an explicit 401 response from the app's own
/// server - never on network errors, timeouts, or other status codes.
/// </summary>
public sealed class SessionTokenStore
{
    private const string StorageKey = "studylife-session-token";

    private readonly IJSRuntime _js;

    public SessionTokenStore(IJSRuntime js) => _js = js;

    public string? Token { get; private set; }

    /// <summary>Fires when the server has declared the session invalid via 401 -
    /// MainLayout then redirects to the login page.</summary>
    public event Action? OnSessionInvalidated;

    /// <summary>Raised whenever a session token becomes available: after the stored token was
    /// loaded (InitializeAsync) and after a login (SetTokenAsync). Lets services that need a
    /// live session (AppStateService's change stream) start at the right moment instead of
    /// checking Token once in their constructor - in the native app the store is initialised by
    /// the root component AFTER those services were constructed, so a constructor check never
    /// saw the token there (no change stream in the app until 2026-09).</summary>
    public event Action? OnTokenAvailable;

    /// <summary>
    /// Set by AppStateService's constructor (plain composition, not DI - AppStateService already
    /// depends on SessionTokenStore, so the reverse would be circular) to purge every
    /// account-scoped offline cache (S7: read caches + write queue, all namespaced per account -
    /// see AppStateService) on EITHER logout path below, not just one of them. Both ClearAsync
    /// and NotifySessionInvalidated used to only ever discard the auth token, leaving a shared
    /// browser's next user free to offline-cold-start straight into the previous user's stale
    /// cached data - see the "-v2" cache key comment history in AppStateService for how that bug
    /// was first found.
    /// </summary>
    public Func<Task>? OnLoggedOutAsync { get; set; }

    public async Task InitializeAsync()
    {
        try
        {
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            Token = string.IsNullOrWhiteSpace(stored) ? null : stored;
        }
        catch
        {
            Token = null; // localStorage not available (e.g. private mode) - app keeps running without a session
        }
        if (Token != null) OnTokenAvailable?.Invoke();
    }

    public async Task SetTokenAsync(string token)
    {
        Token = token;
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, token); }
        catch { /* quota/private mode - session then only lives until the next reload, uncritical */ }
        OnTokenAvailable?.Invoke();
    }

    public async Task ClearAsync()
    {
        Token = null;
        try { await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey); }
        catch { /* best effort */ }
        // Awaited (S7): the only caller (PasskeyDeviceManager.LogoutAsync) immediately does a
        // forceLoad navigation right after this returns - the cache/queue/marker purge MUST be
        // written to localStorage before that reload discards this whole WASM instance, or it
        // never happens at all.
        if (OnLoggedOutAsync != null) await OnLoggedOutAsync();
    }

    /// <summary>Called by the SessionHandler on an explicit 401 response: immediately clear the
    /// token from memory (no further request will send it along), clean up localStorage
    /// afterward on a best-effort basis, then notify subscribers (MainLayout → /login).</summary>
    public void NotifySessionInvalidated()
    {
        Token = null;
        _ = ClearStorageBestEffortAsync();
        // Best-effort/fire-and-forget here too (S7), same as the token cleanup above: this is a
        // synchronous void method (called from deep inside the HTTP pipeline, SessionHandler),
        // so there is no caller left to await. MainLayout's forceLoad redirect (OnSessionInvalidated
        // below) can in theory race ahead of this purge finishing - accepted, documented residual
        // risk (see the AppStateService S7 class comment): the per-account cache NAMESPACING is
        // the primary defense against cross-account leakage, this purge is defense-in-depth on
        // top of it, so a lost race here doesn't reopen the original bug.
        if (OnLoggedOutAsync != null) _ = OnLoggedOutAsync();
        OnSessionInvalidated?.Invoke();
    }

    private async Task ClearStorageBestEffortAsync()
    {
        try { await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey); }
        catch { /* best effort */ }
    }
}
