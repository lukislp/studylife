using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    // ── Registration invites (owner-only, audit finding A10) ────────────────────

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

        var now = DateTime.UtcNow;
        var token = AuthSessionService.GenerateToken();
        var invite = new AuthInviteEntity
        {
            TokenHash = AuthSessionService.HashToken(token),
            CreatedByUserId = userId,
            CreatedAt = now,
            ExpiresAt = now + RegistrationGateService.InviteLifetime,
        };
        _db.AuthInvites.Add(invite);
        await _db.SaveChangesAsync();

        return new CreateInviteResponseDto { Id = invite.Id, Token = token, CreatedAt = invite.CreatedAt, ExpiresAt = invite.ExpiresAt };
    }

    /// <summary>Lists every invite (owner-only) - never the token itself, only enough for the
    /// setup UI to show created/expires/used state per row (InviteListItemDto).</summary>
    [HttpGet("invites")]
    public async Task<ActionResult<List<InviteListItemDto>>> ListInvites()
    {
        if (!await IsOwnerAsync()) return Forbid();

        return await _db.AuthInvites.AsNoTracking()
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InviteListItemDto { Id = i.Id, CreatedAt = i.CreatedAt, ExpiresAt = i.ExpiresAt, UsedAt = i.UsedAt })
            .ToListAsync();
    }

    /// <summary>Permanently deletes an invite (owner-only) - works on unused, used, and expired
    /// rows alike (simple cleanup/revoke, no separate "revoke" vs. "delete" distinction).</summary>
    [HttpDelete("invites/{id:int}")]
    public async Task<IActionResult> DeleteInvite(int id)
    {
        if (!await IsOwnerAsync()) return Forbid();

        var invite = await _db.AuthInvites.FirstOrDefaultAsync(i => i.Id == id);
        if (invite is null) return NotFound();

        _db.AuthInvites.Remove(invite);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
