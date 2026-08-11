using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Pure unit tests for AiProxyTokenService.Mint (no host needed - static, stateless).
/// </summary>
public class AiProxyTokenServiceTests
{
    [Fact]
    public void Mint_MatchesIndependentlyVerifiedPythonCompatibleOutput()
    {
        // Golden value cross-checked against an independent Python implementation
        // (hmac + hashlib + base64) using the same secret/userId/expiry, before
        // anything was built on top of this format - see studylife-ai's
        // docs/decisions.md "M4.5 Multi-user support", "Auth flow, take two".
        // Locks in cross-language compatibility as a permanent regression test,
        // not just the one-off verification script that first confirmed it.
        var utcNow = DateTimeOffset.FromUnixTimeSeconds(1755000000 - 300).UtcDateTime;

        var token = AiProxyTokenService.Mint(42, "test-shared-secret-123", utcNow);

        Assert.Equal("42.1755000000.vKxqViv6oZzF3GsDvmkE0sNLZ3tM9TV7_6Sm6Yik5v4", token);
    }

    [Fact]
    public void Mint_ProducesThreeDotSeparatedParts()
    {
        var token = AiProxyTokenService.Mint(1, "secret", DateTime.UtcNow);

        Assert.Equal(3, token.Split('.').Length);
    }

    [Fact]
    public void Mint_EncodesTheExpiryAsLifetimeSecondsFromNow()
    {
        var utcNow = DateTime.UtcNow;

        var token = AiProxyTokenService.Mint(1, "secret", utcNow);

        var expiry = long.Parse(token.Split('.')[1]);
        var expected = new DateTimeOffset(utcNow + AiProxyTokenService.Lifetime).ToUnixTimeSeconds();
        Assert.Equal(expected, expiry);
    }

    [Fact]
    public void Mint_DifferentUserIds_ProduceDifferentTokens()
    {
        var utcNow = DateTime.UtcNow;

        var tokenA = AiProxyTokenService.Mint(1, "secret", utcNow);
        var tokenB = AiProxyTokenService.Mint(2, "secret", utcNow);

        Assert.NotEqual(tokenA, tokenB);
    }

    [Fact]
    public void Mint_DifferentSecrets_ProduceDifferentSignatures()
    {
        var utcNow = DateTime.UtcNow;

        var tokenA = AiProxyTokenService.Mint(1, "secret-a", utcNow);
        var tokenB = AiProxyTokenService.Mint(1, "secret-b", utcNow);

        Assert.NotEqual(tokenA.Split('.')[2], tokenB.Split('.')[2]);
    }
}
