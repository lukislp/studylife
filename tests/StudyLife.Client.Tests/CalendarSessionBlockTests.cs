// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components.Calendar;
using StudyLife.Client.Models;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>The calendar block spans exactly its session and shows completed sessions as done.
/// Texts render as their keys (see AiConsentFixtures.Register).</summary>
public class CalendarSessionBlockTests : ConsentBunitTest
{
    private sealed class FixedClock(DateTime local) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(local, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static readonly DateTime Start = new(2026, 11, 15, 13, 0, 0);

    private IRenderedComponent<CalendarSessionBlock> Arrange(int minutes, bool completed, DateTime now, string? topic = "Backprop")
    {
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(new FixedClock(now));
        Services.AddI18nText();
        var session = new StudySession
        {
            CourseName = "Math",
            Topic = topic,
            StartTime = Start,
            EndTime = Start.AddMinutes(minutes),
            IsCompleted = completed,
        };
        var cut = Render<CalendarSessionBlock>(p => p.Add(c => c.Session, session));
        cut.WaitForElement(".cal-session");
        return cut;
    }

    [Fact]
    public void A_15_minute_session_is_15px_high_and_compact()
    {
        var cut = Arrange(15, false, Start.AddHours(-1));

        var block = cut.Find(".cal-session");
        Assert.Contains("height:15px", block.GetAttribute("style"));
        Assert.Contains("cal-session-compact", block.ClassName);
        Assert.Empty(cut.FindAll(".cs-topic"));
        Assert.Contains("Math", cut.Find(".cs-name").TextContent);
        Assert.Contains("Backprop", block.GetAttribute("title"));
    }

    [Fact]
    public void A_90_minute_session_keeps_the_full_layout()
    {
        var cut = Arrange(90, false, Start.AddHours(-1));

        var block = cut.Find(".cal-session");
        Assert.Contains("height:90px", block.GetAttribute("style"));
        Assert.DoesNotContain("cal-session-compact", block.ClassName);
        Assert.Equal("Backprop", cut.Find(".cs-topic").TextContent);
    }

    [Fact]
    public void A_completed_session_is_marked_done_with_an_accessible_text()
    {
        var cut = Arrange(15, true, Start.AddMinutes(20));

        Assert.Contains("is-done", cut.Find(".cal-session").ClassName);
        Assert.Equal("AriaSessionDone", cut.Find(".cs-done-mark").GetAttribute("aria-label"));
    }

    [Fact]
    public void An_unfinished_past_session_is_muted_without_the_done_mark()
    {
        var cut = Arrange(15, false, Start.AddMinutes(20));

        Assert.Contains("is-past", cut.Find(".cal-session").ClassName);
        Assert.Empty(cut.FindAll(".cs-done-mark"));
    }

    [Fact]
    public void A_running_session_gets_the_now_accent()
    {
        var cut = Arrange(60, false, Start.AddMinutes(10));

        Assert.Contains("is-running", cut.Find(".cal-session").ClassName);
    }
}
