using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    // ── Native app token handoff (PKCE-style, RFC 7636/8252 §8.1) ────────────────

    private static readonly TimeSpan HandoffLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Token bound to the code_challenge given at handoff time - the token itself is
    /// only ever released again to whoever proves possession of the matching code_verifier.</summary>
    private sealed record PendingHandoff(string Token, string CodeChallenge);

    /// <summary>
    /// PKCE handoff (unauthenticated like the rest of /api/auth - the caller has just proven
    /// possession of a real token from register/login/recovery complete, that IS the
    /// authorization here): stores the token server-side under a fresh, single-use code bound
    /// to the app's code_challenge, so a native app shell's studylife:// custom-scheme redirect
    /// (or the Windows loopback listener) only ever has to carry the opaque code, never the
    /// bearer token itself - see AppReturnContext.BuildTokenReturnRedirectAsync for why this
    /// matters (a different app claiming the same custom scheme, or a local process racing the
    /// loopback port, could otherwise turn interception of the redirect into a full account
    /// takeover with no further step needed).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("handoff")]
    public async Task<ActionResult<AuthHandoffResponseDto>> Handoff([FromBody] AuthHandoffRequestDto request)
    {
        if (string.IsNullOrEmpty(request.Token) || string.IsNullOrEmpty(request.CodeChallenge))
            return BadRequest();

        var code = GenerateHandoffCode();
        await CacheSetAsync(HandoffCacheKey(code), new PendingHandoff(request.Token, request.CodeChallenge), HandoffLifetime);
        return new AuthHandoffResponseDto { Code = code };
    }

    /// <summary>
    /// Redeems a handoff code for the real session token - unauthenticated (the app has no
    /// session yet), protected instead by the code being single-use + short-lived + bound to a
    /// code_verifier that only the app which generated the original code_challenge can know
    /// (never transmitted until this exact call). Uniformly 401 for "not found"/"expired"/
    /// "wrong verifier", same non-distinguishing pattern as recovery/login.
    ///
    /// The cache entry is deliberately removed ONLY on a successful match, not on every
    /// attempt: whoever intercepts the code (the exact scenario this whole mechanism defends
    /// against) could otherwise grief the legitimate app's real exchange call by firing one
    /// request with a garbage verifier first - that would burn the single use without ever
    /// producing a token for anyone, a pure denial-of-service with no compensating security
    /// benefit (guessing the correct 256-bit verifier isn't something repeated attempts make
    /// meaningfully more likely). Found live during production testing of this exact flow.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("exchange")]
    public async Task<ActionResult<AuthExchangeResponseDto>> Exchange([FromBody] AuthExchangeRequestDto request)
    {
        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.CodeVerifier))
            return Unauthorized();

        var key = HandoffCacheKey(request.Code);
        var pending = await CacheGetAsync<PendingHandoff>(key);
        if (pending is null) return Unauthorized();

        var computedChallenge = Base64UrlEncode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(request.CodeVerifier)));
        var computedBytes = System.Text.Encoding.ASCII.GetBytes(computedChallenge);
        var expectedBytes = System.Text.Encoding.ASCII.GetBytes(pending.CodeChallenge);
        if (computedBytes.Length != expectedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(computedBytes, expectedBytes))
            return Unauthorized();

        await _cache.RemoveAsync(key); // single-use: only ever consumed on the successful redemption
        return new AuthExchangeResponseDto { Token = pending.Token };
    }

    private static string HandoffCacheKey(string code) => $"auth-handoff:{code}";
}
