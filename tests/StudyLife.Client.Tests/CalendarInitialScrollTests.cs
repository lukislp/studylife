// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Models;
using StudyLife.Client.Pages;
using StudyLife.Client.Services;
using StudyLife.Shared;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>The pure decision: where the calendar grid opens, for the week and the day view alike.</summary>
public class CalendarInitialScrollDecisionTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 14, 25, 0);

    private static StudySession Session(DateTime start) => new() { StartTime = start, EndTime = start.AddHours(1) };

    [Fact]
    public void Today_in_view_scrolls_to_the_current_time_with_context_above()
    {
        var target = CalendarInitialScroll.Resolve(Now, [Now.Date], [Session(new DateTime(2026, 11, 18, 10, 15, 0))]);

        Assert.Equal(14 * 60 + 25, target.Minute);
        Assert.Equal(CalendarInitialScroll.ContextFraction, target.ViewportFraction);
    }

    [Fact]
    public void Today_inside_a_visible_week_counts_as_today()
    {
        var week = Enumerable.Range(0, 7).Select(i => new DateTime(2026, 11, 16).AddDays(i)).ToList();

        Assert.Equal(14 * 60 + 25, CalendarInitialScroll.Resolve(Now, week, []).Minute);
    }

    [Fact]
    public void Another_day_scrolls_to_its_first_session()
    {
        var day = new DateTime(2026, 11, 20);
        var sessions = new[]
        {
            Session(day.AddHours(15)),
            Session(day.AddHours(9).AddMinutes(30)),
            Session(day.AddDays(1).AddHours(6)), // not a visible day
        };

        var target = CalendarInitialScroll.Resolve(Now, [day], sessions);

        Assert.Equal(9 * 60 + 30, target.Minute);
        Assert.Equal(CalendarInitialScroll.ContextFraction, target.ViewportFraction);
    }

    [Fact]
    public void An_empty_other_day_opens_at_the_morning_default_at_the_top()
    {
        var target = CalendarInitialScroll.Resolve(Now, [new DateTime(2026, 11, 20)], []);

        Assert.Equal(CalendarInitialScroll.DefaultMinute, target.Minute);
        Assert.Equal(0, target.ViewportFraction);
    }
}

/// <summary>Calendar.razor hands the decision to the JS helper once the real grid exists, and again
/// after each navigation. Texts render as their keys (see AiConsentFixtures.Register).</summary>
public class CalendarInitialScrollPageTests : ConsentBunitTest
{
    private const string ScrollCall = "scrollElementToMinute";

    private IRenderedComponent<Calendar> Arrange(bool narrow, params StudySession[] sessions)
    {
        var server = new ScriptedServer()
            .OnJson("GET api/settings", new UserSettings())
            .OnJson("GET api/courses", Array.Empty<CourseDto>())
            .OnJson("GET api/sessions", sessions);
        AiConsentFixtures.Register(Ctx, server);
        Services.AddSingleton<INativeIcsIntake>(new NoNativeIcsIntake());
        Ctx.JSInterop.Setup<bool>("isNarrowViewport").SetResult(narrow);
        var cut = Render<Calendar>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".day-body")), ChainTimeout);
        return cut;
    }

    private int ScrollCalls() => Ctx.JSInterop.Invocations.Count(i => i.Identifier == ScrollCall);

    private Bunit.JSRuntimeInvocation LastScrollCall() => Ctx.JSInterop.Invocations.Last(i => i.Identifier == ScrollCall);

    [Fact]
    public void Day_view_on_a_narrow_screen_scrolls_to_now_once_the_grid_is_rendered()
    {
        var cut = Arrange(narrow: true);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".cal-day-mode")), ChainTimeout);
        cut.WaitForAssertion(() => Assert.True(ScrollCalls() >= 1), ChainTimeout);
        var last = LastScrollCall();
        Assert.Equal(CalendarInitialScroll.ContextFraction, last.Arguments[2]);
        var minute = Assert.IsType<int>(last.Arguments[1]);
        var now = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        Assert.True(Math.Abs(minute - now) <= 5 || now < 5 || now > 24 * 60 - 5, $"minute {minute} is not the current time {now}");
    }

    [Fact]
    public async Task Paging_scrolls_again_but_a_plain_render_does_not()
    {
        var cut = Arrange(narrow: true);
        cut.WaitForAssertion(() => Assert.True(ScrollCalls() >= 1), ChainTimeout);
        var settled = ScrollCalls();

        await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("ArrowRight", false, false, false, false, false));

        cut.WaitForAssertion(() => Assert.Equal(settled + 1, ScrollCalls()), ChainTimeout);
        // Another day without sessions: the morning default at the top of the view.
        Assert.Equal(CalendarInitialScroll.DefaultMinute, LastScrollCall().Arguments[1]);

        await cut.InvokeAsync(() => cut.Render());
        Assert.Equal(settled + 1, ScrollCalls());
    }

    [Fact]
    public async Task The_first_session_of_another_day_is_the_target()
    {
        var tomorrow = DateTime.Today.AddDays(1);
        var cut = Arrange(narrow: true,
            new StudySession { Id = 1, StartTime = tomorrow.AddHours(10).AddMinutes(15), EndTime = tomorrow.AddHours(11) });
        cut.WaitForAssertion(() => Assert.True(ScrollCalls() >= 1), ChainTimeout);
        var before = ScrollCalls();

        await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("ArrowRight", false, false, false, false, false));

        cut.WaitForAssertion(() => Assert.Equal(before + 1, ScrollCalls()), ChainTimeout);
        Assert.Equal(10 * 60 + 15, LastScrollCall().Arguments[1]);
    }
}
