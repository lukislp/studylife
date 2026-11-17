// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

public class ConsentPkceTests
{
    [Fact]
    public void Matches_Rfc7636AppendixBVector()
        => Assert.True(ConsentPkce.Matches(TestPkce.Challenge, TestPkce.Verifier));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXl")] // right shape, wrong value
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjX!")] // illegal character
    public void Matches_RejectsMissingMalformedOrWrongVerifiers(string? verifier)
        => Assert.False(ConsentPkce.Matches(TestPkce.Challenge, verifier));

    [Fact]
    public void Evaluate_NothingOffered_IsNone()
    {
        Assert.Equal(ConsentPkce.OfferState.None, ConsentPkce.Evaluate(null, null).State);
        Assert.Equal(ConsentPkce.OfferState.None, ConsentPkce.Evaluate("", "").State);
    }

    [Fact]
    public void Evaluate_WellFormedS256_IsValidAndCarriesTheChallenge()
    {
        var offer = ConsentPkce.Evaluate(TestPkce.Challenge, "S256");
        Assert.Equal(ConsentPkce.OfferState.Valid, offer.State);
        Assert.Equal(TestPkce.Challenge, offer.Challenge);
    }

    [Theory]
    [InlineData(TestPkce.Challenge, null)]     // half a pair
    [InlineData(null, "S256")]                 // half a pair
    [InlineData(TestPkce.Challenge, "plain")]  // wrong method
    [InlineData(TestPkce.Challenge, "s256")]   // case matters
    [InlineData("short", "S256")]              // malformed challenge
    public void Evaluate_AnythingElse_IsInvalid(string? challenge, string? method)
        => Assert.Equal(ConsentPkce.OfferState.Invalid, ConsentPkce.Evaluate(challenge, method).State);
}
