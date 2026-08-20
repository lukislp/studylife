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
    private const int ReadinessMinSamples = 14;

    private string ReadinessStatusText => _readinessStatus switch
    {
        "above" => T.ReadinessAboveBaseline ?? "",
        "below" => T.ReadinessBelowBaseline ?? "",
        _ => T.ReadinessAroundBaseline ?? "",
    };

    private void BuildReadinessScore(IReadOnlyList<double>? hrvSamples)
    {
        _readinessVisible = false;
        _readinessPercent = 0;
        _readinessStatus = "";

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
        _readinessVisible = true;
    }
}
