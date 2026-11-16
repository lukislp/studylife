// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>Audit L1: a stored SourceUrl or listing URL may only become an http(s) link.</summary>
public class WebLinkPolicyTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/a:b?x=1#y")]
    [InlineData("HTTPS://EXAMPLE.COM/path")]
    [InlineData("  https://example.com  ")]
    public void WebLinks_AreAllowed(string url) => Assert.True(WebLinkPolicy.IsWebLink(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("https://exa\nmple.com")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("studyatrium://callback")]
    [InlineData("//evil.example")]
    [InlineData("/relative")]
    [InlineData("mailto:a@example.com")]
    [InlineData("example.com")]
    public void EverythingElse_IsRefused(string? url) => Assert.False(WebLinkPolicy.IsWebLink(url));

    [Fact]
    public void WebLinkOrNull_DropsNonWebLinks()
    {
        Assert.Null(WebLinkPolicy.WebLinkOrNull("javascript:alert(1)"));
        Assert.Equal("https://example.com", WebLinkPolicy.WebLinkOrNull(" https://example.com "));
    }
}
