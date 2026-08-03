using System.Net;

namespace StudyLife.Client.Services;

/// <summary>
/// Sibling of ApiKeyHandler: attaches the passkey session token (if present after login)
/// as an X-Session-Token header to every request to the app's own server. Server-side, the
/// session token always wins over the API key fallback during user resolution - so a logged-in
/// client is guaranteed to see the data of ITS OWN account, even though the ApiKeyHandler
/// underneath still sends the shared key along.
///
/// 401 rule ("no pointless logout"): network errors and timeouts throw an exception here
/// and leave the token untouched - only an actual 401 RESPONSE from the app's own server on
/// one of its own (non-auth) API paths triggers NotifySessionInvalidated. This applies EVEN if
/// no token was attached at all (no login state present, e.g. fresh browser/cleared
/// storage) - precisely in that case the app really does need to redirect to the login page,
/// otherwise it would get stuck on the empty/broken page with no error handling at all.
/// /api/auth paths are excluded: there, 401 means "login/action failed" (e.g. wrong
/// signature), not "your session is dead" - otherwise a failed login attempt would wrongly
/// trigger the same redirect.
/// </summary>
public sealed class SessionHandler : DelegatingHandler
{
    private readonly SessionTokenStore _tokenStore;
    private readonly Uri _baseAddress;

    public SessionHandler(SessionTokenStore tokenStore, Uri baseAddress)
    {
        _tokenStore = tokenStore;
        _baseAddress = baseAddress;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isOwnApi = request.RequestUri is { } uri && _baseAddress.IsBaseOf(uri);
        var isAuthPath = isOwnApi && request.RequestUri!.AbsolutePath.Contains("/api/auth/", StringComparison.OrdinalIgnoreCase);

        if (isOwnApi && _tokenStore.Token is { Length: > 0 } token)
            request.Headers.TryAddWithoutValidation("X-Session-Token", token);

        var response = await base.SendAsync(request, cancellationToken);

        if (isOwnApi && !isAuthPath && response.StatusCode == HttpStatusCode.Unauthorized)
            _tokenStore.NotifySessionInvalidated();

        return response;
    }
}
