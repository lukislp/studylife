// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services.SignIn;

/// <summary>Why a password operation was refused. The controller maps each to a stable RFC 9457
/// problem type the client switches on.</summary>
public enum PasswordProblem
{
    None,
    /// <summary>The instance switch is off.</summary>
    Disabled,
    UsernameInvalid,
    UsernameTaken,
    /// <summary>The account already has a password credential, so its login name is fixed; the
    /// request asked for a different one.</summary>
    UsernameFixed,
    TooShort,
    TooLong,
    Common,
    ContainsUsername,
    /// <summary>The password is the account's last usable sign-in method.</summary>
    LastMethod,
    /// <summary>The account has no password to change or remove.</summary>
    NotFound,
}

/// <summary>A validated and already hashed new credential, ready to be stored. Hashing happens
/// before any transaction is opened, so the expensive KDF never runs while a write lock is held.</summary>
public sealed record PreparedPassword(string Username, string NormalizedUsername, string PasswordHash);

/// <summary>Outcome of a sign-in attempt. Deliberately only "who" or "nobody": unknown name,
/// wrong password and a locked account are indistinguishable to the caller.</summary>
public readonly record struct PasswordSignInResult(int? UserId, bool ChangeRequired)
{
    public bool Succeeded => UserId is not null;

    public static PasswordSignInResult Failed => new(null, false);
}

/// <summary>
/// Use cases of the username + password sign-in method (docs/ARCHITECTURE.md "Password sign-in").
/// The rules live in the pure policies next to it (UsernamePolicy, PasswordPolicy,
/// PasswordLockoutPolicy, SignInMethodRules), hashing behind <see cref="IPasswordHashing"/>. This
/// service only loads, applies and persists. It never issues a session - the controller does that
/// through AuthSessionService.IssueSession, the same single place every other sign-in uses - and it
/// never decides whether the caller proved their presence (the controller's step-up check).
/// </summary>
public interface IPasswordCredentialService
{
    /// <summary>The instance switch <c>PasswordSignIn:Enabled</c>.</summary>
    bool Enabled { get; }

    Task<PasswordStatusDto> GetStatusAsync(int userId, CancellationToken ct = default);

    /// <summary>Checks a username and password. Applies and records the per-account lockout; on
    /// success resets it, stamps LastUsedAt and upgrades an outdated hash.</summary>
    Task<PasswordSignInResult> SignInAsync(string? username, string? password, CancellationToken ct = default);

    /// <summary>Checks the CURRENT password of an account as proof of presence for a sensitive
    /// action. Same lockout as sign-in, so a stolen session cannot guess the password either.</summary>
    Task<bool> VerifyForUserAsync(int userId, string? password, CancellationToken ct = default);

    /// <summary>Validates and hashes a new credential. <paramref name="forUserId"/> is the account
    /// it is for (null for a brand-new account). Once the account has a credential its login name is
    /// fixed: a blank or equal <paramref name="username"/> keeps it, a different one is refused with
    /// <see cref="PasswordProblem.UsernameFixed"/>.</summary>
    Task<(PasswordProblem Problem, PreparedPassword? Prepared)> PrepareAsync(
        int? forUserId, string? username, string? password, CancellationToken ct = default);

    /// <summary>Adds the credential of a brand-new account to the context (registration); the
    /// caller saves inside its own transaction.</summary>
    void AddForNewAccount(int userId, PreparedPassword prepared, DateTime now);

    /// <summary>Adds or replaces the account's password. Replacing one revokes every other session
    /// of the account (the one in <paramref name="keepSessionId"/> survives; null revokes all).</summary>
    Task<PasswordProblem> SetAsync(int userId, PreparedPassword prepared, int? keepSessionId,
        bool changeRequired = false, CancellationToken ct = default);

    /// <summary>Whether the password may be removed - checked before the caller spends a one-time
    /// proof on a request that would be refused anyway.</summary>
    Task<PasswordProblem> CheckRemovableAsync(int userId, CancellationToken ct = default);

    /// <summary>Removes the password and revokes every other session of the account.</summary>
    Task<PasswordProblem> RemoveAsync(int userId, int currentSessionId, CancellationToken ct = default);

    /// <summary>A random temporary password for the owner reset (about 99 bits).</summary>
    string GenerateTemporaryPassword();
}

