// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Controllers;

namespace StudyLife.Server.Tests;

/// <summary>Audit L11: metric labels come from closed sets and client text cannot forge or flood
/// log lines.</summary>
public class TelemetryLabelTests
{
    [Theory]
    [InlineData("/notes/123?x=1", "/notes")]
    [InlineData("/focus", "/focus")]
    [InlineData("/definitely-not-a-page", "other")]
    [InlineData("/a1b2c3d4e5", "other")]
    [InlineData("notes", "unknown")]
    public void SanitizePage_BucketsToKnownPages(string page, string expected) =>
        Assert.Equal(expected, TelemetryController.SanitizePage(page));

    [Theory]
    [InlineData("TypeError", "TypeError")]
    [InlineData("System.InvalidOperationException", "System.InvalidOperationException")]
    [InlineData("My.Custom.Error123", "other")]
    [InlineData(null, "other")]
    public void SanitizeErrorType_BucketsToKnownTypes(string? type, string expected) =>
        Assert.Equal(expected, TelemetryController.SanitizeErrorType(type));

    [Fact]
    public void ForLogSingleLine_FoldsLineBreaksAndCapsTheLength()
    {
        var forged = "at a.js:1\r\nClientError kind=js forged\nat b.js:2\t\u0085end" + new string('x', 50);

        var line = TelemetryController.ForLogSingleLine(forged, 40);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\u0085', line);
        Assert.True(line.Length <= 40);
        Assert.Equal("", TelemetryController.ForLogSingleLine(null, 40));
    }
}
