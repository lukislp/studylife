// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;
using StudyLife.Server.Services.SignIn;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

public partial class AuthController
{
    // ── Username + password (docs/ARCHITECTURE.md "Password sign-in") ──────────────────────
    // A second sign-in method next to passkeys, behind the operator switch PasswordSignIn:Enabled.
    // Everything that decides lives in IPasswordCredentialService and the pure policies of
    // Services/SignIn; what stays here is the request part: which credential the call came with,
    // the setup-code/registration gates shared with the passkey registration, the shared step-up
    // check (VerifyFreshProofAsync), the shared session issuance (IssueSignInSessionAsync) and the
    // status codes. Brute force: per-IP partitions in Program.cs, per-account lockout in the service.

    /// <summary>PUT api/auth/password: add a password to the calling account or change it. The login
    /// name is fixed once the account has a password: a blank or equal <see cref="Username"/> is
    /// fine, a different one is refused (400 username-fixed). Only the first password chooses it.</summary>
    public class PasswordSetRequest : FreshProofRequest
    {
        [MaxLength(256)]
        public string? Username { get; set; }
        [MaxLength(512)]
        public string? NewPassword { get; set; }
    }

    /// <summary>POST api/auth/users/{id}/password-reset: the login name is only needed (and only
    /// accepted) when the account has none yet; a different one is refused (400 username-fixed).</summary>
    public class PasswordResetRequest : FreshProofRequest
    {
        [MaxLength(256)]
        public string? Username { get; set; }
    }

    /// <summary>Which sign-in methods the sign-in and registration pages offer. Anonymous - it
    /// reveals only the operator's switch, which the pages show anyway. Never offered on a demo
    /// instance (it signs in automatically).</summary>
    [AllowAnonymous]
    [HttpGet("methods")]
    public ActionResult<AuthMethodsDto> GetSignInMethods() =>
        new AuthMethodsDto { PasswordSignIn = _passwords.Enabled && !DemoModeEnabled };

    /// <summary>
    /// Sign in with a username and password. Uniformly 401 for an unknown name, a wrong password and
    /// a temporarily locked account, with the same work done in all three cases (the service runs a
    /// dummy hash verification), so neither the answer nor its timing tells which names exist. A
    /// success issues exactly the session a passkey sign-in issues.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("password/login")]
    public async Task<ActionResult<PasswordLoginResponseDto>> PasswordLogin([FromBody] PasswordLoginRequestDto request)
    {
        if (!_passwords.Enabled) return PasswordProblemResult(PasswordProblem.Disabled);

        var result = await _passwords.SignInAsync(request.Username, request.Password);
        if (!result.Succeeded) return Unauthorized();

        var (token, displayName) = await IssueSignInSessionAsync(result.UserId!.Value, DateTime.UtcNow);
        return new PasswordLoginResponseDto
        {
            Token = token,
            DisplayName = displayName,
            PasswordChangeRequired = result.ChangeRequired,
        };
    }

