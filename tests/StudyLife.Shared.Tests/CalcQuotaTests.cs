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
    public void ZeroTargets_ReturnsZeroPercentAndNoWarning_InsteadOfNaN()
    {
        // maxBar = 0 -> no goal configured. Previously this fell through to a 0/0 division,
        // producing NaN/Infinity (not JSON-serializable, and "width: NaN%" in the dashboard's
        // progress bar CSS). Now it short-circuits to a flat 0%/no-warning result, matching the
        // Python Home Assistant port (coordinator.py), which returns 0% for this case.
        var result = StudyMetrics.CalcQuota(0, 0, 0);

        Assert.Equal(0, result.Percent);
        Assert.Equal(0, result.MinPercent);
        Assert.False(result.Warning);
        Assert.Equal(0, result.MissingHours);
    }

    [Fact]
    public void ZeroTargets_WithHoursStudied_StillReturnsZeroPercentAndNoWarning()
    {
        // Same no-goal-configured case, but with hours already logged - still nothing to
        // divide by, so it stays at a flat 0%/no-warning rather than NaN or a spurious 100%.
        var result = StudyMetrics.CalcQuota(5, 0, 0);

        Assert.Equal(0, result.Percent);
        Assert.Equal(0, result.MinPercent);
        Assert.False(result.Warning);
        Assert.Equal(0, result.MissingHours);
    }
}
