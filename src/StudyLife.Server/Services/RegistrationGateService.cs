using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services;

/// <summary>Registration:Mode values (env Registration__Mode) - audit finding A10.</summary>
public enum RegistrationMode
{
    /// <summary>Anyone may register (the previous, unconditional behavior).</summary>
    Open,
    /// <summary>register/begin requires a valid, unused, unexpired invite token; register/complete
    /// consumes it. The default when Registration:Mode is unset or holds an unrecognized value -
    /// deliberately the SAFER default (a fresh install that never configured this explicitly should
    /// not silently stay wide open), unlike the true legacy behavior which would be Open.</summary>
    Invite,
    /// <summary>Nobody may register - not even with a valid invite.</summary>
    Closed,
}

/// <summary>Outcome of a register/begin gate check (RegistrationGateService.CheckBeginAsync) -
/// the three rejection cases each map to a distinct, stable RegistrationGateErrorDto.Reason string
/// so the client can show the right message instead of a generic "didn't work".</summary>
public enum RegistrationGateDecision
{
    Allowed,
    Closed,
    InviteRequired,
    InviteInvalid,
}

/// <summary>
/// Single source of truth for "is a NEW self-registration (register/begin with ForAuthUserId ==
/// null) currently allowed" (audit finding A10). Deliberately separate from, and orthogonal to,
/// the pre-existing setup-secret check in AuthController.RegisterBegin: that check only ever
/// gates the very first passkey (bootstrap of a freshly migrated instance, still carrying the
/// phase-1 legacy AuthUser with zero passkeys) and is UNCHANGED by this feature - bootstrap must
/// never brick a fresh install, so this gate is skipped entirely while
/// !PasskeyCredentials.AnyAsync() (see AuthController.RegisterBegin/RegisterComplete, which both
/// compute that same flag for their own, pre-existing reasons). Once the instance is bootstrapped
/// (at least one passkey exists - "family signup" in the old model), THIS gate takes over instead.
/// </summary>
public interface IRegistrationGateService
{
    /// <summary>Checked at register/begin, before a WebAuthn challenge is even issued.</summary>
    Task<RegistrationGateDecision> CheckBeginAsync(string? inviteToken);

    /// <summary>
    /// Atomically marks the invite used (UsedAt/UsedByUserId) IF it is still valid at this exact
    /// moment - called at register/complete, never at begin, so a failed/abandoned attestation
    /// never burns the invite (see AuthController.RegisterComplete). The single
    /// "UPDATE ... WHERE TokenHash = @hash AND UsedAt IS NULL AND ExpiresAt > @now" this compiles
    /// to is what makes two concurrent register/complete calls racing on the same token resolve
    /// cleanly: only one UPDATE can ever match (the unique index on TokenHash plus the WHERE
    /// UsedAt IS NULL clause), the loser gets affected-rows == 0 and this returns false.
    /// A null/empty inviteToken also returns false - but the caller never passes one: the call
    /// site guards on pending.InviteToken being non-empty and only invokes this when
    /// RegisterBegin actually required and validated a token (see its own comment).
    /// </summary>
    Task<bool> TryConsumeInviteAsync(string? inviteToken, int consumedByUserId, DateTime now);
}

public class RegistrationGateService(StudyLifeDb db, IConfiguration config) : IRegistrationGateService
{
    /// <summary>Default invite lifetime (audit A10 design) - generous enough that "share a link in
    /// a family chat" doesn't race against a busy week, short enough that a leaked/forgotten link
    /// doesn't stay redeemable indefinitely.</summary>
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Reads Registration:Mode (env Registration__Mode) - "open"/"invite"/"closed",
    /// case-insensitive. Unset or unrecognized falls back to Invite (the safer default) rather
    /// than throwing or silently behaving like Open - a typo in the env var must not accidentally
    /// reopen an instance the operator meant to lock down.
    /// </summary>
    public static RegistrationMode GetMode(IConfiguration config) =>
        (config["Registration:Mode"] ?? "").Trim().ToLowerInvariant() switch
        {
            "open" => RegistrationMode.Open,
            "closed" => RegistrationMode.Closed,
            "invite" => RegistrationMode.Invite,
            _ => RegistrationMode.Invite,
        };

    public async Task<RegistrationGateDecision> CheckBeginAsync(string? inviteToken)
    {
        switch (GetMode(config))
        {
            case RegistrationMode.Open:
                return RegistrationGateDecision.Allowed;
            case RegistrationMode.Closed:
                return RegistrationGateDecision.Closed;
            default: // Invite
                if (string.IsNullOrWhiteSpace(inviteToken))
                    return RegistrationGateDecision.InviteRequired;

                var hash = HashInviteToken(inviteToken);
                var now = DateTime.UtcNow;
                var valid = await db.AuthInvites.AsNoTracking()
                    .AnyAsync(i => i.TokenHash == hash && i.UsedAt == null && i.ExpiresAt > now);
                return valid ? RegistrationGateDecision.Allowed : RegistrationGateDecision.InviteInvalid;
        }
    }

    public async Task<bool> TryConsumeInviteAsync(string? inviteToken, int consumedByUserId, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(inviteToken)) return false;

        var hash = HashInviteToken(inviteToken);
        var affected = await db.AuthInvites
            .Where(i => i.TokenHash == hash && i.UsedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.UsedAt, now)
                .SetProperty(i => i.UsedByUserId, consumedByUserId));
        return affected > 0;
    }

    /// <summary>Same SHA-256-lowercase-hex shape as every other credential hash in this schema
    /// (AuthSessionService.HashToken) - kept as its own method only so invite hashing doesn't read
    /// as coincidentally reusing session-token hashing; the algorithm is identical on purpose (one
    /// hashing convention for the whole app).</summary>
    private static string HashInviteToken(string token) => AuthSessionService.HashToken(token);
}