    /// <summary>
    /// Create an account that signs in with a username and password. The same gates as
    /// register/begin: the setup code while the installation is unclaimed, Registration:Mode and an
    /// invite afterwards; the same account decision (legacy-user claim, owner flag) through
    /// ProvisionSelfRegisteredUserAsync. 409 username-taken when the name is in use - unavoidable
    /// for a user-chosen unique name, and only reachable through the registration gate and its
    /// rate-limit partition.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("password/register")]
    public async Task<ActionResult<PasswordLoginResponseDto>> PasswordRegister([FromBody] PasswordRegisterRequestDto request)
    {
        if (!_passwords.Enabled) return PasswordProblemResult(PasswordProblem.Disabled);

        var displayName = (request.DisplayName ?? "").Trim();
        if (displayName.Length is 0 or > 100)
            return BadRequest("DisplayName must be between 1 and 100 characters long.");

        var claimed = await InstanceBootstrap.IsClaimedAsync(_db);
        if (!claimed && !await _systemSecrets.ValidateSetupSecretAsync(request.SetupSecret))
            return Unauthorized("Setup code required or invalid - see server logs.");

        var inviteToken = request.InviteToken?.Trim();
        if (claimed && await GateRefusalAsync(inviteToken) is { } gateRefusal) return gateRefusal;

        // Validate and hash BEFORE the transaction: the KDF must not run while the write lock is held.
        var (problem, prepared) = await _passwords.PrepareAsync(forUserId: null, request.Username, request.Password);
        if (problem != PasswordProblem.None) return PasswordProblemResult(problem);

        var now = DateTime.UtcNow;
        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Re-read inside the transaction: somebody may have claimed the installation in between,
        // in which case the setup code no longer suffices and the registration gate applies.
        var claimedNow = await InstanceBootstrap.IsClaimedAsync(_db);
        if (claimedNow && !claimed && await GateRefusalAsync(inviteToken) is { } lateRefusal)
        {
            await transaction.RollbackAsync();
            return lateRefusal;
        }

        var targetUser = await ProvisionSelfRegisteredUserAsync(displayName, claimedNow, now);

        // Invite consumption exactly as RegisterComplete does it (atomic UPDATE ... WHERE UsedAt IS NULL).
        if (claimedNow && _registrationGate.CurrentMode == RegistrationMode.Invite && inviteToken is { Length: > 0 }
            && !await _registrationGate.TryConsumeInviteAsync(inviteToken, targetUser.Id, now))
        {
            await transaction.RollbackAsync();
            return StatusCode(StatusCodes.Status403Forbidden, new RegistrationGateErrorDto { Reason = "invite_invalid" });
        }

        _passwords.AddForNewAccount(targetUser.Id, prepared!, now);
        var token = AuthSessionService.IssueSession(_db, targetUser.Id, now);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost the race for the same login name (unique index).
            await transaction.RollbackAsync();
            return PasswordProblemResult(PasswordProblem.UsernameTaken);
        }
        await transaction.CommitAsync();

