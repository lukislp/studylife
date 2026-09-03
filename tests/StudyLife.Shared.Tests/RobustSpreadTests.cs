using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class RobustSpreadTests
{
    [Fact]
    public void FewerThanTwoValues_ReturnsZero()
    {
        Assert.Equal(0, StudyMetrics.RobustSpread(Array.Empty<double>()));
        Assert.Equal(0, StudyMetrics.RobustSpread(new[] { 330.0 }));
    }

    [Fact]
    public void IdenticalValues_ReturnsZero()
    {
        Assert.Equal(0, StudyMetrics.RobustSpread(new[] { 330.0, 330.0, 330.0, 330.0 }));
    }

    [Fact]
    public void NormalLikeSample_MatchesStandardDeviationScale()
    {
        // Bedtimes spread evenly +-20 min around 23:30 (330 min after 6pm): MAD is 10, scaled ~14.8,
        // the population standard deviation of the same sample is ~14.1 - same order, as intended.
        var onsets = new[] { 310.0, 315, 320, 325, 330, 330, 335, 340, 345, 350 };
        var spread = StudyMetrics.RobustSpread(onsets);
        Assert.InRange(spread, 12, 17);
    }

    [Fact]
    public void SingleNapOutlier_DoesNotBlowUpTheSpread()
    {
        // 13 regular nights around 23:30 plus one 14:00 nap (1200 min after 6pm). The plain
        // standard deviation of this sample is ~225 min; the robust spread must stay near the
        // regular nights' own variability.
        var onsets = new List<double> { 320, 325, 330, 335, 340, 330, 328, 332, 326, 338, 331, 329, 333, 1200 };
        var spread = StudyMetrics.RobustSpread(onsets);
        Assert.InRange(spread, 3, 12);

        var mean = onsets.Average();
        var stdDev = Math.Sqrt(onsets.Sum(v => (v - mean) * (v - mean)) / onsets.Count);
        Assert.True(stdDev > 200, $"sanity: plain std dev should be large here but was {stdDev}");
    }

    [Fact]
    public void Median_HandlesOddAndEvenCounts()
    {
        Assert.Equal(3, StudyMetrics.Median(new[] { 5.0, 1, 3 }));
        Assert.Equal(2.5, StudyMetrics.Median(new[] { 4.0, 1, 2, 3 }));
        Assert.Equal(0, StudyMetrics.Median(Array.Empty<double>()));
    }
}
