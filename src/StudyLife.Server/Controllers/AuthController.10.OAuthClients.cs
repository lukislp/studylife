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
    // ── Generic OAuth-style connect flow (add-on marketplace foundation) ──────────────────────
    // Generalizes BuildConnectRedirectAsync/RedeemConsentAssertionAsync (AuthController.5.Consent.cs)
    // for dynamically registered clients (OAuthClientEntity, see DeveloperController) instead of
    // one of the 5 hardcoded audiences there - those 5 are deliberately untouched. Reuses the
    // exact same single-use-assertion cache mechanism (ConsentAssertionCacheKey/
    // PendingConsentAssertion/McpAssertionLifetime); only the "audience" here is
    // "client:{clientId}" instead of a fixed string. Unlike the hardcoded audiences, the caller
    // must resolve WHICH client wants what before rendering consent (GetOAuthClientInfo below) -
    // the 5 hardcoded flows never needed this, their consent copy is baked into the client-side
    // Razor component per audience.

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("oauth-clients/{clientId}")]
    public async Task<ActionResult<OAuthClientInfoDto>> GetOAuthClientInfo(string clientId)
    {
        var client = await _db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId);
        if (client is null) return NotFound();
        return new OAuthClientInfoDto
        {
            Name = client.Name,
            Description = client.Description,
            RequestedScopes = ApiKeyScopes.Parse(client.RequestedScopes).Select(e => $"{e.Controller}.{e.Action}").ToList(),
        };
    }

    /// <summary>
    /// Step 3, generalized: same role as McpConnect/TrayConnect/etc., but resolves the client
    /// from OAuthClientEntity instead of a fixed audience string. redirectUri must EXACTLY match
    /// one of the client's own registered AllowedRedirectUris - stricter than
    /// IsAllowedRedirectUri's blanket https-or-loopback check the 5 hardcoded audiences use,
    /// since here the set of trusted values is itself developer-controlled data.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("connect")]
    public async Task<ActionResult<GenericConnectResponseDto>> Connect([FromBody] GenericConnectRequestDto request)
    {
        var client = await _db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == request.ClientId);
        if (client is null) return NotFound();

        var allowedUris = client.AllowedRedirectUris.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!allowedUris.Contains(request.RedirectUri, StringComparer.Ordinal))
            return BadRequest("redirectUri must exactly match one of this client's registered redirect URIs.");

        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var key = AuthSessionService.GenerateToken();
        var entity = new ClientApiKeyEntity
        {
            AuthUserId = userId,
            ClientId = client.ClientId,
            // SNAPSHOT at this exact moment - see ClientApiKeyEntity.GrantedScopes for why this
            // must never be re-read live from OAuthClientEntity later.
            GrantedScopes = client.RequestedScopes,
            KeyHash = AuthSessionService.HashToken(key),
            CreatedAt = DateTime.UtcNow,
        };
        _db.ClientApiKeys.Add(entity);
        await _db.SaveChangesAsync();

        var audience = $"client:{client.ClientId}";
        var assertion = GenerateHandoffCode();
        await CacheSetAsync(ConsentAssertionCacheKey(assertion), new PendingConsentAssertion(userId, audience, key), McpAssertionLifetime);

        var separator = request.RedirectUri.Contains('?') ? "&" : "?";
        var redirectTo = $"{request.RedirectUri}{separator}assertion={Uri.EscapeDataString(assertion)}&state={Uri.EscapeDataString(request.State)}";
        return new GenericConnectResponseDto { RedirectTo = redirectTo };
    }

    /// <summary>
    /// Step 4, generalized: unlike the 5 hardcoded per-audience exchange endpoints, this one is
    /// shared across every dynamic client, so ClientId has to be supplied explicitly (it used to
    /// be implicit in the endpoint's own URL, e.g. tray-assertion-exchange). Same non-
    /// distinguishing-401 and audience-isolation rules as every other *AssertionExchange action.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("assertion-exchange")]
    public async Task<ActionResult<GenericAssertionExchangeResponseDto>> AssertionExchange([FromBody] GenericAssertionExchangeRequestDto request)
    {
        var result = await RedeemConsentAssertionAsync($"client:{request.ClientId}", request.Assertion);
        if (result is null) return Unauthorized();
        return new GenericAssertionExchangeResponseDto { UserId = result.Value.UserId, ApiKey = result.Value.ApiKey };
    }
}
