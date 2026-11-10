// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel.DataAnnotations;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;
using StudyLife.Server.Services.Account;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    // ── Account deletion (App Store guideline 5.1.1(v)) ──────────────────────────────────────
    // Everything that decides or deletes lives in AccountDeletionService. What stays here is the
    // part that is tied to the request: which credential the call came with (session only - no
    // API key may delete an account), the fresh proof of presence, and the status codes.

    /// <summary>How long after its own sign-in a session still counts as proof that the user was
    /// just present. Covers the native shells that cannot run a passkey ceremony inside the web
    /// view and sign in again through the system browser right before deleting; the web client
    /// always sends an explicit proof instead.</summary>
    internal static readonly TimeSpan FreshSessionWindow = TimeSpan.FromMinutes(2);

    private sealed record PendingReauth(AssertionOptions Options, int UserId);

    /// <summary>Body of DELETE api/auth/account and DELETE api/auth/users/{id}: the fresh proof.
    /// Either a passkey assertion for the challenge of POST account/reauth/begin
    /// (<see cref="OptionsId"/> + <see cref="Response"/>) or one of the caller's own recovery codes.
    /// Both absent is accepted only for a session that signed in within
    /// <see cref="FreshSessionWindow"/>.</summary>
    public class AccountDeleteRequest
    {
        public string? OptionsId { get; set; }
        public AuthenticatorAssertionRawResponse? Response { get; set; }
        [MaxLength(50)]
        public string? RecoveryCode { get; set; }
    }

    /// <summary>What the "Delete account" card shows before the user commits: whether deletion is
    /// possible and, if not, why - plus what would go with it.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("account/deletion-preview")]
    public async Task<ActionResult<AccountDeletionPreviewDto>> GetAccountDeletionPreview() =>
        await _accountDeletion.PreviewAsync(SessionUserId);

    /// <summary>Issues the passkey challenge for the re-authentication that guards deletion. Limited
    /// to the caller's own approved passkeys; 400 when there is none (the client then offers a
    /// recovery code).</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("account/reauth/begin")]
    public async Task<ActionResult<PasskeyBeginResponseDto>> BeginReauthentication()
    {
        var userId = SessionUserId;
        var ownCredentials = await _db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.AuthUserId == userId && c.ApprovedAt != null)
            .Select(c => c.CredentialId)
            .ToListAsync();
        if (ownCredentials.Count == 0) return BadRequest("This account has no passkey to confirm with.");

        var options = CreateFido2().GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = ownCredentials.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            UserVerification = UserVerificationRequirement.Preferred,
        });
        var optionsId = Guid.NewGuid().ToString("N");
        await CacheSetAsync(ReauthCacheKey(optionsId), new PendingReauth(options, userId), ChallengeLifetime);
        return new PasskeyBeginResponseDto { OptionsId = optionsId, OptionsJson = options.ToJson() };
    }

    /// <summary>Deletes the calling account and everything it owns. Session-only; needs a fresh proof
    /// of presence (see <see cref="AccountDeleteRequest"/>). 409 when the caller is the owner and
    /// other accounts exist.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpDelete("account")]
    public async Task<ActionResult<AccountDeletionResultDto>> DeleteAccount([FromBody] AccountDeleteRequest? request, CancellationToken ct) =>
        await DeleteWithProofAsync(SessionUserId, request, ct);

    /// <summary>The owner's list behind "remove user": every other account, by display name.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("users")]
    public async Task<ActionResult<List<UserListItemDto>>> ListUsers()
    {
        if (!await _ownership.IsOwnerAsync(SessionUserId)) return StatusCode(StatusCodes.Status403Forbidden);
        return await _accountDeletion.ListOtherUsersAsync(SessionUserId);
    }

    /// <summary>Owner-only: removes another account through the SAME use case as self-deletion,
    /// confirmed with the owner's own fresh proof.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpDelete("users/{id:int}")]
    public async Task<ActionResult<AccountDeletionResultDto>> RemoveUser(int id, [FromBody] AccountDeleteRequest? request, CancellationToken ct)
    {
        if (id == SessionUserId) return BadRequest("Use account deletion to remove your own account.");
        return await DeleteWithProofAsync(id, request, ct);
    }

    private async Task<ActionResult<AccountDeletionResultDto>> DeleteWithProofAsync(int targetUserId, AccountDeleteRequest? request, CancellationToken ct)
    {
        request ??= new AccountDeleteRequest();
        var callerId = SessionUserId;

        // Policy first: a one-time recovery code must not be spent on a request that is refused anyway.
        var policy = await _accountDeletion.CheckPolicyAsync(targetUserId, callerId);
        if (MapRefusal(policy) is { } refusal) return refusal;

        var proof = await VerifyFreshProofAsync(request, callerId);
        if (proof == FreshProof.Missing)
            return BadRequest("A fresh proof is required: confirm with a passkey, a recovery code, or sign in again.");
        if (proof == FreshProof.Failed)
            return Problem(statusCode: StatusCodes.Status403Forbidden, type: "reauthentication-failed",
                title: "reauthentication-failed", detail: "The confirmation was not accepted.");

        var result = await _accountDeletion.DeleteAsync(targetUserId, callerId, ct);
        if (MapRefusal(result.Outcome) is { } lateRefusal) return lateRefusal;
        return result.Value!;
    }

    private ActionResult? MapRefusal(AccountDeletionOutcome outcome) => outcome switch
    {
        AccountDeletionOutcome.Deleted => null,
        AccountDeletionOutcome.NotFound => NotFound(),
        AccountDeletionOutcome.DemoMode => Problem(statusCode: StatusCodes.Status403Forbidden, type: "demo-readonly",
            title: "demo-readonly", detail: "Accounts cannot be deleted on the demo instance."),
        AccountDeletionOutcome.InitiatorNotOwner => Problem(statusCode: StatusCodes.Status403Forbidden, type: "owner-only",
            title: "owner-only", detail: "Only the owner can remove another account."),
        AccountDeletionOutcome.OwnerHasOtherUsers => Problem(statusCode: StatusCodes.Status409Conflict, type: "owner-has-other-users",
            title: "owner-has-other-users",
            detail: "The owner administers this installation. Remove the other accounts first, or keep this one."),
        _ => StatusCode(StatusCodes.Status500InternalServerError),
    };

    private enum FreshProof { Accepted, Missing, Failed }

    /// <summary>Re-authentication right before a destructive action. Order: an explicit recovery
    /// code, an explicit passkey assertion, and only without either the recency of the session's
    /// own sign-in. An explicit proof that fails is a failure - it never falls back to recency.</summary>
    private async Task<FreshProof> VerifyFreshProofAsync(AccountDeleteRequest request, int userId)
    {
        if (!string.IsNullOrWhiteSpace(request.RecoveryCode))
            return await _recovery.TryConsumeForUserAsync(userId, request.RecoveryCode) ? FreshProof.Accepted : FreshProof.Failed;

        if (request.Response is not null || !string.IsNullOrEmpty(request.OptionsId))
        {
            if (request.Response is null || string.IsNullOrEmpty(request.OptionsId)) return FreshProof.Failed;
            var pending = await CacheGetAsync<PendingReauth>(ReauthCacheKey(request.OptionsId));
            if (pending is null) return FreshProof.Failed;
            await _cache.RemoveAsync(ReauthCacheKey(request.OptionsId)); // single use, like every challenge here
            if (pending.UserId != userId) return FreshProof.Failed;

            var verified = await VerifyAssertionAsync(pending.Options, request.Response, onlyUserId: userId);
            if (verified is null || verified.Credential.ApprovedAt is null) return FreshProof.Failed;

            verified.Credential.SignCount = verified.SignCount;
            verified.Credential.LastUsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return FreshProof.Accepted;
        }

        var issuedAt = await _db.AuthSessions.AsNoTracking()
            .Where(s => s.Id == CurrentSessionId).Select(s => (DateTime?)s.IssuedAt).FirstOrDefaultAsync();
        return issuedAt is { } at && DateTime.UtcNow - at <= FreshSessionWindow ? FreshProof.Accepted : FreshProof.Missing;
    }

    private static string ReauthCacheKey(string optionsId) => $"passkey-reauth:{optionsId}";
}
