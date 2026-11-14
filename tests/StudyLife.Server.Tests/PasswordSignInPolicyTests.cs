// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using StudyLife.Server.Services.SignIn;

namespace StudyLife.Server.Tests;

/// <summary>The pure rules of password sign-in (Services/SignIn): no host, no database.</summary>
public class UsernamePolicyTests
{
    [Theory]
    [InlineData("  alex  ", "alex")]
    [InlineData("Alex.Example", "Alex.Example")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Normalize_Trims(string? input, string expected) =>
        Assert.Equal(expected, UsernamePolicy.Normalize(input));

    [Fact]
    public void Normalize_BringsDecomposedTextIntoNfc()
    {
        // "é" as e + U+0301 and as the single code point U+00E9 is the same name.
        Assert.Equal("jérôme", UsernamePolicy.Normalize("jérôme"));
    }

    [Fact]
    public void Normalize_InvalidUnicode_YieldsEmpty() =>
        Assert.Equal("", UsernamePolicy.Normalize("ab\ud800cd"));

    [Theory]
    [InlineData("alex")]
    [InlineData("al3")]
    [InlineData("alex.example@mail.test")]
    [InlineData("anna_maria-2")]
    [InlineData("jürgen")]
    [InlineData("Ελένη")]
    [InlineData("𝒜lex")] // a letter outside the BMP (surrogate pair)
    [InlineData("1st-user")]
    public void IsValid_AcceptsLettersDigitsAndSafeSymbols(string name) =>
        Assert.True(UsernamePolicy.IsValid(UsernamePolicy.Normalize(name)));

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("a b c")]
    [InlineData(".alex")]
    [InlineData("_alex")]
    [InlineData("alex!")]
    [InlineData("alex/../root")]
    [InlineData("al\u200bex")] // zero-width space
    [InlineData("al\tex")]
    public void IsValid_RejectsEverythingElse(string name) =>
        Assert.False(UsernamePolicy.IsValid(UsernamePolicy.Normalize(name)));

    [Fact]
    public void IsValid_EnforcesTheLengthLimits()
    {
        Assert.True(UsernamePolicy.IsValid(new string('a', UsernamePolicy.MaxLength)));
        Assert.False(UsernamePolicy.IsValid(new string('a', UsernamePolicy.MaxLength + 1)));
        Assert.True(UsernamePolicy.IsValid(new string('a', UsernamePolicy.MinLength)));
    }

    [Fact]
    public void Key_IsCaseInsensitive()
    {
        Assert.Equal(UsernamePolicy.Key("Alex.Example"), UsernamePolicy.Key("ALEX.example"));
        Assert.Equal(UsernamePolicy.Key("JÜRGEN"), UsernamePolicy.Key("jürgen"));
    }
}

public class PasswordPolicyTests
{
    private const string Strong = "correct pony staple 42";

    [Fact]
    public void Check_AcceptsALongPassphraseWithoutCompositionRules()
    {
        Assert.Equal(PasswordRuleViolation.Ok, PasswordPolicy.Check(Strong, "alex"));
        // No digit, no symbol, no capital - still fine (NIST 800-63B: length, not composition).
        Assert.Equal(PasswordRuleViolation.Ok, PasswordPolicy.Check("violets are blue", "alex"));
    }

    [Fact]
    public void Check_EnforcesTheMinimumLength()
    {
        Assert.Equal(PasswordRuleViolation.TooShort, PasswordPolicy.Check("Gx7#kq2!Lm9", null)); // 11
        Assert.Equal(PasswordRuleViolation.Ok, PasswordPolicy.Check("Gx7#kq2!Lm9z", null)); // 12
        Assert.Equal(PasswordRuleViolation.TooShort, PasswordPolicy.Check(null, null));
        Assert.Equal(PasswordRuleViolation.TooShort, PasswordPolicy.Check("", null));
    }

    [Fact]
    public void Check_CountsUnicodeScalarsNotUtf16Units()
    {
        // 11 letters + one emoji (two UTF-16 units) = 12 characters.
        Assert.Equal(PasswordRuleViolation.Ok, PasswordPolicy.Check("Gx7kq2Lm9zQ😀", null));
        // 10 letters + one emoji = 11 characters, although it is 12 UTF-16 units.
        Assert.Equal(PasswordRuleViolation.TooShort, PasswordPolicy.Check("Gx7kq2Lm9z😀", null));
    }

    [Fact]
    public void Check_EnforcesTheMaximumLength()
    {
        Assert.Equal(PasswordRuleViolation.TooLong, PasswordPolicy.Check(Strong + new string('x', PasswordPolicy.MaxLength), null));
    }

