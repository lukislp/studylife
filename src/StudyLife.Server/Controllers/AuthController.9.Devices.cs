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
    /// <summary>
    /// Client info about one's own account, currently only IsOwner (the explicit
    /// AuthUserEntity.IsOwner flag, see OwnershipService). The client uses this to avoid showing
    /// the backup/restore UI (Setup.razor, Index.razor reminder) to any other user in the first
    /// place, instead of letting them hit a 403 from BackupController.IsOwnerAsync on click.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("account-info")]
    public async Task<ActionResult<AccountInfoDto>> GetAccountInfo()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        return new AccountInfoDto { IsOwner = await _ownership.IsOwnerAsync(userId), UserId = userId };
    }

    /// <summary>Server-side invalidation of one's own session ("device lost" case) -
    /// the row is deleted, making the token immediately and permanently worthless.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var sessionId = (int)HttpContext.Items[AuthSessionService.SessionItemKey]!; // guaranteed by [Authorize(SessionOnly)]
        await _db.AuthSessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync();
        // The handler's per-pod session cache (AuthSessionCache) would otherwise keep answering
        // for this token for up to 30 more seconds on THIS pod - the explicit sign-out must be
        // immediate here, whatever the other pods do. Resolved from RequestServices instead of
        // the constructor so this partial does not touch the shared constructor signature.
        if (HttpContext.Items[AuthSessionService.SessionTokenHashItemKey] is string tokenHash)
            HttpContext.RequestServices.GetRequiredService<AuthSessionCache>().Remove(tokenHash);
        return NoContent();
    }

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("credentials")]
    public async Task<ActionResult<List<PasskeyListItemDto>>> ListCredentials()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        return await _db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.AuthUserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new PasskeyListItemDto
            {
                Id = c.Id,
                DeviceLabel = c.DeviceLabel,
                CreatedAt = c.CreatedAt,
                LastUsedAt = c.LastUsedAt,
                Pending = c.ApprovedAt == null,
            })
            .ToListAsync();
    }

    /// <summary>
    /// Approves a passkey created via register/begin-additional that has not yet been
    /// approved - callable ONLY from an already logged-in device of the same account
    /// (SessionAuthUserId must match the target credential). Afterward the new device can log
    /// in normally via login/begin+complete.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("credentials/{id:int}/approve")]
    public async Task<IActionResult> ApproveCredential(int id)
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var credential = await _db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == id && c.AuthUserId == userId);
        if (credential is null) return NotFound();
        if (credential.ApprovedAt is not null) return NoContent(); // already approved, idempotent

        credential.ApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPut("credentials/{id:int}/label")]
    public async Task<IActionResult> RenameCredential(int id, [FromBody] PasskeyRenameRequestDto request)
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var credential = await _db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == id && c.AuthUserId == userId);
        if (credential is null) return NotFound();

        var label = (request.Label ?? "").Trim();
        if (label.Length > 100) return BadRequest("Label must be at most 100 characters long.");
        credential.DeviceLabel = label.Length == 0 ? null : label;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpDelete("credentials/{id:int}")]
    public async Task<IActionResult> DeleteCredential(int id)
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var credential = await _db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == id && c.AuthUserId == userId);
        if (credential is null) return NotFound();

        // Deleting the last passkey would permanently lock the user out once their sessions
        // expire (there is no password fallback) - deliberately blocked. Only APPROVED
        // passkeys count toward this: a still-pending additional passkey (reject case) is
        // useless for login anyway and must never prevent deletion of the only real access
        // method.
        var ownApprovedCount = await _db.PasskeyCredentials.CountAsync(c => c.AuthUserId == userId && c.ApprovedAt != null);
        if (credential.ApprovedAt is not null && ownApprovedCount <= 1)
            return BadRequest("The last passkey of an account cannot be removed.");

        _db.PasskeyCredentials.Remove(credential);
        await _db.SaveChangesAsync();
        // Removing a passkey usually means "that device is gone / no longer trusted" - the
        // sessions it holds must not outlive the credential by up to 180 days. Only the caller's
        // own session survives (2026-09 audit S7).
        await RevokeOtherSessionsAsync(userId);
        return NoContent();
    }

    /// <summary>
    /// "Sign out everywhere else": deletes every session of the account except the one making
    /// this call, so a device the user no longer controls loses access immediately instead of
    /// at its sliding expiry. The current session stays - the user is acting from a device they
    /// evidently still hold.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("sessions/revoke-others")]
    public async Task<IActionResult> RevokeOtherSessions()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        await RevokeOtherSessionsAsync(userId);
        return NoContent();
    }

    private async Task<int> RevokeOtherSessionsAsync(int userId)
    {
        var currentSessionId = (int)HttpContext.Items[AuthSessionService.SessionItemKey]!; // set for every session-authenticated request
        var others = _db.AuthSessions.Where(s => s.AuthUserId == userId && s.Id != currentSessionId);
        // Evict the revoked tokens from this pod's AuthSessionCache as well - otherwise a device
        // signed out here could keep authenticating against THIS pod for up to 30 more seconds.
        // Other pods drop their entries at the cache's TTL (documented on AuthSessionCache).
        var revokedHashes = await others.Select(s => s.TokenHash).ToListAsync();
        var deleted = await others.ExecuteDeleteAsync();
        var cache = HttpContext.RequestServices.GetRequiredService<AuthSessionCache>();
        foreach (var hash in revokedHashes) cache.Remove(hash);
        return deleted;
    }

    /// <summary>
    /// The add-on keys issued to THIS user via the generic consent flow
    /// (AuthController.10.OAuthClients.cs) - one row per consent click. Joined with the client
    /// registration for a display name; a key whose registration has since been deleted still
    /// lists (with ClientName null) so it can be revoked. Session-only like the other
    /// credential-management endpoints: an API key must never be able to enumerate or revoke
    /// its siblings.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("client-keys")]
    public async Task<ActionResult<List<ClientApiKeyListItemDto>>> ListClientKeys()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        return await LoadClientKeysAsync(_db, userId);
    }

    // internal instead of private: reused by SetupController (bundle endpoint).
    internal static async Task<List<ClientApiKeyListItemDto>> LoadClientKeysAsync(StudyLifeDb db, int userId)
    {
        var keys = await db.ClientApiKeys.AsNoTracking()
            .Where(k => k.AuthUserId == userId)
            .OrderBy(k => k.CreatedAt)
            .ToListAsync();
        var clientIds = keys.Select(k => k.ClientId).Distinct().ToList();
        var names = await db.OAuthClients.AsNoTracking()
            .Where(c => clientIds.Contains(c.ClientId))
            .ToDictionaryAsync(c => c.ClientId, c => c.Name);
        return keys.Select(k => new ClientApiKeyListItemDto
        {
            Id = k.Id,
            ClientId = k.ClientId,
            ClientName = names.GetValueOrDefault(k.ClientId),
            GrantedScopes = ApiKeyScopes.Parse(k.GrantedScopes).Select(e => $"{e.Controller}.{e.Action}").OrderBy(s => s).ToList(),
            CreatedAt = k.CreatedAt,
        }).ToList();
    }

    /// <summary>Revokes one issued add-on key. The next request carrying it fails
    /// authentication (the handler looks the hash up per request, nothing is cached).</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpDelete("client-keys/{id:int}")]
    public async Task<IActionResult> RevokeClientKey(int id)
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var deleted = await _db.ClientApiKeys.Where(k => k.Id == id && k.AuthUserId == userId).ExecuteDeleteAsync();
        return deleted == 0 ? NotFound() : NoContent();
    }
}
