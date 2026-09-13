using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    // ── Registration invites (owner-only, audit finding A10) ────────────────────
    // The persistence lives in AuthInviteService; what stays here is the access control, which
    // is exactly what must NOT move: the owner check has to answer 403 and it has to run before
    // the service is ever asked anything.

    /// <summary>
    /// Owner-only, session-only, deliberately NOT [Authorize(Policy = SessionOnly)] - same
    /// reasoning as BackupController.IsOwnerAsync (mirrored here verbatim): a merely-authenticated-
    /// but-not-owner session must get 403 (not 401), which the automatic SessionOnly policy
    /// pipeline can't express (it always challenges with 401, see
    /// AlwaysChallengeAuthorizationMiddlewareResultHandler), so this stays a manual check that
    /// calls Forbid() itself. Falls through to the default ApiAccess policy (no attribute at all)
    /// like BackupController - and, just as importantly, these three actions are deliberately NOT
    /// added to ApiKeyScopes for any slot (ha/ai/mcp/capture): a bare API key hitting them fails
    /// ONLY the ApiKeyScopeRequirement, which AlwaysChallengeAuthorizationMiddlewareResultHandler's
    /// one documented exception turns into 403 automatically, before this method (or the action)
    /// ever runs - so "api key -> 403" and "non-owner session -> 403" both hold, via two different
    /// mechanisms, without an explicit [Authorize] attribute getting in the way of either.
    /// </summary>
    private Task<bool> IsOwnerAsync() =>
        HttpContext.SessionAuthUserId() is int sessionUserId ? _ownership.IsOwnerAsync(sessionUserId) : Task.FromResult(false);

    /// <summary>
    /// Generates a new invite (owner-only): returns the PLAINTEXT token exactly once, only its
    /// SHA-256 hash is persisted (AuthInviteEntity.TokenHash) - same one-time-visibility pattern as
    /// HaApiKeyGenerateResponseDto/RecoveryCodesResponseDto. The client builds the shareable
    /// "/register?invite=&lt;token&gt;" link itself from Token plus its own origin.
    /// </summary>
    [HttpPost("invites")]
    public async Task<ActionResult<CreateInviteResponseDto>> CreateInvite()
    {
        if (!await IsOwnerAsync()) return Forbid();
        var userId = HttpContext.SessionAuthUserId()!.Value;

        return await _invites.CreateAsync(userId);
    }

    /// <summary>Lists every invite (owner-only) - never the token itself, only enough for the
    /// setup UI to show created/expires/used state per row (InviteListItemDto).</summary>
    [HttpGet("invites")]
    public async Task<ActionResult<List<InviteListItemDto>>> ListInvites()
    {
        if (!await IsOwnerAsync()) return Forbid();
        return await _invites.ListAsync();
    }

    /// <summary>Permanently deletes an invite (owner-only) - works on unused, used, and expired
    /// rows alike (simple cleanup/revoke, no separate "revoke" vs. "delete" distinction).</summary>
    [HttpDelete("invites/{id:int}")]
    public async Task<IActionResult> DeleteInvite(int id)
    {
        if (!await IsOwnerAsync()) return Forbid();
        return (await _invites.DeleteAsync(id)).ToNoContentResult(this);
    }
}
