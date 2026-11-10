// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class SupportedLanguagesTests
{
    [Fact]
    public void Codes_are_unique_lower_case_two_letter_codes_with_the_fallback_first()
    {
        var codes = SupportedLanguages.Codes;
        Assert.Equal(26, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[a-z]{2}$", c));
        Assert.Equal(SupportedLanguages.Fallback, codes[0]);
        Assert.Contains(SupportedLanguages.LegacyDefault, codes);
    }

    [Theory]
    [InlineData("de", true)]
    [InlineData("pt", true)]
    [InlineData("DE", false)]
    [InlineData("pt-BR", false)]
    [InlineData("xx", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupported_accepts_only_registry_codes(string? code, bool expected)
        => Assert.Equal(expected, SupportedLanguages.IsSupported(code));

    [Theory]
    [InlineData("de-AT", "de")]
    [InlineData("pt_PT", "pt")]
    [InlineData(" EN ", "en")]
    [InlineData("zh-Hans", null)]
    [InlineData("-de", null)]
    [InlineData(null, null)]
    public void Match_finds_the_language_of_a_tag(string? tag, string? expected)
        => Assert.Equal(expected, SupportedLanguages.Match(tag));
}
