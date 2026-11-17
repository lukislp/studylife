// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

/// <summary>Audit L2: a crafted ?returnUrl= must never turn a sign-in into an open redirect.</summary>
public class ReturnUrlPolicyTests
{
    private static readonly Uri Base = new("https://studyatrium.example/");

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/setup", "/setup")]
    [InlineData("/setup?forgotPassword=true#password", "/setup?forgotPassword=true#password")]
    [InlineData("/connect/mcp?redirect_uri=https%3A%2F%2Fclient.example%2Fcb&state=x",
        "/connect/mcp?redirect_uri=https%3A%2F%2Fclient.example%2Fcb&state=x")]
    public void SameOriginPaths_AreKept(string input, string expected) =>
        Assert.Equal(expected, ReturnUrlPolicy.ToSameOriginPath(input, Base));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]         // what ?returnUrl=/%5Cevil.example decodes to
    [InlineData("/%5Cevil.example")]         // double-encoded variant
    [InlineData("/%5cevil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/\t/evil.example")]         // browsers strip the tab -> "//evil.example"
    [InlineData("/\n/evil.example")]
    [InlineData("https://evil.example/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("evil.example")]
    public void ForeignOrAmbiguousTargets_AreRefused(string? input) =>
        Assert.Null(ReturnUrlPolicy.ToSameOriginPath(input, Base));

    [Fact]
    public void SubPathBase_StaysOnOrigin() =>
        Assert.Equal("/notes", ReturnUrlPolicy.ToSameOriginPath("/notes", new Uri("https://host.example/app/")));
}
