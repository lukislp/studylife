using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class CalcQuotaTests
{
    [Fact]
    public void ZeroHours_ReturnsZeroPercentAndFullWarningGap()
    {
        var result = StudyMetrics.CalcQuota(0, 25, 30);

        Assert.Equal(0, result.Percent);
        Assert.True(result.Warning);
        Assert.Equal(25, result.MissingHours);
    }

    [Fact]
    public void HoursExactlyAtMinTarget_NoLongerWarns()
    {
        var result = StudyMetrics.CalcQuota(25, 25, 30);

        Assert.False(result.Warning);
        Assert.Equal(0, result.MissingHours);
    }

    [Fact]
    public void HoursExactlyAtMaxTarget_DoesNotFillWholeBar()
    {
        // Bar scales to 115% of max, so hitting max exactly should read under 100%.
        var result = StudyMetrics.CalcQuota(30, 25, 30);
        var expectedPercent = 30.0 / (30 * 1.15) * 100;

        Assert.False(result.Warning);
        Assert.Equal(expectedPercent, result.Percent, precision: 10);
        Assert.True(result.Percent < 100);
    }

    [Fact]
    public void HoursAboveMax_PercentCapsAtOneHundred()
    {
        // 115% of max (34.5h) already reaches 100; go further above to confirm the cap holds.
        var result = StudyMetrics.CalcQuota(1000, 25, 30);

        Assert.Equal(100, result.Percent);
        Assert.False(result.Warning);
    }

    [Fact]
    public void HoursAtExactly115PercentOfMax_ReachesFullBar()
    {
        var result = StudyMetrics.CalcQuota(30 * 1.15, 25, 30);

        Assert.Equal(100, result.Percent, precision: 6);
    }

    [Fact]
    public void HoursBelowMin_ReportsMissingHoursAndWarning()
    {
        var result = StudyMetrics.CalcQuota(10, 25, 30);

        Assert.True(result.Warning);
        Assert.Equal(15, result.MissingHours);
    }

    [Fact]
    public void MinPercent_ReflectsMinTargetPositionOnBar()
    {
        var result = StudyMetrics.CalcQuota(0, 25, 30);
        var expectedMinPercent = 25.0 / (30 * 1.15) * 100;

        Assert.Equal(expectedMinPercent, result.MinPercent, precision: 10);
    }

    [Fact]
    public void ZeroTargets_DoesNotThrow_AndTreatsAnyHoursAsAboveTarget()
    {
        // maxBar = 0 -> division by zero -> percent becomes NaN or Infinity, capped by Math.Min(100, ...).
        // hours=0 vs targetMin=0 means warning should be false (0 < 0 is false).
        var result = StudyMetrics.CalcQuota(0, 0, 0);

        Assert.False(result.Warning);
        Assert.Equal(0, result.MissingHours);
    }
}
