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

    /// <summary>Sleep onset time for the last <paramref name="nights"/> nights, most recent
    /// last, as minutes after 6pm (wrapping at 24h) - e.g. 23:30 is 330, 01:15 is 450. This
    /// anchor avoids circular-statistics complexity for the normal bedtime range (21:00-03:00)
    /// at the cost of being a poor fit for genuinely unusual sleep schedules (e.g. a night-shift
    /// worker sleeping at noon) - acceptable for a v1 consistency signal. One entry per night
    /// with a detected sleep session (gaps simply absent). Null if authorization was never
    /// granted/denied.</summary>
    Task<IReadOnlyList<double>?> GetRecentSleepOnsetMinutesAsync(int nights) => Task.FromResult<IReadOnlyList<double>?>(null);

    /// <summary>Step count over the last <paramref name="minutesAgo"/> minutes up to now - used
    /// by the Focus Timer's movement-break nudge (OnFocusMilestone) to check whether the user
    /// has moved at all during a long uninterrupted focus stretch. Null if authorization was
    /// never granted/denied (distinct from a genuine 0 steps).</summary>
    Task<int?> GetStepsSinceAsync(int minutesAgo) => Task.FromResult<int?>(null);
}

/// <summary>Default registration in the browser client (Program.cs).</summary>
public sealed class NoNativeHealthData : INativeHealthData
{
}
