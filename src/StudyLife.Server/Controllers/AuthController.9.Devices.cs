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
        return NoContent();
    }
}
