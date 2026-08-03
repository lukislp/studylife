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
    }

    public async Task SetTokenAsync(string token)
    {
        Token = token;
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, token); }
        catch { /* quota/private mode - session then only lives until the next reload, uncritical */ }
    }

    public async Task ClearAsync()
    {
        Token = null;
        try { await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey); }
        catch { /* best effort */ }
    }

    /// <summary>Called by the SessionHandler on an explicit 401 response: immediately clear the
    /// token from memory (no further request will send it along), clean up localStorage
    /// afterward on a best-effort basis, then notify subscribers (MainLayout → /login).</summary>
    public void NotifySessionInvalidated()
    {
        Token = null;
        _ = ClearStorageBestEffortAsync();
        OnSessionInvalidated?.Invoke();
    }

    private async Task ClearStorageBestEffortAsync()
    {
        try { await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey); }
        catch { /* best effort */ }
    }
}
