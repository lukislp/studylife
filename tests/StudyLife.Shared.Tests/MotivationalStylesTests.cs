// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class MotivationalStylesTests
{
    [Fact]
    public void Canonical_MapsTheRetiredStorageValueToThoughtful()
    {
        Assert.Equal(MotivationalStyles.Thoughtful, MotivationalStyles.Canonical(MotivationalStyles.LegacyThoughtful));
    }

    [Theory]
    [InlineData("thoughtful")]
    [InlineData("zen")]
    [InlineData("intense")]
    [InlineData("hype")]
    [InlineData("something-unknown")]
    public void Canonical_LeavesEveryOtherValueAlone(string style)
    {
        Assert.Equal(style, MotivationalStyles.Canonical(style));
    }

    [Fact]
    public void Canonical_NullBecomesEmpty()
    {
        Assert.Equal("", MotivationalStyles.Canonical(null));
    }

    [Fact]
    public void ResolveOrDefault_UpgradesLegacyAndFallsBackForUnknown()
    {
        Assert.Equal(MotivationalStyles.Thoughtful, MotivationalStyles.ResolveOrDefault(MotivationalStyles.LegacyThoughtful));
        Assert.Equal(MotivationalStyles.Hype, MotivationalStyles.ResolveOrDefault("hype"));
        Assert.Equal(MotivationalStyles.Default, MotivationalStyles.ResolveOrDefault("nope"));
        Assert.Equal(MotivationalStyles.Default, MotivationalStyles.ResolveOrDefault(null));
    }

    [Fact]
    public void LegacyValueIsNotOneOfTheCurrentStyles()
    {
        Assert.DoesNotContain(MotivationalStyles.LegacyThoughtful, MotivationalStyles.All);
    }
}
