// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Http.Json;
using System.Text.RegularExpressions;
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
/// AuthController's handoff/exchange endpoints).
///
/// Since the 2026-11-16 audit (M2) the challenge is mandatory: ?app=1 without an S256-shaped
/// appchallenge is not an app flow at all - the page then behaves like the plain web sign-in and
/// never sends anything to a custom scheme or loopback URL, so another app registered for
/// studylife:// cannot open the page without a challenge and collect the raw token afterwards.
/// There is no "token=" redirect any more. Every released app build sends a challenge (mobile and
/// Windows alike), so none of them is locked out. The app may also send ?appstate= (its own
/// random value); it is echoed back as state= in every return redirect, so the app can refuse a
/// redirect it did not start (a forged studylife://auth?code=... while its sign-in is pending).
/// </summary>
public sealed class AppReturnContext
{
    private const string CustomSchemeReturn = "studylife://auth";

    private AppReturnContext(bool isActive, string? returnUrl, string? codeChallenge, string? state, string? language)
    {
        IsActive = isActive;
        _returnUrl = returnUrl;
        _codeChallenge = codeChallenge;
        _state = state;
        Language = language;
    }

    private const string SessionTokenHeader = "X-Session-Token";
    private static readonly Regex ChallengeShape = new(PkceShape.ChallengePattern);
    private static readonly Regex StateShape = new(PkceShape.StatePattern);

    private readonly string? _returnUrl;
    private readonly string? _codeChallenge;
    private readonly string? _state;

    /// <summary>True only for ?app=1 WITH a valid appchallenge (see class remarks).</summary>
    public bool IsActive { get; }

    /// <summary>The app's UI language (?applang=), so the auth page in the system browser speaks the
    /// language the app shows instead of whatever this browser last stored; null when absent or not a
    /// supported language. Program.cs passes it to UiLanguageStartup as the prescribed language.</summary>
    public string? Language { get; }

    public static AppReturnContext FromUri(Uri uri)
    {
        var appRequested = false;
        string? returnUrl = null;
        string? codeChallenge = null;
        string? state = null;
        string? language = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            if (pair == "app=1") appRequested = true;
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "appret")
                returnUrl = ValidateLoopbackUrl(Uri.UnescapeDataString(kv[1]));
            if (kv.Length == 2 && kv[0] == "appchallenge")
                codeChallenge = Matching(ChallengeShape, Uri.UnescapeDataString(kv[1]));
            if (kv.Length == 2 && kv[0] == "appstate")
                state = Matching(StateShape, Uri.UnescapeDataString(kv[1]));
            if (kv.Length == 2 && kv[0] == "applang")
                language = SupportedLanguages.Match(Uri.UnescapeDataString(kv[1]));
        }
        // The language is harmless and follows the app's request either way; everything that
        // decides where a sign-in result goes needs the challenge.
        var isActive = appRequested && codeChallenge is not null;
        return new AppReturnContext(isActive, isActive ? returnUrl : null, isActive ? codeChallenge : null,
            isActive ? state : null, appRequested ? language : null);
    }

    /// <summary>Query suffix that links between the auth pages use to carry the app context
    /// forward ("" in the normal web flow).</summary>
    public string LinkQuery => IsActive
        ? "?app=1"
          + (_returnUrl != null ? "&appret=" + Uri.EscapeDataString(_returnUrl) : "")
          + (_codeChallenge != null ? "&appchallenge=" + Uri.EscapeDataString(_codeChallenge) : "")
          + (_state != null ? "&appstate=" + Uri.EscapeDataString(_state) : "")
          + (Language != null ? "&applang=" + Uri.EscapeDataString(Language) : "")
        : "";

    /// <summary>Target URL for returning a non-token result to the app, e.g.
    /// BuildReturnRedirect("linked=1"). For a real session token, use
    /// BuildTokenReturnRedirectAsync instead.</summary>
    public string BuildReturnRedirect(string queryParams)
        => $"{_returnUrl ?? CustomSchemeReturn}?{queryParams}"
           + (_state != null ? "&state=" + Uri.EscapeDataString(_state) : "");

    /// <summary>Hands the real session token to the server in exchange for a short-lived,
    /// single-use handoff code bound to the app's code_challenge (see class remarks) and returns
    /// the redirect that carries only that code. The token authenticates the handoff request
    /// itself (the server takes it from the header, never from the body). Only valid in an app
    /// flow (<see cref="IsActive"/>); the pages never call it otherwise.</summary>
    public async Task<string> BuildTokenReturnRedirectAsync(HttpClient http, string token)
    {
        if (_codeChallenge is null)
            throw new InvalidOperationException("Not an app sign-in: there is no code challenge to hand the token off to.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/handoff")
        {
            Content = JsonContent.Create(new AuthHandoffRequestDto { CodeChallenge = _codeChallenge }),
        };
        // In an app flow the fresh token is not in this browser's store, so it goes on this request explicitly.
        request.Headers.Add(SessionTokenHeader, token);
        var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AuthHandoffResponseDto>();
        return BuildReturnRedirect($"code={Uri.EscapeDataString(result!.Code)}");
    }

    private static string? Matching(Regex shape, string value) => shape.IsMatch(value) ? value : null;

    private static string? ValidateLoopbackUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp) return null;
        if (uri.Host != "127.0.0.1" && uri.Host != "localhost") return null;
        return uri.GetLeftPart(UriPartial.Path);
    }
}
