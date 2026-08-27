using Fido2NetLib;
using Fido2NetLib.Objects;
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
    /// <summary>Challenge intermediate state of an ongoing registration. ForAuthUserId != null
    /// for "additional passkey for one's own account" (via session OR via link code) - for
    /// open registration, the user decision (claim legacy user vs. create new) is only made
    /// in Complete. RequiresSessionAtComplete distinguishes HOW ForAuthUserId was authorized:
    /// true = via begin-additional (Complete must see the same valid session again),
    /// false = via link code (begin-linked already verified authorization via the code itself -
    /// the calling device has no session at all). LinkCode is only set on the code path, so
    /// Complete can mark it as consumed after success.</summary>
    /// <summary>InviteToken (audit finding A10): only ever set on the self-registration path
    /// (ForAuthUserId == null) once RegisterBegin's RegistrationGateService check required and
    /// validated one (Registration:Mode=invite, past bootstrap) - carried forward to Complete so
    /// TryConsumeInviteAsync consumes the SAME token that was validated at Begin, atomically with
    /// user/credential creation, never at Begin itself (see RegisterComplete).</summary>
    private sealed record PendingRegistration(CredentialCreateOptions Options, string DisplayName, int? ForAuthUserId, bool RequiresSessionAtComplete, string? LinkCode = null, string? InviteToken = null);

    public class PasskeyRegisterCompleteRequest
    {
        public string OptionsId { get; set; } = "";
        public AuthenticatorAttestationRawResponse Response { get; set; } = default!;
    }

    // ── Registration (openly accessible) ─────────────────────────────────────

    [AllowAnonymous]
    [HttpPost("register/begin")]
    public async Task<ActionResult<PasskeyBeginResponseDto>> RegisterBegin([FromBody] PasskeyRegisterBeginRequestDto request)
    {
        var displayName = (request.DisplayName ?? "").Trim();
        if (displayName.Length is 0 or > 100)
            return BadRequest("DisplayName must be between 1 and 100 characters long.");

        // Bootstrap flag: shared by the pre-existing setup-secret check below AND the
        // RegistrationGateService check further down (audit A10) - "not a single passkey exists
        // yet" is this app's established notion of "the instance hasn't been claimed/set up yet"
        // (RegisterComplete uses the exact same predicate for the legacy-user-claim decision).
        // The registration-mode gate is deliberately skipped entirely while this is true: a fresh
        // (or restored-empty) install must never be bricked by Registration:Mode=invite/closed
        // before an owner has even had the chance to log in and generate an invite in the first
        // place (nobody could - invite creation is owner-only, see the /api/auth/invites group).
        var anyPasskeyExists = await _db.PasskeyCredentials.AnyAsync();

        // The first registration ever requires the setup secret generated at startup and printed
        // to the container logs - this prevents anyone who reaches the open registration endpoint
        // before the actual operator from automatically becoming owner (inheriting existing data +
        // backup/restore rights, see BackupController.IsOwnerAsync). Unaffected by
        // Registration:Mode - unlike "family signup" below, this check ITSELF already gates the
        // one case Registration:Mode:closed would otherwise also want to block.
        if (!anyPasskeyExists && !await _systemSecrets.ValidateSetupSecretAsync(request.SetupSecret))
            return Unauthorized("Setup code required or invalid - see server logs.");

        var inviteToken = request.InviteToken?.Trim();
        if (anyPasskeyExists)
        {
            // Past bootstrap: Registration:Mode now governs whether "family signup" may still
            // happen at all, and if so, whether it needs a valid invite (audit A10 - this used to
            // be unconditionally open once the setup-secret gate above no longer applied).
            var decision = await _registrationGate.CheckBeginAsync(inviteToken);
            if (decision != RegistrationGateDecision.Allowed)
                return StatusCode(StatusCodes.Status403Forbidden, new RegistrationGateErrorDto { Reason = ReasonFor(decision) });
        }

        return await BeginRegistration(displayName, forAuthUserId: null, requiresSessionAtComplete: true,
            excludeCredentials: [], inviteToken: inviteToken);
    }

    /// <summary>
    /// Additional passkey for one's OWN, already logged-in account (e.g. a second device) -
    /// unlike register/begin this requires a valid session and attaches the new passkey to the
    /// same AuthUserId, instead of going through the legacy-user/new-user decision. Assumes that
    /// THIS (already logged-in) device itself generates the new passkey - for a physically
    /// DIFFERENT, not-yet-logged-in device see register/begin-linked.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("register/begin-additional")]
    public async Task<ActionResult<PasskeyBeginResponseDto>> RegisterBeginAdditional()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        // Exclude the user's already registered passkeys so the browser doesn't
        // offer the same authenticator a second time.
        var exclude = await _db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.AuthUserId == userId)
            .Select(c => c.CredentialId)
            .ToListAsync();

        return await BeginRegistration(user.DisplayName, forAuthUserId: userId, requiresSessionAtComplete: true,
            excludeCredentials: exclude.Select(id => new PublicKeyCredentialDescriptor(id)).ToList());
    }

    /// <summary>
    /// Additional passkey for one's OWN account, initiated from a NEW, not-yet-logged-in
    /// device via a link code (see LinkBegin) - an alternative to the browser-dependent
    /// WebAuthn cross-device/hybrid transport (QR code+Bluetooth), which depending on
    /// browser/OS is not discoverable at all, or only behind a hidden "more options" link.
    /// Deliberately WITHOUT a session requirement (the calling device has none) - the
    /// authorization lives in the code itself, which only an already logged-in device could
    /// generate. Registers its own, LOCAL passkey on this device (no cross-device ceremony);
    /// like begin-additional it ends up PENDING and still requires an explicit approval.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("register/begin-linked")]
    public async Task<ActionResult<PasskeyBeginResponseDto>> RegisterBeginLinked([FromBody] DeviceLinkRedeemRequestDto request)
    {
        var code = NormalizeLinkCode(request.Code);
        var link = code.Length == 0 ? null : await CacheGetAsync<PendingDeviceLink>(LinkCodeCacheKey(code));
        if (link is null)
            return BadRequest("Link code invalid or expired.");

        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == link.AuthUserId);
        if (user is null) return BadRequest("Link code invalid or expired.");

        var exclude = await _db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.AuthUserId == link.AuthUserId)
            .Select(c => c.CredentialId)
            .ToListAsync();

        return await BeginRegistration(user.DisplayName, forAuthUserId: link.AuthUserId, requiresSessionAtComplete: false,
            excludeCredentials: exclude.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(), linkCode: code);
    }

    private async Task<ActionResult<PasskeyBeginResponseDto>> BeginRegistration(
        string displayName, int? forAuthUserId, bool requiresSessionAtComplete,
        IReadOnlyList<PublicKeyCredentialDescriptor> excludeCredentials, string? linkCode = null, string? inviteToken = null)
    {
        // The user handle is an opaque random handle ONLY for the browser's WebAuthn dialog.
        // Account resolution at login relies exclusively on the (unique) CredentialId,
        // never on this handle - which is why it doesn't need to be persisted and may
        // differ per registration (see IsUserHandleOwnerOfCredentialIdCallback in login).
        var user = new Fido2User
        {
            Id = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            Name = displayName,
            DisplayName = displayName,
        };

        var options = CreateFido2().RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = excludeCredentials,
            // ResidentKey Required = a real passkey discoverable in the authenticator store
            // (Face ID/Touch ID model); the browser itself picks the right credential at
            // login, without the user having to type a name.
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            // No manufacturer attestation needed: we only want the public key, not
            // device certificate chains (which Apple/Google mostly anonymize anyway).
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var optionsId = Guid.NewGuid().ToString("N");
        await CacheSetAsync(RegistrationCacheKey(optionsId),
            new PendingRegistration(options, displayName, forAuthUserId, requiresSessionAtComplete, linkCode, inviteToken), ChallengeLifetime);

        return new PasskeyBeginResponseDto { OptionsId = optionsId, OptionsJson = options.ToJson() };
    }

    /// <summary>Maps a RegistrationGateDecision to the stable Reason string the client switches on
    /// (RegistrationGateErrorDto) - never called with Allowed (the caller only invokes this for a
    /// rejection).</summary>
    private static string ReasonFor(RegistrationGateDecision decision) => decision switch
    {
        RegistrationGateDecision.Closed => "closed",
        RegistrationGateDecision.InviteRequired => "invite_required",
        RegistrationGateDecision.InviteInvalid => "invite_invalid",
        _ => "closed",
    };

    [AllowAnonymous]
    [HttpPost("register/complete")]
    public async Task<ActionResult<PasskeyCompleteResponseDto>> RegisterComplete([FromBody] PasskeyRegisterCompleteRequest request)
    {
        if (request.Response is null)
            return BadRequest("Registration challenge unknown or expired.");
        var pending = await CacheGetAsync<PendingRegistration>(RegistrationCacheKey(request.OptionsId));
        if (pending is null)
            return BadRequest("Registration challenge unknown or expired.");
        await _cache.RemoveAsync(RegistrationCacheKey(request.OptionsId));

        // Additional-passkey path via begin-additional: the session must still be valid at
        // the time of Complete AND belong to the same user for whom Begin issued the challenge.
        // The begin-linked path (RequiresSessionAtComplete=false) has no session at all -
        // there the link code already verified the authorization in Begin. Conditional, so this
        // stays a manual check instead of an [Authorize(SessionOnly)] attribute (the action must
        // remain reachable WITHOUT a session on the other two registration paths).
        if (pending.ForAuthUserId is int forUserId && pending.RequiresSessionAtComplete && HttpContext.SessionAuthUserId() != forUserId)
            return Unauthorized();

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await CreateFido2().MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = request.Response,
                OriginalOptions = pending.Options,
                IsCredentialIdUniqueToUserCallback = async (p, ct) =>
                    !await _db.PasskeyCredentials.AnyAsync(c => c.CredentialId == p.CredentialId, ct),
            });
        }
        catch (Fido2VerificationException)
        {
            return BadRequest("Passkey attestation could not be verified.");
        }

        var now = DateTime.UtcNow;

        // User decision wrapped in a transaction, so that two concurrent first registrations
        // don't both claim the same legacy user or create users on top of each other.
        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Shared by the legacy-user-claim decision below AND the invite-consumption check further
        // down (audit A10) - hoisted out of the `else` branch so both can read it. Same predicate
        // RegisterBegin used to decide whether its RegistrationGateService check even ran.
        var anyPasskeyExists = await _db.PasskeyCredentials.AnyAsync();

        AuthUserEntity targetUser;
        if (pending.ForAuthUserId is int ownUserId)
        {
            targetUser = (await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == ownUserId))!;
        }
        else
        {
            // Core of the first-registration logic: if NOT a single passkey exists yet, meaning
            // nobody has ever registered, then this first registration "claims" the legacy user
            // created by the phase-1 migration ("My Studies") along with all of its existing
            // data - only its DisplayName is updated to the input. Once at least one passkey
            // exists, the legacy user is taken and every further registration creates a
            // brand-new, empty user (open signup, intentional).
            var legacyUser = anyPasskeyExists
                ? null
                : await _db.AuthUsers.OrderBy(u => u.Id).FirstOrDefaultAsync();

            if (legacyUser is not null)
            {
                // Claiming the legacy user (created by the phase-1 migration, or - on a normal,
                // never-emptied instance - the true first-ever user): IsOwner already sits on
                // this row from the AddAuthUserIsOwner backfill (lowest Id) or an earlier first
                // registration, so nothing to set here.
                legacyUser.DisplayName = pending.DisplayName;
                targetUser = legacyUser;
            }
            else
            {
                // Reached either for open family signup (anyPasskeyExists == true, a legacy/
                // owner user already exists - IsOwner stays false, the entity default) or for the
                // genuine "zero AuthUsers exist at all" edge case (anyPasskeyExists == false AND
                // the query above still found nothing - e.g. a restored empty/wiped DB): only the
                // latter is the true first-ever user and becomes owner (audit A15/A2 fix - this
                // used to be re-derived implicitly as "lowest Id" at every check instead of
                // decided once, here, at creation time).
                targetUser = new AuthUserEntity { DisplayName = pending.DisplayName, CreatedAt = now, IsOwner = !anyPasskeyExists };
                _db.AuthUsers.Add(targetUser);
                await _db.SaveChangesAsync(); // Id is needed for the credential row
            }
        }

        // Registration gate (audit A10): consume the invite that gated this self-registration at
        // Begin - deliberately HERE, inside the same transaction as the user/credential creation,
        // and only NOW (not at Begin) so a failed/abandoned attestation attempt never burns it.
        // Guarded by the CURRENT mode (not just "a token happens to be present") so a stray/garbage
        // InviteToken from an "open"-mode client (e.g. a leftover ?invite= query param from a
        // previous invite-mode deployment) can never accidentally fail an otherwise-legitimate
        // open registration. Only for self-registration past bootstrap - RegisterBegin only ever
        // required and validated a token in exactly that case (see its own comment); the
        // additional-passkey/link paths (pending.ForAuthUserId != null) never carry one at all.
        // The single "UPDATE ... WHERE UsedAt IS NULL" this compiles to (see
        // RegistrationGateService.TryConsumeInviteAsync) is what makes a concurrent double-complete
        // race using the same token resolve cleanly: the loser's affected-rows is 0, so it rolls
        // back and returns a clean 403 instead of also creating a second user.
        if (pending.ForAuthUserId is null && anyPasskeyExists
            && RegistrationGateService.GetMode(_config) == RegistrationMode.Invite
            && pending.InviteToken is { Length: > 0 } inviteToken)
        {
            if (!await _registrationGate.TryConsumeInviteAsync(inviteToken, targetUser.Id, now))
            {
                await transaction.RollbackAsync();
                return StatusCode(StatusCodes.Status403Forbidden, new RegistrationGateErrorDto { Reason = "invite_invalid" });
            }
        }

        // Open first registration (claiming the legacy user or a new user): there is no
        // "other" device that could approve it, so it's approved immediately - the user has
        // just proven possession of their authenticator, the same proof as at login. An
        // additional passkey for one's own account, however: PENDING until an already
        // logged-in device consents via the device list (see LoginComplete/ApproveCredential) -
        // otherwise a stolen/replayed session token alone would be enough to permanently plant
        // one's own, independent access method.
        var isAdditionalDevice = pending.ForAuthUserId is not null;
        _db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            AuthUserId = targetUser.Id,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            CreatedAt = now,
            ApprovedAt = isAdditionalDevice ? null : now,
        });

        // Issue a session only directly at the open first registration (instead of forcing a
        // second passkey dialog right after) - on the additional-passkey path it's neither
        // necessary (the device is already logged in) nor permissible (the new passkey isn't
        // approved yet).
        var token = isAdditionalDevice ? null : AuthSessionService.IssueSession(_db, targetUser.Id, now);

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        // The link code is single-use: consumed after the first SUCCESSFUL device, so it
        // cannot be redeemed multiple times. Failed/aborted attempts BEFORE this point
        // deliberately leave it untouched (retry from device A possible without a new code).
        if (pending.LinkCode is { } usedCode) await _cache.RemoveAsync(LinkCodeCacheKey(usedCode));

        return new PasskeyCompleteResponseDto { Token = token, DisplayName = targetUser.DisplayName, Pending = isAdditionalDevice };
    }

    private static string RegistrationCacheKey(string optionsId) => $"passkey-reg:{optionsId}";
}
