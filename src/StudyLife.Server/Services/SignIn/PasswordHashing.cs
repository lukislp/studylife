// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace StudyLife.Server.Services.SignIn;

/// <summary>Result of checking a password against a stored hash.</summary>
public enum PasswordCheck
{
    Failed,
    Success,
    /// <summary>Correct, but hashed with older parameters - store a fresh hash.</summary>
    SuccessRehashNeeded,
}

/// <summary>
/// Port for password hashing. The application code never touches a hash format or a KDF itself;
/// the only implementation is <see cref="IdentityPasswordHashing"/>.
/// </summary>
public interface IPasswordHashing
{
    string Hash(string password);

    PasswordCheck Verify(string storedHash, string password);

    /// <summary>Runs one full verification against a fixed dummy hash and discards the result, so a
    /// sign-in for an unknown or locked account costs the same time as one for a real account and
    /// response timing does not reveal which names exist.</summary>
    void VerifyDummy(string password);
}

/// <summary>
/// Adapter over ASP.NET Core Identity's <see cref="PasswordHasher{TUser}"/> - part of the shared
/// framework, no extra package, no custom crypto. Format v3: PBKDF2 with HMAC-SHA512, a 128-bit
/// random salt per password, 256-bit subkey, iteration count stored inside the hash; the
/// comparison is constant-time (CryptographicOperations.FixedTimeEquals inside the hasher).
///
/// <see cref="Iterations"/> follows the OWASP Password Storage Cheat Sheet for PBKDF2-HMAC-SHA512
/// (210,000). Raising it later is safe: an older hash still verifies and reports
/// <see cref="PasswordCheck.SuccessRehashNeeded"/>, and the sign-in then stores a fresh one.
/// </summary>
public sealed class IdentityPasswordHashing : IPasswordHashing
{
    public const int Iterations = 210_000;

    // The hasher never reads the user object; a marker type keeps the generic argument honest.
    private sealed class Subject;

    private static readonly Subject Anyone = new();

    private readonly PasswordHasher<Subject> _hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = Iterations,
    }));

    private readonly Lazy<string> _dummyHash;

    public IdentityPasswordHashing()
    {
        _dummyHash = new Lazy<string>(() => Hash(Guid.NewGuid().ToString("N")));
    }

    public string Hash(string password) => _hasher.HashPassword(Anyone, password);

    public PasswordCheck Verify(string storedHash, string password) =>
        _hasher.VerifyHashedPassword(Anyone, storedHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };

    public void VerifyDummy(string password) => _ = Verify(_dummyHash.Value, password);
}
