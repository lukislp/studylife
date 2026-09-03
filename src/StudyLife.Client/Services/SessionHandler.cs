using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;

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
///
/// Telemetry phase 2 (docs/ARCHITECTURE.md "Telemetry"): every own-API round trip is timed here
/// and handed to TelemetryService - the one place that already sees EVERY request/response,
/// instead of instrumenting each call site. TelemetryService is resolved lazily through
/// IServiceProvider (not constructor-injected) on purpose: this handler is built INSIDE the
/// HttpClient factory that TelemetryService itself later uses to send its own batches - an
/// eager constructor dependency the other way would be a DI construction cycle. Resolving it
/// only once actually needed (well after both are fully constructed) avoids that entirely.
/// </summary>
public sealed class SessionHandler : DelegatingHandler
{
    private readonly SessionTokenStore _tokenStore;
    private readonly Uri _baseAddress;
    private readonly IServiceProvider _services;

    public SessionHandler(SessionTokenStore tokenStore, Uri baseAddress, IServiceProvider services)
    {
        _tokenStore = tokenStore;
        _baseAddress = baseAddress;
        _services = services;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isOwnApi = request.RequestUri is { } uri && _baseAddress.IsBaseOf(uri);
        var isAuthPath = isOwnApi && request.RequestUri!.AbsolutePath.Contains("/api/auth/", StringComparison.OrdinalIgnoreCase);

        if (isOwnApi && _tokenStore.Token is { Length: > 0 } token)
            request.Headers.TryAddWithoutValidation("X-Session-Token", token);

        var stopwatch = isOwnApi ? Stopwatch.StartNew() : null;
        var response = await base.SendAsync(request, cancellationToken);

        if (isOwnApi) RecordTelemetry(request, response, stopwatch!.Elapsed.TotalMilliseconds);

        if (isOwnApi && !isAuthPath && response.StatusCode == HttpStatusCode.Unauthorized)
            _tokenStore.NotifySessionInvalidated();

        return response;
    }

    /// <summary>Best-effort, never lets a telemetry mishap affect the real response. Skips its
    /// own POST /api/telemetry (would otherwise report on itself every flush) and /api/auth
    /// (login/session-check traffic isn't a meaningful "API route" data point here).</summary>
    private void RecordTelemetry(HttpRequestMessage request, HttpResponseMessage response, double durationMs)
    {
        try
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (path.StartsWith("api/telemetry", StringComparison.OrdinalIgnoreCase)) return;
            if (path.StartsWith("api/auth/", StringComparison.OrdinalIgnoreCase)) return;

            _services.GetService<TelemetryService>()?.RecordApi(
                NormalizeRoute(path), request.Method.Method, (int)response.StatusCode,
                durationMs, response.StatusCode == HttpStatusCode.NotModified, retries: 0);
        }
        catch { /* telemetry must never affect the real request/response */ }
    }

    /// <summary>Client-side mirror of TelemetryRouteCatalog.Normalize's segment rule (the server
    /// re-validates against its own route table anyway) - any integer/GUID/&gt;40-char segment
    /// becomes "{id}" so the route stays a template, never a concrete URL.</summary>
    private static string NormalizeRoute(string path)
    {
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length > 40 || long.TryParse(segment, out _) || Guid.TryParse(segment, out _))
                segments[i] = "{id}";
        }
        return string.Join('/', segments);
    }
}