    [Theory]
    [InlineData("password1234")]
    [InlineData("PASSWORD1234")]
    [InlineData("qwertyuiop12")]
    [InlineData("123456789012")]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("abababababab")]
    [InlineData("abcdabcdabcd")]
    [InlineData("abcdefghijklm")]
    [InlineData("zyxwvutsrqpo")]
    [InlineData("studylife2026")]
    public void Check_RejectsCommonAndPredictablePasswords(string password) =>
        Assert.Equal(PasswordRuleViolation.Common, PasswordPolicy.Check(password, null));

    [Fact]
    public void Check_RejectsThePasswordContainingTheUsername()
    {
        Assert.Equal(PasswordRuleViolation.ContainsUsername, PasswordPolicy.Check("my name is Alexander!", "alexander"));
        // A three-letter name inside a passphrase is coincidence.
        Assert.Equal(PasswordRuleViolation.Ok, PasswordPolicy.Check("bobsled down the hill", "bob"));
    }

    [Fact]
    public void Normalize_UsesNfkc()
    {
        // Full-width letters (an East Asian keyboard) are the same password as plain ASCII.
        Assert.Equal("Passphrase", PasswordPolicy.Normalize("Ｐａｓｓｐｈｒａｓｅ"));
        Assert.Null(PasswordPolicy.Normalize(null));
        Assert.Null(PasswordPolicy.Normalize(""));
        Assert.Null(PasswordPolicy.Normalize("ab\udc00cd"));
    }
}

public class PasswordLockoutPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void TheFirstFiveFailuresAreFree(int failures) =>
        Assert.Null(PasswordLockoutPolicy.LockoutAfter(failures));

    [Theory]
    [InlineData(6, 30)]
    [InlineData(7, 60)]
    [InlineData(8, 120)]
    [InlineData(9, 240)]
    [InlineData(10, 480)]
    [InlineData(11, 900)]
    [InlineData(50, 900)]
    [InlineData(int.MaxValue, 900)]
    public void LockoutDoublesUpToFifteenMinutes(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PasswordLockoutPolicy.LockoutAfter(failures));

    [Fact]
    public void IsLocked_ComparesWithNow()
    {
        var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(PasswordLockoutPolicy.IsLocked(null, now));
        Assert.False(PasswordLockoutPolicy.IsLocked(now, now));
        Assert.True(PasswordLockoutPolicy.IsLocked(now.AddSeconds(1), now));
    }
}

public class SignInMethodRulesTests
{
    [Theory]
    [InlineData(true, 2, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, true)] // a pending passkey or a switched-off password may always go
    [InlineData(false, 0, true)]
    public void MayRemove_KeepsAtLeastOneUsableMethod(bool removedIsUsable, int usable, bool expected) =>
        Assert.Equal(expected, SignInMethodRules.MayRemove(removedIsUsable, usable));

    [Fact]
    public async Task Catalog_SumsAllMethods()
    {
        var catalog = new SignInMethodCatalog([new FixedMethod(SignInMethodKind.Passkey, 2), new FixedMethod(SignInMethodKind.Password, 1)]);
        Assert.Equal(3, await catalog.CountUsableAsync(7));
    }

    private sealed class FixedMethod(SignInMethodKind kind, int count) : ISignInMethod
    {
        public SignInMethodKind Kind => kind;
        public Task<int> CountUsableAsync(int userId, CancellationToken ct = default) => Task.FromResult(count);
    }
}

public class IdentityPasswordHashingTests
{
    private readonly IdentityPasswordHashing _hashing = new();

    [Fact]
    public void Hash_IsIdentityV3_WithPbkdf2Sha512AndTheConfiguredIterations()
    {
        var bytes = Convert.FromBase64String(_hashing.Hash("correct pony staple 42"));
        Assert.Equal(0x01, bytes[0]); // format marker v3
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1))); // KeyDerivationPrf.HMACSHA512
        Assert.Equal((uint)IdentityPasswordHashing.Iterations, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)));
        Assert.True(IdentityPasswordHashing.Iterations >= 100_000);
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9))); // 128-bit salt
    }

    [Fact]
    public void Hash_IsSaltedAndVerifies()
    {
        var first = _hashing.Hash("correct pony staple 42");
        var second = _hashing.Hash("correct pony staple 42");
        Assert.NotEqual(first, second);
        Assert.Equal(PasswordCheck.Success, _hashing.Verify(first, "correct pony staple 42"));
        Assert.Equal(PasswordCheck.Failed, _hashing.Verify(first, "correct pony staple 43"));
        Assert.Equal(PasswordCheck.Failed, _hashing.Verify("not a hash", "correct pony staple 42"));
    }

    [Fact]
    public void Verify_ReportsAnOlderHashForUpgrade()
    {
        var older = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }))
            .HashPassword(new object(), "correct pony staple 42");
        Assert.Equal(PasswordCheck.SuccessRehashNeeded, _hashing.Verify(older, "correct pony staple 42"));
    }

    [Fact]
    public void VerifyDummy_RunsWithoutThrowing() => _hashing.VerifyDummy("anything at all");
}
