using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Index
{
    // HRV-based study readiness: a personal rolling Z-score, not a trained/learned model - see
    // docs/ARCHITECTURE.md and the INativeHealthData comment. Needs at least ReadinessMinSamples
    // days of HRV history before a personal baseline means anything (same spirit as
    // ProductivityMinSessions in Index.Insights.razor.cs) - stays hidden until then, and always
    // hidden outside the native iOS app (NoNativeHealthData.IsAvailable is false everywhere else).
    private bool _readinessVisible;
    private double _readinessPercent;
    private string _readinessStatus = "";
    private double _readinessTodayMs;
    private double _readinessBaselineMs;
    private const int ReadinessMinSamples = 14;

    private string ReadinessStatusText => _readinessStatus switch
    {
        "above" => T.ReadinessAboveBaseline ?? "",
        "below" => T.ReadinessBelowBaseline ?? "",
        _ => T.ReadinessAroundBaseline ?? "",
    };

    // The percent/badge alone don't explain themselves - e.g. 100% just means the Z-score hit
    // the display clamp (+2.5 SD), which could be a genuine outlier OR an artifact of a small,
    // low-variance baseline (only ReadinessMinSamples days required). Showing the actual ms
    // values lets it be sanity-checked against the Health app instead of trusting a bare score.
    private string ReadinessValueText => string.Format(
        T.ReadinessValueFormat ?? "",
        _readinessTodayMs.ToString("0"),
        _readinessBaselineMs.ToString("0"));

    private void BuildReadinessScore(IReadOnlyList<double>? hrvSamples)
    {
        _readinessVisible = false;
        _readinessPercent = 0;
        _readinessStatus = "";
        _readinessTodayMs = 0;
        _readinessBaselineMs = 0;

        if (hrvSamples == null || hrvSamples.Count < ReadinessMinSamples) return;

        // Most recent entry is today's value (see INativeHealthData.GetRecentHrvAsync) - the
        // baseline is everything BEFORE it, so today never influences its own comparison.
        var today = hrvSamples[^1];
        var baseline = hrvSamples.Take(hrvSamples.Count - 1).ToList();
        var mean = baseline.Average();
        var variance = baseline.Sum(v => (v - mean) * (v - mean)) / baseline.Count;
        var stdDev = Math.Sqrt(variance);
        if (stdDev == 0) return; // no variation to compare against - can't say anything meaningful

        var z = (today - mean) / stdDev;
        // Z of 0 (exactly average) -> 50%, +-2.5 (rare) saturates the display at 0/100%.
        _readinessPercent = Math.Clamp(50 + z * 20, 0, 100);
        _readinessStatus = z >= 0.5 ? "above" : z <= -0.5 ? "below" : "around";
        _readinessTodayMs = today;
        _readinessBaselineMs = mean;
        _readinessVisible = true;
    }

    // Sleep consistency: unlike readiness, this isn't "today vs. baseline" - it's "how variable
    // has bedtime been lately," so the displayed statistic is the spread of onset times itself,
    // not a Z-score. The spread is StudyMetrics.RobustSpread (scaled median absolute deviation)
    // rather than a plain standard deviation: one nap or one mis-clustered night must not turn
    // "+-25 min" into "+-150 min". The average sleep duration is shown next to it so the tile can
    // be sanity-checked against the Health app, which only ever shows durations. Needs at least
    // SleepConsistencyMinNights of history.
    private bool _sleepConsistencyVisible;
    private double _sleepConsistencyPercent;
    private string _sleepConsistencyStatus = "";
    private double _sleepConsistencySpreadMinutes;
    private double _sleepAverageDurationMinutes;
    private const int SleepConsistencyMinNights = 14;

    private string SleepConsistencyStatusText => _sleepConsistencyStatus switch
    {
        "good" => T.SleepConsistencyGood ?? "",
        "poor" => T.SleepConsistencyPoor ?? "",
        _ => T.SleepConsistencyModerate ?? "",
    };

    private string SleepConsistencyValueText => string.Format(
        T.SleepConsistencyValueFormat ?? "",
        _sleepConsistencySpreadMinutes.ToString("0"),
        FormatSleepDuration(_sleepAverageDurationMinutes));

    // "6 h 59 min" - unit abbreviations that read the same in all 26 UI languages.
    private static string FormatSleepDuration(double minutes)
    {
        var total = (int)Math.Round(minutes);
        return $"{total / 60} h {total % 60:00} min";
    }

    private void BuildSleepConsistency(IReadOnlyList<SleepNight>? nights)
    {
        _sleepConsistencyVisible = false;
        _sleepConsistencyPercent = 0;
        _sleepConsistencyStatus = "";
        _sleepConsistencySpreadMinutes = 0;
        _sleepAverageDurationMinutes = 0;

        if (nights == null || nights.Count < SleepConsistencyMinNights) return;

        var onsets = new double[nights.Count];
        var durationSum = 0.0;
        for (var i = 0; i < nights.Count; i++)
        {
            onsets[i] = nights[i].OnsetMinutesAfter6pm;
            durationSum += nights[i].DurationMinutes;
        }

        var spread = StudyMetrics.RobustSpread(onsets);

        // 0 min spread -> 100%, 90+ min spread -> 0%.
        _sleepConsistencyPercent = Math.Clamp(100 - spread / 90.0 * 100, 0, 100);
        _sleepConsistencyStatus = spread <= 30 ? "good" : spread >= 60 ? "poor" : "moderate";
        _sleepConsistencySpreadMinutes = spread;
        _sleepAverageDurationMinutes = durationSum / nights.Count;
        _sleepConsistencyVisible = true;
    }
}