public sealed class PasswordCredentialService(
    StudyLifeDb db,
    IPasswordHashing hashing,
    IOptionsMonitor<PasswordSignInOptions> options,
    ISignInMethodCatalog methods,
    IAuthAccountService accounts,
    TimeProvider? timeProvider = null,
    ILogger<PasswordCredentialService>? logger = null) : IPasswordCredentialService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public bool Enabled => options.CurrentValue.Enabled;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public async Task<PasswordStatusDto> GetStatusAsync(int userId, CancellationToken ct = default)
    {
        var credential = await db.PasswordCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.AuthUserId == userId, ct);
        var hasPasskey = await db.PasskeyCredentials.AnyAsync(c => c.AuthUserId == userId && c.ApprovedAt != null, ct);
        return new PasswordStatusDto
        {
            Enabled = Enabled,
            HasPassword = credential is not null,
            Username = credential?.Username,
            UpdatedAt = credential?.UpdatedAt,
            ChangeRequired = credential?.ChangeRequired ?? false,
            CanRemove = credential is not null && await CheckRemovableAsync(userId, ct) == PasswordProblem.None,
            HasPasskey = hasPasskey,
        };
    }

    public async Task<PasswordSignInResult> SignInAsync(string? username, string? password, CancellationToken ct = default)
    {
        var normalizedPassword = PasswordPolicy.Normalize(password);
        var name = UsernamePolicy.Normalize(username);
        if (normalizedPassword is null || PasswordPolicy.Length(normalizedPassword) > PasswordPolicy.MaxLength
            || !UsernamePolicy.IsValid(name))
        {
            hashing.VerifyDummy(normalizedPassword ?? "");
            return PasswordSignInResult.Failed;
        }

        var key = UsernamePolicy.Key(name);
        var credential = await db.PasswordCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.NormalizedUsername == key, ct);
        if (credential is null)
        {
            hashing.VerifyDummy(normalizedPassword);
            return PasswordSignInResult.Failed;
        }

        return await CheckAsync(credential, normalizedPassword, ct)
            ? new PasswordSignInResult(credential.AuthUserId, credential.ChangeRequired)
            : PasswordSignInResult.Failed;
    }

    public async Task<bool> VerifyForUserAsync(int userId, string? password, CancellationToken ct = default)
    {
        var normalizedPassword = PasswordPolicy.Normalize(password);
        var credential = await db.PasswordCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.AuthUserId == userId, ct);
        if (credential is null || normalizedPassword is null || PasswordPolicy.Length(normalizedPassword) > PasswordPolicy.MaxLength)
        {
            hashing.VerifyDummy(normalizedPassword ?? "");
            return false;
        }
        return await CheckAsync(credential, normalizedPassword, ct);
    }

    /// <summary>The verification both sign-in and step-up share: lockout first (a locked account
    /// still pays for one dummy verification, so timing stays flat), then the hash, then the
    /// bookkeeping. The row is read untracked and every write is a single UPDATE, so the counter
    /// and the lockout are never judged from a stale tracked copy and parallel requests (also on
    /// other web replicas) cannot overwrite each other's counts.</summary>
    private async Task<bool> CheckAsync(PasswordCredentialEntity credential, string normalizedPassword, CancellationToken ct)
    {
        var now = UtcNow;
        if (PasswordLockoutPolicy.IsLocked(credential.LockedUntil, now))
        {
            hashing.VerifyDummy(normalizedPassword);
            return false;
        }

        var check = hashing.Verify(credential.PasswordHash, normalizedPassword);
        if (check == PasswordCheck.Failed)
        {
            await RecordFailureAsync(credential.Id, now, ct);
            return false;
        }

        DateTime? usedAt = now;
        var hash = check == PasswordCheck.SuccessRehashNeeded ? hashing.Hash(normalizedPassword) : credential.PasswordHash;
        await db.PasswordCredentials.Where(c => c.Id == credential.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.FailedAttemptCount, 0)
                .SetProperty(c => c.LockedUntil, (DateTime?)null)
                .SetProperty(c => c.LastUsedAt, usedAt)
                .SetProperty(c => c.PasswordHash, hash), ct);
        return true;
    }

    /// <summary>Counts a wrong password atomically (two parallel guesses must both count, also
    /// across web replicas) and starts a lockout once the policy says so.</summary>
    private async Task RecordFailureAsync(int credentialId, DateTime now, CancellationToken ct)
    {
        await db.PasswordCredentials.Where(c => c.Id == credentialId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FailedAttemptCount, c => c.FailedAttemptCount + 1), ct);
        var failures = await db.PasswordCredentials.AsNoTracking().Where(c => c.Id == credentialId)
            .Select(c => c.FailedAttemptCount).FirstOrDefaultAsync(ct);
        if (PasswordLockoutPolicy.LockoutAfter(failures) is { } lockout)
        {
            DateTime? until = now + lockout;
            await db.PasswordCredentials.Where(c => c.Id == credentialId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LockedUntil, until), ct);
            // Ids and numbers only - never the name that was tried or anything typed.
            _logger.LogWarning("Password sign-in of credential {CredentialId} locked for {Seconds} s after {Failures} wrong passwords in a row",
                credentialId, (int)lockout.TotalSeconds, failures);
        }
    }

    public async Task<(PasswordProblem Problem, PreparedPassword? Prepared)> PrepareAsync(
        int? forUserId, string? username, string? password, CancellationToken ct = default)
    {
        if (!Enabled) return (PasswordProblem.Disabled, null);

        var existing = forUserId is int userId
            ? await db.PasswordCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.AuthUserId == userId, ct)
            : null;
        var requested = UsernamePolicy.Normalize(username);
        if (existing is not null && UsernamePolicy.ChangesFixedName(existing.NormalizedUsername, requested))
            return (PasswordProblem.UsernameFixed, null);
        var name = existing is not null ? existing.Username : requested;
        if (!UsernamePolicy.IsValid(name)) return (PasswordProblem.UsernameInvalid, null);

        var key = UsernamePolicy.Key(name);
        if (await db.PasswordCredentials.AnyAsync(c => c.NormalizedUsername == key && c.AuthUserId != (forUserId ?? 0), ct))
            return (PasswordProblem.UsernameTaken, null);

        var normalizedPassword = PasswordPolicy.Normalize(password);
        var violation = PasswordPolicy.Check(normalizedPassword, name);
        if (violation != PasswordRuleViolation.Ok) return (ToProblem(violation), null);

        return (PasswordProblem.None, new PreparedPassword(name, key, hashing.Hash(normalizedPassword!)));
    }

    private static PasswordProblem ToProblem(PasswordRuleViolation violation) => violation switch
    {
        PasswordRuleViolation.TooShort => PasswordProblem.TooShort,
        PasswordRuleViolation.TooLong => PasswordProblem.TooLong,
        PasswordRuleViolation.Common => PasswordProblem.Common,
        PasswordRuleViolation.ContainsUsername => PasswordProblem.ContainsUsername,
        _ => PasswordProblem.None,
    };

    public void AddForNewAccount(int userId, PreparedPassword prepared, DateTime now) =>
        db.PasswordCredentials.Add(new PasswordCredentialEntity
        {
            AuthUserId = userId,
            Username = prepared.Username,
            NormalizedUsername = prepared.NormalizedUsername,
            PasswordHash = prepared.PasswordHash,
            CreatedAt = now,
            UpdatedAt = now,
        });

    public async Task<PasswordProblem> SetAsync(int userId, PreparedPassword prepared, int? keepSessionId,
        bool changeRequired = false, CancellationToken ct = default)
    {
        if (!Enabled) return PasswordProblem.Disabled;

        var now = UtcNow;
        var existing = await db.PasswordCredentials.FirstOrDefaultAsync(c => c.AuthUserId == userId, ct);
        var credential = existing ?? db.PasswordCredentials.Add(new PasswordCredentialEntity
        {
            AuthUserId = userId,
            CreatedAt = now,
        }).Entity;
        credential.Username = prepared.Username;
        credential.NormalizedUsername = prepared.NormalizedUsername;
        credential.PasswordHash = prepared.PasswordHash;
        credential.UpdatedAt = now;
        credential.FailedAttemptCount = 0;
        credential.LockedUntil = null;
        credential.ChangeRequired = changeRequired;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race for the same name against another account (unique index).
            db.ChangeTracker.Clear();
            return PasswordProblem.UsernameTaken;
        }

        if (changeRequired)
            _logger.LogInformation("Temporary password set for account {UserId} (owner reset); its sessions are revoked", userId);

        // A changed password usually means "the old one may be known to someone": every session
        // that might have been opened with it goes (2026-09 audit S7, same rule as removing a
        // passkey). Adding a first password changes nothing about existing sessions; an owner
        // reset (no session to keep) always revokes them all.
        if (existing is not null || keepSessionId is null)
        {
            if (keepSessionId is int keep) await accounts.RevokeOtherSessionsAsync(userId, keep);
            else await accounts.RevokeAllSessionsAsync(userId);
        }
        return PasswordProblem.None;
    }

    public async Task<PasswordProblem> CheckRemovableAsync(int userId, CancellationToken ct = default)
    {
        if (!await db.PasswordCredentials.AnyAsync(c => c.AuthUserId == userId, ct)) return PasswordProblem.NotFound;
        var usable = await methods.CountUsableAsync(userId, ct);
        return SignInMethodRules.MayRemove(removedIsUsable: Enabled, usable) ? PasswordProblem.None : PasswordProblem.LastMethod;
    }

    public async Task<PasswordProblem> RemoveAsync(int userId, int currentSessionId, CancellationToken ct = default)
    {
        var problem = await CheckRemovableAsync(userId, ct);
        if (problem != PasswordProblem.None) return problem;

        await db.PasswordCredentials.Where(c => c.AuthUserId == userId).ExecuteDeleteAsync(ct);
        await accounts.RevokeOtherSessionsAsync(userId, currentSessionId);
        return PasswordProblem.None;
    }

    // No 0/O/1/l/i: the owner may have to read the temporary password out or copy it by hand.
    private const string TemporaryAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public string GenerateTemporaryPassword()
    {
        var groups = new string[4];
        for (var g = 0; g < groups.Length; g++)
            groups[g] = string.Concat(Enumerable.Range(0, 5)
                .Select(_ => TemporaryAlphabet[RandomNumberGenerator.GetInt32(TemporaryAlphabet.Length)]));
        return string.Join('-', groups);
    }
}
