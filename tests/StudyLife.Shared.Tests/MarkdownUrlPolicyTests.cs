// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class MarkdownUrlPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    [InlineData("http://example.com/a:b")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("MailTo:someone@example.com")]
    [InlineData("  https://example.com  ")]
    [InlineData("/relative/path")]
    [InlineData("relative/a:b")]
    [InlineData("image.png")]
    [InlineData("#anchor")]
    [InlineData("#a:b")]
    [InlineData("?q=a:b")]
    [InlineData("//example.com")]
    [InlineData("&#106;avascript:alert(1)")] // still entity-encoded: a relative path with a fragment
    public void Allowed(string? url) => Assert.True(MarkdownUrlPolicy.IsAllowed(url));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("\u0001javascript:alert(1)")]
    [InlineData("\u0000javascript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("java\nscript:alert(1)")]
    [InlineData("java\r\nscript:alert(1)")]
    [InlineData("jav ascript:alert(1)")]
    [InlineData("%6Aavascript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("VBSCRIPT:msgbox(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("DATA:text/html,x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("blob:https://example.com/uuid")]
    [InlineData("ftp://example.com")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    [InlineData("studylife://callback")]
    [InlineData(":nothing")]
    public void Refused(string url) => Assert.False(MarkdownUrlPolicy.IsAllowed(url));
}
