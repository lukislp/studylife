// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components.Setup;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Tests;

/// <summary>Audit 2026-11-16 L5: calendar and share links are shown once, right after they were
/// created, because the server keeps only a hash.</summary>
public class OneTimeLinkTests
{
    [Theory]
    [InlineData(false, null, OneTimeLinkView.None)]
    [InlineData(false, "tok", OneTimeLinkView.None)]
    [InlineData(true, null, OneTimeLinkView.Hidden)]
    [InlineData(true, "", OneTimeLinkView.Hidden)]
    [InlineData(true, "tok", OneTimeLinkView.Shown)]
    public void Of_DecidesTheCardState(bool exists, string? token, OneTimeLinkView expected)
    {
        Assert.Equal(expected, OneTimeLink.Of(exists, token));
    }
}

/// <summary>The two link cards render the state OneTimeLink decides (texts render as their keys,
/// see AiConsentFixtures.Register).</summary>
public class OneTimeLinkCardTests : ConsentBunitTest
{
    private ScriptedServer Arrange()
    {
        var server = new ScriptedServer();
        AiConsentFixtures.Register(Ctx, server);
        Services.AddSingleton<IServerLinks>(new HttpServerLinks(new HttpClient { BaseAddress = new Uri("https://studylife.test/") }));
        return server;
    }

    [Fact]
    public void CalendarCard_WithoutLink_OffersCreate_ThenShowsTheLinkOnceWithTheCopyNowNotice()
    {
        var server = Arrange();
        server.OnJson("GET api/system/calendar-token", new CalendarTokenResponseDto { Exists = false });
        server.OnJson("POST api/system/regenerate-calendar-token", new RegenerateCalendarTokenResponseDto { CalendarToken = "fresh" });
        var cut = Render<SetupCalendarSubscriptionCard>();

        cut.WaitForElement("button.btn-secondary").Click();

        var link = cut.WaitForElement("input.input", ChainTimeout).GetAttribute("value");
        Assert.EndsWith("api/sessions/ics?calendarToken=fresh", link);
        Assert.Contains("LinkShownOnceNotice", cut.Markup);
    }

    [Fact]
    public void CalendarCard_ExistingLink_IsNotShown_OnlyRegenerateIsOffered()
    {
        var server = Arrange();
        server.OnJson("GET api/system/calendar-token", new CalendarTokenResponseDto { Exists = true });
        var cut = Render<SetupCalendarSubscriptionCard>();

        cut.WaitForElement("button.btn-danger");
        Assert.Empty(cut.FindAll("input.input"));
        Assert.Contains("LinkHiddenNotice", cut.Markup);
    }

    [Fact]
    public void CalendarCard_OlderServerThatStillReturnsTheToken_ShowsIt()
    {
        var server = Arrange();
        server.OnJson("GET api/system/calendar-token", new CalendarTokenResponseDto { CalendarToken = "plain" });
        var cut = Render<SetupCalendarSubscriptionCard>();

        var link = cut.WaitForElement("input.input").GetAttribute("value");
        Assert.EndsWith("calendarToken=plain", link);
    }

    [Fact]
    public void ProgressShareCard_EnabledWithoutToken_SaysItCannotBeShownAgain()
    {
        var server = Arrange();
        server.OnJson("GET api/settings", new UserSettingsDto { ProgressShareEnabled = true, ProgressShareToken = null });
        var cut = Render<SetupProgressShareCard>();

        cut.WaitForElement("button.btn-danger");
        Assert.Empty(cut.FindAll("input.input"));
        Assert.Contains("LinkHiddenNotice", cut.Markup);
    }

    [Fact]
    public void ProgressShareCard_Enable_ShowsTheLinkOnceWithTheCopyNowNotice()
    {
        var server = Arrange();
        server.OnJson("GET api/settings", new UserSettingsDto { ProgressShareEnabled = false });
        server.OnJson("POST api/settings/progress-share/enable",
            new UserSettingsDto { ProgressShareEnabled = true, ProgressShareToken = "share" });
        var cut = Render<SetupProgressShareCard>();

        cut.WaitForElement("button.btn-secondary").Click();

        var link = cut.WaitForElement("input.input", ChainTimeout).GetAttribute("value");
        Assert.EndsWith("shared/share", link);
        Assert.Contains("LinkShownOnceNotice", cut.Markup);
    }
}
