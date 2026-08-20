namespace StudyLife.Client.Services;

/// <summary>
/// Additive hook for Apple Health data (same pattern as INativePush/INativeAppAuth/
/// INativeFileExport): HealthKit is iOS-only and the data never leaves the device - the
/// Dashboard's readiness-score tile (Index.Health.razor.cs) simply stays hidden in the
/// browser/PWA and on every non-iOS platform, exactly like the other native-only features.
/// </summary>
public interface INativeHealthData
{
    bool IsAvailable => false;

    /// <summary>Daily HRV (SDNN, ms) for the last <paramref name="days"/> days, most recent
    /// last, one entry per day with a sample (gaps simply absent, not zero-filled). Null if
    /// authorization was never granted/denied.</summary>
    Task<IReadOnlyList<double>?> GetRecentHrvAsync(int days) => Task.FromResult<IReadOnlyList<double>?>(null);
}

/// <summary>Default registration in the browser client (Program.cs).</summary>
public sealed class NoNativeHealthData : INativeHealthData
{
}
