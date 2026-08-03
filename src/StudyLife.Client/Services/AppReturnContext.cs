using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

/// <summary>
/// App return path for the auth pages (Login/Register/Link) when they run in the system browser
/// of a native app shell (?app=1): mobile shells (iOS/Android) get the session token
/// back via the studylife:// custom scheme; the Windows shell can't register custom
/// schemes (unpackaged app) and instead passes a loopback URL via ?appret=...
/// (RFC 8252 pattern for native OAuth apps). appret is strictly restricted to
/// http://127.0.0.1|localhost - anything else is ignored so the parameter
/// can't be abused as an open redirect for token theft.
///
/// PKCE-style handoff (?appchallenge=...): a custom URL scheme (iOS/Android) or a loopback
/// listener (Windows) can, in the worst case, be claimed by a different process/app on the
/// same device - if the real session token were put directly in that redirect, a successful
/// interception would be a full account takeover with no further step needed. The app
/// therefore sends a code_challenge (SHA-256 of a verifier it never transmits) up front;
/// BuildTokenReturnRedirectAsync then hands the real token to the server in exchange for a
/// short-lived, single-use, opaque code and puts ONLY that code in the redirect - worthless to
/// whoever receives it without the verifier, which never left the app's own memory (see
/// AuthController's handoff/exchange endpoints). Old apps that don't send appchallenge keep
/// getting the token directly (no behavior change, no protection either) - this only tightens
/// once BOTH sides are updated.
/// </summary>
public sealed class AppReturnContext
{
    private const string CustomSchemeReturn = "studylife://auth";

    private AppReturnContext(bool isActive, string? returnUrl, string? codeChallenge)
    {
        IsActive = isActive;
        _returnUrl = returnUrl;
        _codeChallenge = codeChallenge;
    }

    private readonly string? _returnUrl;
    private readonly string? _codeChallenge;

    public bool IsActive { get; }

    public static AppReturnContext FromUri(Uri uri)
    {
        var isActive = false;
        string? returnUrl = null;
        string? codeChallenge = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            if (pair == "app=1") isActive = true;
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "appret")
                returnUrl = ValidateLoopbackUrl(Uri.UnescapeDataString(kv[1]));
            if (kv.Length == 2 && kv[0] == "appchallenge")
                codeChallenge = Uri.UnescapeDataString(kv[1]);
        }
        return new AppReturnContext(isActive, isActive ? returnUrl : null, isActive ? codeChallenge : null);
    }

    /// <summary>Query suffix that links between the auth pages use to carry the app context
    /// forward ("" in the normal web flow).</summary>
    public string LinkQuery => IsActive
        ? "?app=1"
          + (_returnUrl != null ? "&appret=" + Uri.EscapeDataString(_returnUrl) : "")
          + (_codeChallenge != null ? "&appchallenge=" + Uri.EscapeDataString(_codeChallenge) : "")
        : "";

    /// <summary>Target URL for returning a non-token result to the app, e.g.
    /// BuildReturnRedirect("linked=1"). For a real session token, use
    /// BuildTokenReturnRedirectAsync instead.</summary>
    public string BuildReturnRedirect(string queryParams)
        => $"{_returnUrl ?? CustomSchemeReturn}?{queryParams}";

    /// <summary>Hands the real session token to the server in exchange for a short-lived,
    /// single-use handoff code when the app sent a code_challenge (see class remarks); returns
    /// a "token=..." redirect unchanged if it didn't (old app, no protection available yet).</summary>
    public async Task<string> BuildTokenReturnRedirectAsync(HttpClient http, string token)
    {
        if (_codeChallenge is null)
            return BuildReturnRedirect($"token={Uri.EscapeDataString(token)}");

        var response = await http.PostAsJsonAsync("api/auth/handoff",
            new AuthHandoffRequestDto { Token = token, CodeChallenge = _codeChallenge });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AuthHandoffResponseDto>();
        return BuildReturnRedirect($"code={Uri.EscapeDataString(result!.Code)}");
    }

    private static string? ValidateLoopbackUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp) return null;
        if (uri.Host != "127.0.0.1" && uri.Host != "localhost") return null;
        return uri.GetLeftPart(UriPartial.Path);
    }
}
