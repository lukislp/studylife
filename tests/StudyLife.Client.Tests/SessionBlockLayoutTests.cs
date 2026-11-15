// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

public class SessionBlockLayoutTests
{
    private static readonly DateTime Start = new(2026, 11, 15, 13, 0, 0);

    private static SessionBlockLayout Layout(int minutes, bool done, DateTime now) =>
        SessionBlockLayout.Compute(Start, Start.AddMinutes(minutes), done, now);

    [Theory]
    [InlineData(15, true)]
    [InlineData(5, true)]
    [InlineData(44, true)]
    [InlineData(45, false)]
    [InlineData(90, false)]
    public void Height_is_the_session_length_and_short_blocks_are_compact(int minutes, bool compact)
    {
        var layout = Layout(minutes, false, Start.AddHours(-1));

        Assert.Equal(minutes, layout.Height);
        Assert.Equal(compact, layout.Compact);
        Assert.Equal(13 * 60, layout.Top);
    }

    [Fact]
    public void A_session_below_the_minimum_is_stretched_to_it_and_never_further()
    {
        var layout = Layout(2, false, Start.AddHours(-1));

        Assert.Equal(SessionBlockLayout.DefaultMinHeightPx, layout.Height);
        Assert.True(layout.Compact);
    }

    [Fact]
    public void Callers_can_raise_the_minimum()
    {
        var layout = SessionBlockLayout.Compute(Start, Start.AddMinutes(5), false, Start, 16);

        Assert.Equal(16, layout.Height);
    }

    [Fact]
    public void The_block_ends_before_a_now_line_after_the_session()
    {
        var layout = Layout(15, true, Start.AddMinutes(20));

        Assert.True(layout.Top + layout.Height < 13 * 60 + 20);
    }

    [Theory]
    [InlineData(-30, false, SessionBlockState.Upcoming)]
    [InlineData(0, false, SessionBlockState.Running)]
    [InlineData(14, false, SessionBlockState.Running)]
    [InlineData(15, false, SessionBlockState.Past)]
    [InlineData(20, false, SessionBlockState.Past)]
    [InlineData(5, true, SessionBlockState.Done)]
    [InlineData(20, true, SessionBlockState.Done)]
    [InlineData(-30, true, SessionBlockState.Done)]
    public void State_follows_completion_first_then_the_clock(int minutesFromStart, bool done, SessionBlockState expected)
    {
        Assert.Equal(expected, Layout(15, done, Start.AddMinutes(minutesFromStart)).State);
    }

    [Theory]
    [InlineData(SessionBlockState.Done, "is-done")]
    [InlineData(SessionBlockState.Past, "is-past")]
    [InlineData(SessionBlockState.Running, "is-running")]
    [InlineData(SessionBlockState.Upcoming, "")]
    public void Each_state_has_a_css_class(SessionBlockState state, string css)
    {
        Assert.Equal(css, new SessionBlockLayout(0, 10, false, state).StateClass);
    }
}
