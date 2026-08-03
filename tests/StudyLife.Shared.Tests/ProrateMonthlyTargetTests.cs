using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class ProrateMonthlyTargetTests
{
    [Fact]
    public void FirstDayOfMonth_ProratesToOneWeekOfTarget()
    {
        // July 2026 has 31 days -> ceil(31/7) = 5 weeks total. Day 1 -> weeksElapsed = ceil(1/7) = 1.
        var (min, max) = StudyMetrics.ProrateMonthlyTarget(100, 130, new DateTime(2026, 7, 1));

        Assert.Equal((int)Math.Round(100 * 1.0 / 5.0), min);
        Assert.Equal((int)Math.Round(130 * 1.0 / 5.0), max);
    }

    [Fact]
    public void LastDayOfMonth_ProratesToFullTarget()
    {
        // Last day of July: weeksElapsed capped at totalWeeksInMonth (5) -> full target.
        var (min, max) = StudyMetrics.ProrateMonthlyTarget(100, 130, new DateTime(2026, 7, 31));

        Assert.Equal(100, min);
        Assert.Equal(130, max);
    }

    [Fact]
    public void FebruaryNonLeapYear_UsesTwentyEightDays()
    {
        // 2026 is not a leap year: Feb has 28 days -> ceil(28/7) = 4 weeks total.
        Assert.False(DateTime.IsLeapYear(2026));
        var (min, max) = StudyMetrics.ProrateMonthlyTarget(100, 130, new DateTime(2026, 2, 28));

        Assert.Equal(100, min);
        Assert.Equal(130, max);
    }

    [Fact]
    public void FebruaryLeapYear_LastDay_FallsShortOfFullTarget_DueToCeilRoundingAsymmetry()
    {
        // 2028 is a leap year: Feb has 29 days -> totalWeeksInMonth = ceil(29/7) = 5.
        // But the last day is only 28 days after month start, so weeksElapsed = ceil(28/7) = 4.
        // Unlike July (31 days) or non-leap Feb (28 days), the last calendar day here does NOT
        // reach the full monthly target - it lands one "week" short (4/5 of the goal), because
        // daysInMonth (29) sits exactly at a mod-7 boundary (29 mod 7 == 1) that the two separate
        // ceil() calls round differently. This mirrors the intentional _calc_month_quota parity
        // with the Home Assistant integration referenced in ProrateMonthlyTarget's XML doc, so it
        // is documented behavior here, not something this test suite treats as a bug.
        Assert.True(DateTime.IsLeapYear(2028));
        var (min, max) = StudyMetrics.ProrateMonthlyTarget(100, 130, new DateTime(2028, 2, 29));

        Assert.Equal(80, min);
        Assert.Equal(104, max);
    }

    [Fact]
    public void FebruaryLeapYear_FirstDay_ProratesOverFiveWeeksInsteadOfFour()
    {
        // Leap year Feb has one more day than non-leap, potentially pushing totalWeeksInMonth
        // from 4 to 5 weeks (ceil(29/7)=5 vs ceil(28/7)=4), which changes the day-1 fraction.
        var (min2026, max2026) = StudyMetrics.ProrateMonthlyTarget(100, 130, new DateTime(2026, 2, 1));
        var (min2028, max2028) = StudyMetrics.ProrateMonthlyTarget(100, 130, new DateTime(2028, 2, 1));

        Assert.Equal((int)Math.Round(100 * 1.0 / 4.0), min2026);
        Assert.Equal((int)Math.Round(100 * 1.0 / 5.0), min2028);
        Assert.NotEqual(min2026, min2028);
    }

    [Fact]
    public void MidMonth_ProratesProportionallyToWeeksElapsed()
    {
        // July 15, 2026 is 14 days after July 1 -> weeksElapsed = ceil(14/7) = 2 of 5 weeks.
        var (min, max) = StudyMetrics.ProrateMonthlyTarget(100, 130, new DateTime(2026, 7, 15));

        Assert.Equal((int)Math.Round(100 * 2.0 / 5.0), min);
        Assert.Equal((int)Math.Round(130 * 2.0 / 5.0), max);
    }

    [Fact]
    public void ZeroGoal_ReturnsZeroRegardlessOfDate()
    {
        var (min, max) = StudyMetrics.ProrateMonthlyTarget(0, 0, new DateTime(2026, 7, 15));

        Assert.Equal(0, min);
        Assert.Equal(0, max);
    }
}
