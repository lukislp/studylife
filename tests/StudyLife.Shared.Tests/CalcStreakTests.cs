using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class CalcStreakTests
{
    private static readonly DateTime Today = new(2026, 7, 13);

    [Fact]
    public void EmptyHistory_ReturnsZero()
    {
        Assert.Equal(0, StudyMetrics.CalcStreak(Array.Empty<DateTime>(), Today));
    }

    [Fact]
    public void StudiedToday_SingleDay_ReturnsOne()
    {
        var dates = new[] { Today };
        Assert.Equal(1, StudyMetrics.CalcStreak(dates, Today));
    }

    [Fact]
    public void StudiedOnlyYesterday_StillCountsAsActiveStreak()
    {
        // Streak stays alive until midnight even if today has no session yet.
        var dates = new[] { Today.AddDays(-1) };
        Assert.Equal(1, StudyMetrics.CalcStreak(dates, Today));
    }

    [Fact]
    public void GapOfTwoOrMoreDays_ResetsStreakToZero()
    {
        var dates = new[] { Today.AddDays(-3), Today.AddDays(-2) };
        Assert.Equal(0, StudyMetrics.CalcStreak(dates, Today));
    }

    [Fact]
    public void ConsecutiveDaysEndingToday_CountsAllOfThem()
    {
        var dates = new[]
        {
            Today,
            Today.AddDays(-1),
            Today.AddDays(-2),
            Today.AddDays(-3),
        };
        Assert.Equal(4, StudyMetrics.CalcStreak(dates, Today));
    }

    [Fact]
    public void ConsecutiveDaysEndingYesterday_CountsFromYesterdayBackward()
    {
        var dates = new[]
        {
            Today.AddDays(-1),
            Today.AddDays(-2),
            Today.AddDays(-3),
        };
        Assert.Equal(3, StudyMetrics.CalcStreak(dates, Today));
    }

    [Fact]
    public void MultipleSessionsOnSameDay_CountAsOneDay()
    {
        var dates = new[]
        {
            Today,
            Today.AddHours(3),
            Today.AddHours(10),
        };
        Assert.Equal(1, StudyMetrics.CalcStreak(dates, Today));
    }

    [Fact]
    public void SessionSpanningMidnight_UsesDateComponentOnly()
    {
        // A DateTime with a time component just before midnight still belongs to that calendar day.
        var dates = new[] { Today.AddDays(-1).AddHours(23) };
        Assert.Equal(1, StudyMetrics.CalcStreak(dates, Today));
    }

    [Fact]
    public void FutureStudyTimes_AreIgnoredUnlessTheyMatchAnchor()
    {
        var dates = new[] { Today.AddDays(1) };
        Assert.Equal(0, StudyMetrics.CalcStreak(dates, Today));
    }
}
