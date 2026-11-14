// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services.SignIn;

/// <summary>The ways an account can sign in. Recovery codes are deliberately not a method here:
/// they are the emergency way back in, not a door an account may rely on as its only one.</summary>
public enum SignInMethodKind
{
    Passkey,
    Password,
}

/// <summary>
/// One sign-in method, as far as account management needs to know it (strategy port): how many
/// usable credentials of this kind an account holds right now. "Usable" means it can sign in
/// today - an approved passkey, or a password while the instance switch allows passwords. A further
/// method later is one more implementation; the removal rule in <see cref="SignInMethodRules"/>
/// covers it without change.
/// </summary>
public interface ISignInMethod
{
    SignInMethodKind Kind { get; }

    Task<int> CountUsableAsync(int userId, CancellationToken ct = default);
}

/// <summary>Pure rule shared by every "remove a credential" path.</summary>
public static class SignInMethodRules
{
    /// <summary>
    /// May a credential be removed? Only if the account keeps at least one usable credential of
    /// ANY method afterwards - otherwise the user locks themselves out as soon as their sessions
    /// expire. A credential that is not usable itself (a pending passkey, a password while the
    /// switch is off) never counts and may always go.
    /// </summary>
    public static bool MayRemove(bool removedIsUsable, int usableAcrossAllMethods) =>
        !removedIsUsable || usableAcrossAllMethods > 1;
}

/// <summary>Sums <see cref="ISignInMethod"/> over every registered method.</summary>
public interface ISignInMethodCatalog
{
    Task<int> CountUsableAsync(int userId, CancellationToken ct = default);
}

public sealed class SignInMethodCatalog(IEnumerable<ISignInMethod> methods) : ISignInMethodCatalog
{
    public async Task<int> CountUsableAsync(int userId, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var method in methods) total += await method.CountUsableAsync(userId, ct);
        return total;
    }
}

/// <summary>Approved passkeys (a pending one cannot sign in yet, see AuthController.LoginComplete).</summary>
public sealed class PasskeySignInMethod(StudyLifeDb db) : ISignInMethod
{
    public SignInMethodKind Kind => SignInMethodKind.Passkey;

    public Task<int> CountUsableAsync(int userId, CancellationToken ct = default) =>
        db.PasskeyCredentials.CountAsync(c => c.AuthUserId == userId && c.ApprovedAt != null, ct);
}

/// <summary>The account's password - counted only while the instance switch is on.</summary>
public sealed class PasswordSignInMethod(StudyLifeDb db, IOptionsMonitor<PasswordSignInOptions> options) : ISignInMethod
{
    public SignInMethodKind Kind => SignInMethodKind.Password;

    public async Task<int> CountUsableAsync(int userId, CancellationToken ct = default) =>
        options.CurrentValue.Enabled && await db.PasswordCredentials.AnyAsync(c => c.AuthUserId == userId, ct) ? 1 : 0;
}

/// <summary>
/// "Has this installation been claimed yet?" - the bootstrap predicate that the setup code, the
/// first registration's legacy-user claim and the registration gate all key on. It used to be
/// "any passkey exists"; with password accounts an installation can be claimed without a single
/// passkey, and keeping the old predicate would let the next passkey registration claim the
/// password owner's account with the setup code. One definition, used everywhere.
/// </summary>
public static class InstanceBootstrap
{
    public static async Task<bool> IsClaimedAsync(StudyLifeDb db, CancellationToken ct = default) =>
        await db.PasskeyCredentials.AnyAsync(ct) || await db.PasswordCredentials.AnyAsync(ct);
}
