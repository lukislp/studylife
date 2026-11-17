// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using StudyLife.Server.Data;

namespace StudyLife.Server.Services.Account;

/// <summary>
/// "Confirm it's you" for the actions that would let a stolen session token turn itself into
/// lasting access (2026-11-16 audit, finding M1): approving a passkey, generating recovery codes,
/// removing a passkey, signing out the other devices and the raw database download / restore.
///
/// The proof itself is the shared one of POST api/auth/account/reauth (AuthController.VerifyFreshProofAsync:
/// a passkey assertion, a recovery code, the current password, or a sign-in moments ago). A
/// successful proof marks THIS session as confirmed for <see cref="Window"/>; the guarded actions
/// ask <see cref="IsConfirmedAsync"/> before they run (RequireStepUpAttribute). The mark lives in
/// the distributed cache next to the WebAuthn challenges, so every web pod sees it, and it is
/// keyed by the session's token hash: it dies with the session, and a different session of the
/// same account never inherits it.
///
/// A session that signed in within <see cref="FreshSessionWindow"/> counts as confirmed without an
/// explicit proof - the same rule VerifyFreshProofAsync applies when no proof is sent. It is what
/// lets the native shells without in-app passkeys confirm by signing in again in the system browser.
/// </summary>
public interface ISessionStepUpService
{
    /// <summary>Records a successful proof for the session with this token hash.</summary>
    Task ConfirmAsync(string sessionTokenHash, CancellationToken ct = default);

    /// <summary>True when the session proved its user within <see cref="SessionStepUpService.Window"/>
    /// or signed in within <see cref="SessionStepUpService.FreshSessionWindow"/>.</summary>
    Task<bool> IsConfirmedAsync(int sessionId, string sessionTokenHash, CancellationToken ct = default);
}

public sealed class SessionStepUpService(IDistributedCache cache, StudyLifeDb db) : ISessionStepUpService
{
    /// <summary>How long one confirmation covers further guarded actions of the same session -
    /// long enough to approve a new passkey and sign out the old devices in one go, short enough
    /// that a token stolen later does not find a confirmed session lying around.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>How long after its own sign-in a session counts as confirmed without an explicit
    /// proof. Shared with VerifyFreshProofAsync (AuthController.FreshSessionWindow).</summary>
    public static readonly TimeSpan FreshSessionWindow = TimeSpan.FromMinutes(2);

    private static readonly byte[] Marker = [1];

    public Task ConfirmAsync(string sessionTokenHash, CancellationToken ct = default) =>
        cache.SetAsync(CacheKey(sessionTokenHash), Marker,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Window }, ct);

    public async Task<bool> IsConfirmedAsync(int sessionId, string sessionTokenHash, CancellationToken ct = default)
    {
        if (await cache.GetAsync(CacheKey(sessionTokenHash), ct) is not null) return true;

        var issuedAt = await db.AuthSessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => (DateTime?)s.IssuedAt).FirstOrDefaultAsync(ct);
        return issuedAt is { } at && DateTime.UtcNow - at <= FreshSessionWindow;
    }

    private static string CacheKey(string sessionTokenHash) => $"step-up:{sessionTokenHash}";
}
