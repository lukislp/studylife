namespace StudyLife.Shared;

public static partial class StudyMetrics
{
    /// <summary>
    /// Robust spread of a sample in the sample's own unit: the median absolute deviation scaled
    /// by 1.4826, which estimates the standard deviation for normally distributed data but is
    /// not dragged away by a few outliers. Used for the dashboard's sleep-consistency tile,
    /// where a single nap or a mis-clustered night used to push a plain standard deviation of
    /// bedtimes from ~30 to 150+ minutes. Returns 0 for fewer than two values.
    /// </summary>
    public static double RobustSpread(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        var median = Median(values);
        var deviations = new double[values.Count];
        for (var i = 0; i < values.Count; i++) deviations[i] = Math.Abs(values[i] - median);
        return 1.4826 * Median(deviations);
    }

    /// <summary>Median of the values (mean of the two middle values for even counts).</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = new double[values.Count];
        for (var i = 0; i < values.Count; i++) sorted[i] = values[i];
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
