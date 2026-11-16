// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

/// <summary>The real focus intervals a finished round leaves behind (Apple Health logging).</summary>
public class FocusRoundLogTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_uninterrupted_round_is_one_interval_from_its_real_start_to_its_end()
    {
        var log = new FocusRoundLog();
        log.Started(T0);
        log.RoundEnded(T0.AddMinutes(25));

        var finished = log.TakeFinished();

        Assert.Equal([new FocusInterval(T0, T0.AddMinutes(25))], finished);
    }

    [Fact]
    public void Paused_time_is_not_part_of_any_interval()
    {
        var log = new FocusRoundLog();
        log.Started(T0);
        log.Paused(T0.AddMinutes(10));
        log.Started(T0.AddMinutes(40)); // resumed half an hour later
        log.RoundEnded(T0.AddMinutes(55));

        Assert.Equal(
            [new FocusInterval(T0, T0.AddMinutes(10)), new FocusInterval(T0.AddMinutes(40), T0.AddMinutes(55))],
            log.TakeFinished());
    }

    [Fact]
    public void An_abandoned_round_leaves_nothing_and_does_not_leak_into_the_next()
    {
        var log = new FocusRoundLog();
        log.Started(T0);
        log.Paused(T0.AddMinutes(5));
        log.Abandoned();
        log.Started(T0.AddHours(1));
        log.RoundEnded(T0.AddHours(1).AddMinutes(25));

        Assert.Equal([new FocusInterval(T0.AddHours(1), T0.AddHours(1).AddMinutes(25))], log.TakeFinished());
    }

    [Fact]
    public void Rounds_missed_while_suspended_are_all_handed_over_once()
    {
        var log = new FocusRoundLog();
        log.Started(T0);
        log.RoundEnded(T0.AddMinutes(25));
        log.Started(T0.AddMinutes(30)); // next focus phase, begun at the previous phase's end
        log.RoundEnded(T0.AddMinutes(55));

        Assert.Equal(2, log.TakeFinished().Count);
        Assert.Empty(log.TakeFinished());
    }

    [Fact]
    public void A_pause_without_running_focus_time_adds_nothing()
    {
        var log = new FocusRoundLog();
        log.Paused(T0);
        log.RoundEnded(T0.AddMinutes(1));

        Assert.Empty(log.TakeFinished());
    }
}
