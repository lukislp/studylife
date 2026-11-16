// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>Audit L12: a client-supplied round count or phase end must not be able to make the
/// Live Activity worker step through millions of phases.</summary>
public class TimerStateLimitsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TimerStateLimitsTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(3, 3)]
    [InlineData(int.MaxValue, TimerStateLimits.MaxRound)]
    public void ClampRound_KeepsTheRoundInRange(int round, int expected) =>
        Assert.Equal(expected, TimerStateLimits.ClampRound(round));

    [Fact]
    public void ClampPhaseEnd_PullsFarAwayValuesIntoTheWindow()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0);
        Assert.Null(TimerStateLimits.ClampPhaseEnd(null, now));
        Assert.Equal(now.AddMinutes(25), TimerStateLimits.ClampPhaseEnd(now.AddMinutes(25), now));
        Assert.Equal(now - TimerStateLimits.MaxPhaseOffset, TimerStateLimits.ClampPhaseEnd(DateTime.MinValue, now));
        Assert.Equal(now + TimerStateLimits.MaxPhaseOffset, TimerStateLimits.ClampPhaseEnd(DateTime.MaxValue, now));
    }

    [Fact]
    public async Task SaveAsync_ClampsRoundAndPhaseEndBeforeStoring()
    {
        await _factory.WithServiceAsync<ITimerStateService>((timer, _) => timer.SaveAsync(new TimerStateDto
        {
            IsRunning = true,
            CurrentRound = int.MaxValue,
            TimerModeId = 1,
            PhaseEndsAt = DateTime.MinValue.AddDays(1),
        }));

        var stored = await _factory.WithDbAsync(db => db.TimerState.AsNoTracking().FirstAsync());
        Assert.Equal(TimerStateLimits.MaxRound, stored.CurrentRound);
        Assert.True(stored.PhaseEndsAt > DateTime.Now - TimerStateLimits.MaxPhaseOffset - TimeSpan.FromMinutes(1));
    }
}
