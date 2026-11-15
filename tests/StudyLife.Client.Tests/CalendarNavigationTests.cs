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

/// <summary>The pure decision: which key or gesture on the calendar means which step.</summary>
public class CalendarNavigationDecisionTests
{
    [Theory]
    [InlineData("ArrowLeft", CalendarNavStep.Previous)]
    [InlineData("ArrowRight", CalendarNavStep.Next)]
    [InlineData("t", CalendarNavStep.Today)]
    [InlineData("T", CalendarNavStep.Today)]
    [InlineData("ArrowUp", CalendarNavStep.None)]
    [InlineData("a", CalendarNavStep.None)]
    [InlineData(null, CalendarNavStep.None)]
    public void Plain_keys_map_to_steps(string? key, CalendarNavStep expected) =>
        Assert.Equal(expected, CalendarNavigation.FromKey(key, false, false, false, false, false));

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Modifier_combinations_stay_free(bool ctrl, bool meta, bool alt, bool shift) =>
        Assert.Equal(CalendarNavStep.None, CalendarNavigation.FromKey("ArrowLeft", ctrl, meta, alt, shift, false));

    [Theory]
    [InlineData("ArrowLeft")]
    [InlineData("ArrowRight")]
    [InlineData("t")]
    public void Keys_in_a_text_field_or_dialog_are_ignored(string key) =>
        Assert.Equal(CalendarNavStep.None, CalendarNavigation.FromKey(key, false, false, false, false, blocked: true));

    [Fact]
    public void Wheel_needs_the_threshold_and_a_dominant_horizontal_axis()
    {
        var t = CalendarNavigation.WheelThreshold;
        Assert.Equal(CalendarNavStep.Next, CalendarNavigation.FromHorizontalGesture(t, 0));
        Assert.Equal(CalendarNavStep.Previous, CalendarNavigation.FromHorizontalGesture(-t, 10));
        Assert.Equal(CalendarNavStep.None, CalendarNavigation.FromHorizontalGesture(t - 1, 0));
        // vertical scrolling with some horizontal drift never turns pages
        Assert.Equal(CalendarNavStep.None, CalendarNavigation.FromHorizontalGesture(t, t));
        Assert.Equal(CalendarNavStep.None, CalendarNavigation.FromHorizontalGesture(t, -t * 2));
    }
}

/// <summary>Calendar.razor applies the decision: the header title (aria-live) names the new
/// period. Texts render as their keys (see AiConsentFixtures.Register).</summary>
public class CalendarKeyboardNavigationTests : ConsentBunitTest
{
    private IRenderedComponent<Calendar> Arrange()
    {
        var server = new ScriptedServer()
            .OnJson("GET api/settings", new UserSettings())
            .OnJson("GET api/courses", Array.Empty<CourseDto>())
            .OnJson("GET api/sessions", Array.Empty<StudySession>());
        AiConsentFixtures.Register(Ctx, server);
        Services.AddSingleton<INativeIcsIntake>(new NoNativeIcsIntake());
        var cut = Render<Calendar>();
        cut.WaitForAssertion(() => cut.Find(".cal-header h2"));
        return cut;
    }

    private static string Title(IRenderedComponent<Calendar> cut) => cut.Find(".cal-header h2").TextContent;

    [Fact]
    public void The_header_title_is_a_polite_live_region()
    {
        var cut = Arrange();
        Assert.Equal("polite", cut.Find(".cal-header h2").GetAttribute("aria-live"));
    }

    [Fact]
    public async Task Arrow_keys_move_one_week_and_t_returns_to_today()
    {
        var cut = Arrange();
        var today = Title(cut);

        Assert.True(await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("ArrowRight", false, false, false, false, false)));
        var next = Title(cut);
        Assert.NotEqual(today, next);

        Assert.True(await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("ArrowLeft", false, false, false, false, false)));
        Assert.Equal(today, Title(cut));

        await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("ArrowLeft", false, false, false, false, false));
        Assert.True(await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("t", false, false, false, false, false)));
        Assert.Equal(today, Title(cut));
    }

    [Fact]
    public async Task Ignored_cases_leave_the_period_alone_and_do_not_consume_the_key()
    {
        var cut = Arrange();
        var today = Title(cut);

        Assert.False(await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("ArrowRight", false, false, false, false, blocked: true)));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("ArrowRight", false, true, false, false, false)));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("ArrowRight", true, false, false, false, false)));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("ArrowRight", false, false, true, false, false)));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("x", false, false, false, false, false)));
        Assert.Equal(today, Title(cut));
    }

    [Fact]
    public async Task A_horizontal_wheel_gesture_pages_and_a_vertical_one_does_not()
    {
        var cut = Arrange();
        var today = Title(cut);

        await cut.InvokeAsync(() => cut.Instance.OnHorizontalGesture(10, 400));
        Assert.Equal(today, Title(cut));

        await cut.InvokeAsync(() => cut.Instance.OnHorizontalGesture(CalendarNavigation.WheelThreshold, 0));
        Assert.NotEqual(today, Title(cut));
    }

    [Fact]
    public async Task In_day_view_the_arrows_move_by_one_day()
    {
        var cut = Arrange();
        cut.Find(".cal-view-opt:last-child").Click();
        var day = Title(cut);

        await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("ArrowRight", false, false, false, false, false));
        Assert.NotEqual(day, Title(cut));
        await cut.InvokeAsync(() => cut.Instance.OnShortcutKey("ArrowLeft", false, false, false, false, false));
        Assert.Equal(day, Title(cut));
    }
}
