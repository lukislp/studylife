// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using StudyLife.Server.Diagnostics;

namespace StudyLife.Server.Tests;

/// <summary>Audit 2026-11-16 L5: the progress share token must not reach traces via url.path.</summary>
public class TracePathRedactionTests
{
    [Theory]
    [InlineData("/api/progress/shared/abcDEF123")]
    [InlineData("/API/Progress/Shared/abcDEF123")]
    [InlineData("/api/progress/shared/abc/def")]
    public void SharedProgressPath_IsReplacedByTheTemplate(string path) =>
        Assert.Equal("/api/progress/shared/{token}", TracePathRedaction.RedactedPath(path));

    [Theory]
    [InlineData("/api/progress")]
    [InlineData("/api/progress/shared")]
    [InlineData("/api/progress/sharedx/abc")]
    [InlineData("/api/sessions/ics")]
    [InlineData("/api/notes/5")]
    public void OtherPaths_AreLeftAlone(string path) =>
        Assert.Null(TracePathRedaction.RedactedPath(path));

    [Fact]
    public void Enrich_OverwritesUrlPathTag()
    {
        using var activity = new Activity("test");
        activity.SetTag("url.path", "/api/progress/shared/secret-token");
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/progress/shared/secret-token";

        TracePathRedaction.Enrich(activity, context.Request);

        Assert.Equal("/api/progress/shared/{token}", activity.GetTagItem("url.path"));
    }
}
