// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

/// <summary>The app's language reaches the auth pages it opens in the system browser (?applang=).</summary>
public class AppReturnContextLanguageTests
{
    [Fact]
    public void The_app_language_is_read_from_an_app_flow()
    {
        var context = AppReturnContext.FromUri(new Uri("https://studylife.example.com/login?app=1&appchallenge=abc&applang=en"));
        Assert.Equal("en", context.Language);
    }

    [Fact]
    public void The_app_language_is_carried_to_the_other_auth_pages()
    {
        var context = AppReturnContext.FromUri(new Uri("https://studylife.example.com/login?app=1&appchallenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&applang=fr"));
        Assert.Contains("applang=fr", context.LinkQuery);
    }

    [Fact]
    public void Outside_an_app_flow_the_parameter_is_ignored()
    {
        var context = AppReturnContext.FromUri(new Uri("https://studylife.example.com/login?applang=en"));
        Assert.Null(context.Language);
        Assert.Equal("", context.LinkQuery);
    }

    [Fact]
    public void An_unsupported_language_is_ignored()
    {
        var context = AppReturnContext.FromUri(new Uri("https://studylife.example.com/login?app=1&applang=xx"));
        Assert.Null(context.Language);
        Assert.DoesNotContain("applang", context.LinkQuery);
    }
}