        return new PasswordLoginResponseDto { Token = token, DisplayName = targetUser.DisplayName };
    }

    private async Task<ActionResult?> GateRefusalAsync(string? inviteToken)
    {
        var decision = await _registrationGate.CheckBeginAsync(inviteToken);
        return decision == RegistrationGateDecision.Allowed
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, new RegistrationGateErrorDto { Reason = ReasonFor(decision) });
    }

    /// <summary>The password card in Settings: whether passwords are on, whether this account has
    /// one, its login name, and whether it may be removed.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("password")]
    public async Task<ActionResult<PasswordStatusDto>> GetPasswordStatus() =>
        await _passwords.GetStatusAsync(SessionUserId);

    /// <summary>
    /// Add a password to the calling account, or change it. Session-only and with a fresh proof
    /// (passkey, recovery code, the current password, or a sign-in within the last two minutes):
    /// without it a stolen session token alone could plant a lasting second way in - the same
    /// reason an additional passkey stays pending until approved. Changing revokes every other
    /// session; this one stays.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPut("password")]
    public async Task<IActionResult> SetPassword([FromBody] PasswordSetRequest request)
    {
        var userId = SessionUserId;
        // Input first: a one-time recovery code must not be spent on a password that is refused anyway.
        var (problem, prepared) = await _passwords.PrepareAsync(userId, request.Username, request.NewPassword);
        if (problem != PasswordProblem.None) return PasswordProblemResult(problem);
        if (await ProofRefusalAsync(request, userId) is { } refusal) return refusal;

        var result = await _passwords.SetAsync(userId, prepared!, keepSessionId: CurrentSessionId);
        return result == PasswordProblem.None ? NoContent() : PasswordProblemResult(result);
    }

    /// <summary>Remove the calling account's password. Refused (409 last-sign-in-method) while it is
    /// the account's only usable sign-in method. Works while passwords are switched off too, so a
    /// user can clear a password they can no longer use. Revokes every other session.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpDelete("password")]
    public async Task<IActionResult> RemovePassword([FromBody] FreshProofRequest? request)
    {
        request ??= new FreshProofRequest();
        var userId = SessionUserId;
        var removable = await _passwords.CheckRemovableAsync(userId);
        if (removable != PasswordProblem.None) return PasswordProblemResult(removable);
        if (await ProofRefusalAsync(request, userId) is { } refusal) return refusal;

        var result = await _passwords.RemoveAsync(userId, CurrentSessionId);
        return result == PasswordProblem.None ? NoContent() : PasswordProblemResult(result);
    }

    /// <summary>
    /// Owner only: give another account a temporary password, shown to the owner exactly once (only
    /// its hash is stored). Meant for "lost every passkey and every recovery code". Revokes every
    /// session of that account and marks the password as temporary, so the user is asked to choose
    /// their own after signing in. Confirmed with the owner's own fresh proof, like removing a user.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("users/{id:int}/password-reset")]
    public async Task<ActionResult<PasswordResetResultDto>> ResetUserPassword(int id, [FromBody] PasswordResetRequest? request)
    {
        request ??= new PasswordResetRequest();
        var callerId = SessionUserId;
        if (!_passwords.Enabled) return PasswordProblemResult(PasswordProblem.Disabled);
        if (!await _ownership.IsOwnerAsync(callerId))
            return Problem(statusCode: StatusCodes.Status403Forbidden, type: "owner-only",
                title: "owner-only", detail: "Only the owner can reset another account's password.");
        if (id == callerId) return BadRequest("Use the password settings to change your own password.");
        if (!await _db.AuthUsers.AnyAsync(u => u.Id == id)) return NotFound();

        // A random temporary password never trips the common-password check; it could only contain
        // a short login name by sheer chance - draw again in that case.
        string temporary;
        (PasswordProblem Problem, PreparedPassword? Prepared) prepared;
        var attempts = 0;
        do
        {
            temporary = _passwords.GenerateTemporaryPassword();
            prepared = await _passwords.PrepareAsync(id, request.Username, temporary);
        }
        while (prepared.Problem == PasswordProblem.ContainsUsername && ++attempts < 5);
        if (prepared.Problem != PasswordProblem.None) return PasswordProblemResult(prepared.Problem);

        if (await ProofRefusalAsync(request, callerId) is { } refusal) return refusal;

        var result = await _passwords.SetAsync(id, prepared.Prepared!, keepSessionId: null, changeRequired: true);
        if (result != PasswordProblem.None) return PasswordProblemResult(result);
        return new PasswordResetResultDto { Username = prepared.Prepared!.Username, TemporaryPassword = temporary };
    }

    private async Task<ActionResult?> ProofRefusalAsync(FreshProofRequest request, int userId) =>
        await VerifyFreshProofAsync(request, userId) switch
        {
            FreshProof.Accepted => null,
            FreshProof.Missing => Problem(statusCode: StatusCodes.Status400BadRequest, type: "reauthentication-required",
                title: "reauthentication-required",
                detail: "A fresh proof is required: confirm with a passkey, a recovery code, your password, or sign in again."),
            _ => Problem(statusCode: StatusCodes.Status403Forbidden, type: "reauthentication-failed",
                title: "reauthentication-failed", detail: "The confirmation was not accepted."),
        };

    /// <summary>Stable RFC 9457 problem types the client switches on (PasswordSignInState).</summary>
    private ActionResult PasswordProblemResult(PasswordProblem problem)
    {
        var (status, type, detail) = problem switch
        {
            PasswordProblem.Disabled => (StatusCodes.Status403Forbidden, "password-sign-in-disabled",
                "Password sign-in is switched off on this installation."),
            PasswordProblem.UsernameInvalid => (StatusCodes.Status400BadRequest, "username-invalid",
                $"The username needs {UsernamePolicy.MinLength} to {UsernamePolicy.MaxLength} letters, digits, '.', '_', '-' or '@' and must start with a letter or digit."),
            PasswordProblem.UsernameTaken => (StatusCodes.Status409Conflict, "username-taken", "This username is already in use."),
            PasswordProblem.UsernameFixed => (StatusCodes.Status400BadRequest, "username-fixed",
                "The login name of an account with a password cannot be changed. Remove the password first to choose a new name."),
            PasswordProblem.TooShort => (StatusCodes.Status400BadRequest, "password-too-short",
                $"The password needs at least {PasswordPolicy.MinLength} characters."),
            PasswordProblem.TooLong => (StatusCodes.Status400BadRequest, "password-too-long",
                $"The password may have at most {PasswordPolicy.MaxLength} characters."),
            PasswordProblem.Common => (StatusCodes.Status400BadRequest, "password-common",
                "This password is too common or too predictable."),
            PasswordProblem.ContainsUsername => (StatusCodes.Status400BadRequest, "password-contains-username",
                "The password must not contain the username."),
            PasswordProblem.LastMethod => (StatusCodes.Status409Conflict, "last-sign-in-method",
                "The password is the only way to sign in to this account. Add a passkey first."),
            PasswordProblem.NotFound => (StatusCodes.Status404NotFound, "password-not-set", "This account has no password."),
            _ => (StatusCodes.Status500InternalServerError, "unexpected", "Unexpected outcome."),
        };
        return Problem(statusCode: status, type: type, title: type, detail: detail);
    }
}
