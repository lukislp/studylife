// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Configuration;

namespace StudyLife.Server.Tests;

/// <summary>Audit 2026-11-19 S3: the startup warning for a multi-account server without Legal:* pages.</summary>
public class LegalStartupCheckTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(40, true)]
    public void It_warns_only_from_the_second_account(int accounts, bool expected) =>
        Assert.Equal(expected, LegalStartupCheck.ShouldWarn(new LegalOptions(), accounts));

    [Theory]
    [InlineData("https://op.example.org/privacy", null)]
    [InlineData(null, "https://op.example.org/imprint")]
    [InlineData("https://op.example.org/privacy", "https://op.example.org/imprint")]
    public void It_stays_quiet_once_the_operator_set_either_page(string? privacy, string? imprint) =>
        Assert.False(LegalStartupCheck.ShouldWarn(new LegalOptions { PrivacyUrl = privacy, ImprintUrl = imprint }, 5));

    [Fact]
    public void Blank_settings_count_as_unset() =>
        Assert.True(LegalStartupCheck.ShouldWarn(new LegalOptions { PrivacyUrl = "  ", ImprintUrl = "" }, 3));

    [Fact]
    public void The_warning_names_both_settings()
    {
        Assert.Contains("Legal__PrivacyUrl", LegalStartupCheck.Warning);
        Assert.Contains("Legal__ImprintUrl", LegalStartupCheck.Warning);
    }
}
