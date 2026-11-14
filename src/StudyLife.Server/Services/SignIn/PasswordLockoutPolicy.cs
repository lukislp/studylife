// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Server.Services.SignIn;

/// <summary>
/// Per-account brute-force protection for password sign-in (the per-IP limit lives in the rate
/// limiter, Program.cs). The first <see cref="FreeAttempts"/> wrong passwords in a row cost
/// nothing; every further one locks the account's PASSWORD sign-in for an exponentially growing
/// time: 30 s, 1 min, 2 min, 4 min, 8 min, then <see cref="MaxLockout"/>. Passkeys and recovery
/// codes are not affected, so a stranger hammering a known username can delay the owner's
/// password sign-in by at most 15 minutes at a time but never lock the account itself. A
/// successful sign-in resets the counter.
/// </summary>
public static class PasswordLockoutPolicy
{
    public const int FreeAttempts = 5;
    public static readonly TimeSpan BaseLockout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);

    /// <summary>How long password sign-in is locked after the <paramref name="consecutiveFailures"/>-th
    /// wrong password in a row; null = not locked.</summary>
    public static TimeSpan? LockoutAfter(int consecutiveFailures)
    {
        if (consecutiveFailures <= FreeAttempts) return null;
        var exponent = Math.Min(consecutiveFailures - FreeAttempts - 1, 10);
        var lockout = TimeSpan.FromTicks(BaseLockout.Ticks << exponent);
        return lockout < MaxLockout ? lockout : MaxLockout;
    }

    public static bool IsLocked(DateTime? lockedUntil, DateTime now) => lockedUntil is { } until && until > now;
}
