using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Pure unit tests for AiProxyTokenService.Mint (no host needed - static, stateless).
/// </summary>
public class AiProxyTokenServiceTests
{
    [Fact]
    public void MintLegacy_MatchesIndependentlyVerifiedPythonCompatibleOutput()
    {
        // Golden value cross-checked against an independent Python implementation
        // (hmac + hashlib + base64) using the same secret/userId/expiry, before
        // anything was built on top of this format - see studylife-ai's
        // docs/decisions.md "M4.5 Multi-user support", "Auth flow, take two".
        // Locks in cross-language compatibility as a permanent regression test,
        // not just the one-off verification script that first confirmed it. Still the
        // legacy (unkeyed) 3-part format - the wire format kept for the audit A5 rollout
        // fallback (StudyLifeAi:SharedSecret) unchanged.
        var utcNow = DateTimeOffset.FromUnixTimeSeconds(1755000000 - 300).UtcDateTime;

        var token = AiProxyTokenService.MintLegacy(42, "test-shared-secret-123", utcNow);

        Assert.Equal("42.1755000000.vKxqViv6oZzF3GsDvmkE0sNLZ3tM9TV7_6Sm6Yik5v4", token);
    }

    [Fact]
    public void MintLegacy_ProducesThreeDotSeparatedParts()
    {
        var token = AiProxyTokenService.MintLegacy(1, "secret", DateTime.UtcNow);

        Assert.Equal(3, token.Split('.').Length);
    }

    [Fact]
    public void MintLegacy_EncodesTheExpiryAsLifetimeSecondsFromNow()
    {
        var utcNow = DateTime.UtcNow;

        var token = AiProxyTokenService.MintLegacy(1, "secret", utcNow);

        var expiry = long.Parse(token.Split('.')[1]);
        var expected = new DateTimeOffset(utcNow + AiProxyTokenService.Lifetime).ToUnixTimeSeconds();
        Assert.Equal(expected, expiry);
    }

    [Fact]
    public void MintLegacy_DifferentUserIds_ProduceDifferentTokens()
    {
        var utcNow = DateTime.UtcNow;

        var tokenA = AiProxyTokenService.MintLegacy(1, "secret", utcNow);
        var tokenB = AiProxyTokenService.MintLegacy(2, "secret", utcNow);

        Assert.NotEqual(tokenA, tokenB);
    }

    [Fact]
    public void MintLegacy_DifferentSecrets_ProduceDifferentSignatures()
    {
        var utcNow = DateTime.UtcNow;

        var tokenA = AiProxyTokenService.MintLegacy(1, "secret-a", utcNow);
        var tokenB = AiProxyTokenService.MintLegacy(1, "secret-b", utcNow);

        Assert.NotEqual(tokenA.Split('.')[2], tokenB.Split('.')[2]);
    }

    // --- New key-id-tagged format (audit A5) ---

    [Fact]
    public void ParseSigningKeys_ParsesMultipleCommaSeparatedEntries()
    {
        var keys = AiProxyTokenService.ParseSigningKeys("v1:secret-one,v2:secret-two");

        Assert.Equal(2, keys.Count);
        Assert.Equal(new AiProxyTokenService.SigningKey("v1", "secret-one"), keys[0]);
        Assert.Equal(new AiProxyTokenService.SigningKey("v2", "secret-two"), keys[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-colon-here")]
    [InlineData(":missing-kid")]
    [InlineData("missing-secret:")]
    public void ParseSigningKeys_ThrowsOnMalformedEntry(string config)
    {
        Assert.Throws<FormatException>(() => AiProxyTokenService.ParseSigningKeys(config));
    }

    [Fact]
    public void Mint_WithSigningKeys_ProducesFourDotSeparatedPartsWithTheFirstKid()
    {
        var keys = AiProxyTokenService.ParseSigningKeys("v1:secret-one,v2:secret-two");

        var token = AiProxyTokenService.Mint(42, keys, DateTime.UtcNow);
        var parts = token.Split('.');

        Assert.Equal(4, parts.Length);
        Assert.Equal("42", parts[0]);
        Assert.Equal("v1", parts[2]);
    }

    [Fact]
    public void Mint_AlwaysSignsWithTheFirstKeyRegardlessOfListOrder()
    {
        var utcNow = DateTime.UtcNow;
        var keysV1First = AiProxyTokenService.ParseSigningKeys("v1:secret-one,v2:secret-two");
        var keysV2First = AiProxyTokenService.ParseSigningKeys("v2:secret-two,v1:secret-one");

        var tokenA = AiProxyTokenService.Mint(1, keysV1First, utcNow);
        var tokenB = AiProxyTokenService.Mint(1, keysV2First, utcNow);

        Assert.Equal("v1", tokenA.Split('.')[2]);
        Assert.Equal("v2", tokenB.Split('.')[2]);
        Assert.NotEqual(tokenA, tokenB);
    }
}
