using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class CalcLongestStreakTests
{
    private static readonly DateTime Base = new(2026, 7, 1);

    [Fact]
    public void EmptyHistory_ReturnsZero()
    {
        Assert.Equal(0, StudyMetrics.CalcLongestStreak(Array.Empty<DateTime>()));
    }

    [Fact]
    public void SingleSession_ReturnsOne()
    {
        Assert.Equal(1, StudyMetrics.CalcLongestStreak(new[] { Base }));
    }

    [Fact]
    public void AllConsecutiveDays_ReturnsFullLength()
    {
        var dates = Enumerable.Range(0, 5).Select(i => Base.AddDays(i));
        Assert.Equal(5, StudyMetrics.CalcLongestStreak(dates));
    }

    [Fact]
    public void MultipleRuns_ReturnsTheLongestOne()
    {
        // Run of 2 (days 0-1), gap, run of 4 (days 5-8), gap, run of 1 (day 12).
        var dates = new[]
        {
            Base, Base.AddDays(1),
            Base.AddDays(5), Base.AddDays(6), Base.AddDays(7), Base.AddDays(8),
            Base.AddDays(12),
        };
        Assert.Equal(4, StudyMetrics.CalcLongestStreak(dates));
    }

    [Fact]
    public void LaterLongerRunOverridesEarlierShorterRun()
    {
        // Ensures the running max is correctly updated, not just the first run kept.
        var dates = new[]
        {
            Base, Base.AddDays(1), Base.AddDays(2),
            Base.AddDays(10), Base.AddDays(11), Base.AddDays(12), Base.AddDays(13), Base.AddDays(14),
        };
        Assert.Equal(5, StudyMetrics.CalcLongestStreak(dates));
    }

    [Fact]
    public void DuplicateTimestampsOnSameDay_CountAsOneDay()
    {
        var dates = new[]
        {
            Base, Base.AddHours(2), Base.AddHours(5),
            Base.AddDays(1), Base.AddDays(1).AddHours(9),
        };
        Assert.Equal(2, StudyMetrics.CalcLongestStreak(dates));
    }

    [Fact]
    public void UnorderedInput_IsSortedBeforeCalculating()
    {
        var dates = new[]
        {
            Base.AddDays(2), Base, Base.AddDays(1),
        };
        Assert.Equal(3, StudyMetrics.CalcLongestStreak(dates));
    }
}
