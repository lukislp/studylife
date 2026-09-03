using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    // ── Consent connect flow (identity contract v1 §2) ────────────────────────
    // Originally MCP-only ("MCP OAuth connect flow"), generalized to a per-audience mechanism so
    // studylife-capture (the browser extension) can reuse the exact same single-use-assertion
    // machinery instead of a second, copy-pasted implementation - only the audience string, the
    // rotated key slot, and the two DTO shapes differ per consumer; the cache/expiry/single-use/
    // redirect-uri-validation logic lives exactly once, below.

    private static readonly TimeSpan McpAssertionLifetime = TimeSpan.FromSeconds(120);

    private const string AudienceMcp = "mcp";
    private const string AudienceCapture = "capture";
    private const string AudienceFocusGuard = "focusguard";
    private const string AudienceFocusTunes = "focustunes";
    private const string AudienceTray = "tray";

    /// <summary>Cached under a single-use assertion token, exactly like PendingHandoff -
    /// ApiKey is the ONE moment the plaintext exists after rotation until the consumer's
    /// server-to-server exchange picks it up (or the 120s expiry silently drops it). Audience
    /// ("mcp"/"capture") pins WHICH consumer this assertion was minted for - RedeemConsentAssertionAsync
    /// checks it against the endpoint actually hit, so an mcp-connect assertion can never be
    /// redeemed at capture-assertion-exchange or vice versa, even though both audiences share this
    /// exact same cache entry shape/key namespace.</summary>
    private sealed record PendingConsentAssertion(int UserId, string Audience, string ApiKey);

    /// <summary>
    /// SYNTACTIC redirect URI gate: an absolute https URL, or the RFC 8252 §8.3 native-app
    /// loopback exception (EXACTLY http://127.0.0.1:&lt;port&gt;/... or http://localhost:&lt;port&gt;/...,
    /// any port, any path). Nothing else non-https is ever accepted. This alone is NOT sufficient
    /// for the five hardcoded audiences anymore: the assertion-exchange endpoints are anonymous
    /// and the assertion is their only credential, so any https host that passed here could
    /// redeem it (2026-09 audit S1). BuildConnectRedirectAsync therefore additionally requires
    /// ConsentRedirectPolicy.IsAllowed - the per-audience allow-list. This method stays as the
    /// shared first-stage check, and internal instead of private because DeveloperController
    /// reuses it as-is when validating an OAuthClientEntity's AllowedRedirectUris at
    /// registration time (those are then matched exactly by AuthController.10.OAuthClients.cs).
    /// </summary>
    internal static bool IsAllowedRedirectUri(string? redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return ConsentRedirectPolicy.IsLoopback(uri);
    }

    /// <summary>
    /// Shared core of every consent "connect" action (step 3 of the flow): validates redirect_uri
    /// (IsAllowedRedirectUri), rotates the caller's key in the given slot via `rotateKey` (the
    /// SAME helper SettingsController's own generate endpoint for that slot uses, e.g. RotateMcpKey/
    /// RotateCaptureKey - never duplicated hashing), and stakes out a single-use, audience-bound
    /// assertion the browser carries back to the consumer's own callback, so the plaintext key
    /// never appears in a URL/browser history/redirect chain - only the opaque assertion does.
    /// Returns the error ActionResult instead of throwing for anything that shouldn't proceed; the
    /// two thin actions below translate success into their own audience-specific response DTO.
    /// </summary>
    private async Task<(string? RedirectTo, ActionResult? Error)> BuildConnectRedirectAsync(
        string audience, string redirectUri, string state, Func<AuthUserEntity, DateTime, string> rotateKey)
    {
        // Two-stage check. The syntactic gate alone is not enough: the exchange endpoints below
        // are anonymous and take nothing but the assertion, so ANY party receiving the browser
        // redirect can redeem it for the freshly rotated key - the redirect target must therefore
        // be a callback this audience is actually known to use (ConsentRedirectPolicy), not
        // merely "some https URL" (2026-09 audit S1).
        if (!IsAllowedRedirectUri(redirectUri))
            return (null, BadRequest("redirectUri must be an absolute https URL, or an http://127.0.0.1|localhost loopback URL (RFC 8252)."));
        if (!_consentRedirects.IsAllowed(audience, redirectUri))
            return (null, BadRequest("redirectUri is not an allowed callback for this audience (see Consent:AllowedRedirectUris)."));

        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)] on both callers
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return (null, Unauthorized());

        var apiKey = rotateKey(user, DateTime.UtcNow);
        await _db.SaveChangesAsync();

        var assertion = GenerateHandoffCode(); // same 256-bit CSPRNG base64url shape as the PKCE handoff code
        await CacheSetAsync(ConsentAssertionCacheKey(assertion), new PendingConsentAssertion(userId, audience, apiKey), McpAssertionLifetime);

        var separator = redirectUri.Contains('?') ? "&" : "?";
        var redirectTo = $"{redirectUri}{separator}assertion={Uri.EscapeDataString(assertion)}&state={Uri.EscapeDataString(state)}";
        return (redirectTo, null);
    }

    /// <summary>
    /// Shared core of every consent "assertion-exchange" action (step 4): validates the assertion
    /// and its Audience against the endpoint actually hit, and single-use-consumes it - but ONLY
    /// on an audience MATCH. Deliberately chosen consume-on-match-only semantics (same rationale
    /// as Exchange's own PKCE comment above): an mcp assertion presented at
    /// capture-assertion-exchange (wrong audience) must not be permanently burned by that
    /// misdirected/malicious attempt - the legitimate mcp-assertion-exchange call (which never even
    /// ran) must still be able to redeem it within the original 120s window. A caller presenting an
    /// assertion at the wrong endpoint gains nothing either way: it never learns whether the
    /// assertion was well-formed, expired, or simply wrong-audience - null (-> 401) uniformly for
    /// all three, same non-distinguishing pattern as Exchange/LoginComplete.
    /// </summary>
    private async Task<(int UserId, string ApiKey)?> RedeemConsentAssertionAsync(string audience, string assertion)
    {
        if (string.IsNullOrEmpty(assertion)) return null;

        var key = ConsentAssertionCacheKey(assertion);
        var pending = await CacheGetAsync<PendingConsentAssertion>(key);
        if (pending is null) return null;
        if (pending.Audience != audience) return null; // wrong-audience: NOT consumed, see summary above

        await _cache.RemoveAsync(key); // single-use: consumed on first successful (matching) exchange only
        return (pending.UserId, pending.ApiKey);
    }

    /// <summary>
    /// Step 3 of the MCP connect flow: session-required like the other privileged endpoints (the
    /// ONE action in this controller - alongside CaptureConnect - that is NOT [AllowAnonymous],
    /// since it needs the framework to actually reject non-session requests before the action
    /// runs). See BuildConnectRedirectAsync for the actual mechanism.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("mcp-connect")]
    public async Task<ActionResult<McpConnectResponseDto>> McpConnect([FromBody] McpConnectRequestDto request)
    {
        var (redirectTo, error) = await BuildConnectRedirectAsync(AudienceMcp, request.RedirectUri, request.State, SettingsController.RotateMcpKey);
        if (error is not null) return error;
        return new McpConnectResponseDto { RedirectTo = redirectTo! };
    }

    /// <summary>
    /// Step 4 of the MCP connect flow: exchanges a single-use, mcp-audience assertion for the real
    /// AuthUserId and the plaintext MCP key - called server-to-server by studylife-mcp against the
    /// cluster-internal StudyLife URL, never by the browser. EXEMPT from the API gate ([AllowAnonymous])
    /// and needs NO resolved user of its own: the assertion IS the credential, the userId it
    /// returns comes entirely from the cache entry McpConnect wrote - post-A2, an unresolved
    /// CurrentUserAccessor.AuthUserId must never silently fall back to "user 1", and this endpoint
    /// never touches it in the first place.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("mcp-assertion-exchange")]
    public async Task<ActionResult<McpAssertionExchangeResponseDto>> McpAssertionExchange([FromBody] McpAssertionExchangeRequestDto request)
    {
        var result = await RedeemConsentAssertionAsync(AudienceMcp, request.Assertion);
        if (result is null) return Unauthorized();
        return new McpAssertionExchangeResponseDto { UserId = result.Value.UserId, McpApiKey = result.Value.ApiKey };
    }

    /// <summary>
    /// Step 3 of the capture connect flow (identity contract v1 §2, generalized to the
    /// studylife-capture browser extension as a second audience): same session-required shape as
    /// McpConnect, rotating the capture key slot instead (SettingsController.RotateCaptureKey).
    /// The extension's own chrome.identity.launchWebAuthFlow supplies redirect_uri/state exactly
    /// like studylife-mcp's OAuth authorize redirect does for the mcp flow.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("capture-connect")]
    public async Task<ActionResult<CaptureConnectResponseDto>> CaptureConnect([FromBody] CaptureConnectRequestDto request)
    {
        var (redirectTo, error) = await BuildConnectRedirectAsync(AudienceCapture, request.RedirectUri, request.State, SettingsController.RotateCaptureKey);
        if (error is not null) return error;
        return new CaptureConnectResponseDto { RedirectTo = redirectTo! };
    }

    /// <summary>
    /// Step 4 of the capture connect flow: exchanges a single-use, capture-audience assertion for
    /// the real AuthUserId and the plaintext capture key - called by the extension's own background
    /// script against its configured StudyLife URL. EXEMPT from the API gate ([AllowAnonymous]),
    /// same non-distinguishing-401 and audience-isolation rules as McpAssertionExchange.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("capture-assertion-exchange")]
    public async Task<ActionResult<CaptureAssertionExchangeResponseDto>> CaptureAssertionExchange([FromBody] CaptureAssertionExchangeRequestDto request)
    {
        var result = await RedeemConsentAssertionAsync(AudienceCapture, request.Assertion);
        if (result is null) return Unauthorized();
        return new CaptureAssertionExchangeResponseDto { UserId = result.Value.UserId, CaptureApiKey = result.Value.ApiKey };
    }

    /// <summary>
    /// Step 3 of the focusguard connect flow (identity contract v1 §2, third audience alongside
    /// mcp/capture): same session-required shape as CaptureConnect, rotating the focusguard key
    /// slot instead (SettingsController.RotateFocusGuardKey). The extension's own
    /// chrome.identity.launchWebAuthFlow supplies redirect_uri/state exactly like the capture flow.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("focusguard-connect")]
    public async Task<ActionResult<FocusGuardConnectResponseDto>> FocusGuardConnect([FromBody] FocusGuardConnectRequestDto request)
    {
        var (redirectTo, error) = await BuildConnectRedirectAsync(AudienceFocusGuard, request.RedirectUri, request.State, SettingsController.RotateFocusGuardKey);
        if (error is not null) return error;
        return new FocusGuardConnectResponseDto { RedirectTo = redirectTo! };
    }

    /// <summary>
    /// Step 4 of the focusguard connect flow: exchanges a single-use, focusguard-audience
    /// assertion for the real AuthUserId and the plaintext focusguard key - called by the
    /// extension's own background script against its configured StudyLife URL. EXEMPT from the
    /// API gate ([AllowAnonymous]), same non-distinguishing-401 and audience-isolation rules as
    /// CaptureAssertionExchange.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("focusguard-assertion-exchange")]
    public async Task<ActionResult<FocusGuardAssertionExchangeResponseDto>> FocusGuardAssertionExchange([FromBody] FocusGuardAssertionExchangeRequestDto request)
    {
        var result = await RedeemConsentAssertionAsync(AudienceFocusGuard, request.Assertion);
        if (result is null) return Unauthorized();
        return new FocusGuardAssertionExchangeResponseDto { UserId = result.Value.UserId, FocusGuardApiKey = result.Value.ApiKey };
    }

    /// <summary>
    /// Step 3 of the focustunes connect flow (identity contract v1 §2, fourth audience): same
    /// session-required shape as CaptureConnect, rotating the focustunes key slot instead
    /// (SettingsController.RotateFocusTunesKey).
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("focustunes-connect")]
    public async Task<ActionResult<FocusTunesConnectResponseDto>> FocusTunesConnect([FromBody] FocusTunesConnectRequestDto request)
    {
        var (redirectTo, error) = await BuildConnectRedirectAsync(AudienceFocusTunes, request.RedirectUri, request.State, SettingsController.RotateFocusTunesKey);
        if (error is not null) return error;
        return new FocusTunesConnectResponseDto { RedirectTo = redirectTo! };
    }

    /// <summary>
    /// Step 4 of the focustunes connect flow: exchanges a single-use, focustunes-audience
    /// assertion for the real AuthUserId and the plaintext focustunes key. EXEMPT from the API
    /// gate ([AllowAnonymous]), same non-distinguishing-401 and audience-isolation rules as
    /// CaptureAssertionExchange.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("focustunes-assertion-exchange")]
    public async Task<ActionResult<FocusTunesAssertionExchangeResponseDto>> FocusTunesAssertionExchange([FromBody] FocusTunesAssertionExchangeRequestDto request)
    {
        var result = await RedeemConsentAssertionAsync(AudienceFocusTunes, request.Assertion);
        if (result is null) return Unauthorized();
        return new FocusTunesAssertionExchangeResponseDto { UserId = result.Value.UserId, FocusTunesApiKey = result.Value.ApiKey };
    }

    /// <summary>
    /// Step 3 of the tray connect flow (identity contract v1 §2, fifth audience): same
    /// session-required shape as FocusTunesConnect, rotating the tray key slot instead
    /// (SettingsController.RotateTrayKey). Unlike every browser-extension audience above, the
    /// caller here is a native desktop app - BuildConnectRedirectAsync's IsAllowedRedirectUri
    /// already accepts an RFC 8252 http://127.0.0.1|localhost loopback redirect_uri for exactly
    /// this reason (see studylife-mcp's CLI login flow for the other existing precedent).
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("tray-connect")]
    public async Task<ActionResult<TrayConnectResponseDto>> TrayConnect([FromBody] TrayConnectRequestDto request)
    {
        var (redirectTo, error) = await BuildConnectRedirectAsync(AudienceTray, request.RedirectUri, request.State, SettingsController.RotateTrayKey);
        if (error is not null) return error;
        return new TrayConnectResponseDto { RedirectTo = redirectTo! };
    }

    /// <summary>
    /// Step 4 of the tray connect flow: exchanges a single-use, tray-audience assertion for the
    /// real AuthUserId and the plaintext tray key - called by the app itself against its own
    /// loopback HTTP listener's captured redirect. EXEMPT from the API gate ([AllowAnonymous]),
    /// same non-distinguishing-401 and audience-isolation rules as FocusTunesAssertionExchange.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("tray-assertion-exchange")]
    public async Task<ActionResult<TrayAssertionExchangeResponseDto>> TrayAssertionExchange([FromBody] TrayAssertionExchangeRequestDto request)
    {
        var result = await RedeemConsentAssertionAsync(AudienceTray, request.Assertion);
        if (result is null) return Unauthorized();
        return new TrayAssertionExchangeResponseDto { UserId = result.Value.UserId, TrayApiKey = result.Value.ApiKey };
    }

    private static string ConsentAssertionCacheKey(string assertion) => $"consent-assertion:{assertion}";
}
