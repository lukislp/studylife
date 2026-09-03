namespace StudyLife.Client.Services;

/// <summary>
/// A single cardio-fitness (VO2max) reading, as returned by <see cref="INativeHealthData.GetCardioFitnessPointsAsync"/>.
/// A record struct rather than a value tuple: LINQ generic-instantiated over a value-tuple
/// element type has reproducibly crashed the MAUI app's Mono AOT compiler on iOS (SIGABRT at
/// startup, from the method merely being AOT-compiled, not from ever being called - see
/// Stats.Health.razor.cs's BuildCardioFitnessTrend for the original writeup, which had to avoid
/// LINQ entirely over the old <c>(DateTime Date, double Vo2Max)</c> tuple to work around it). A
/// record struct doesn't hit that gsharedvt code-generation bug, so consumers are free to use
/// normal LINQ again once they're on this type.
/// </summary>
public readonly record struct CardioFitnessPoint(DateTime Date, double Vo2Max);

/// <summary>
/// One night's main sleep, as returned by <see cref="INativeHealthData.GetRecentSleepNightsAsync"/>:
/// onset as minutes after 6pm (wrapping at 24h, same anchor as the older onset-only API) and the
/// total asleep duration in minutes. A record struct for the same Mono AOT reason as
/// <see cref="CardioFitnessPoint"/>.
/// </summary>
public readonly record struct SleepNight(double OnsetMinutesAfter6pm, double DurationMinutes);

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

    /// <summary>One entry per night with a detected MAIN sleep in the last <paramref name="nights"/>
    /// nights, oldest first. Supersedes <see cref="GetRecentSleepOnsetMinutesAsync"/> for the
    /// dashboard: that older bridge split a night at every gap of more than an hour between
    /// asleep samples and also counted daytime naps as "nights", so a single nap or a long
    /// nocturnal wake produced onset values hours away from the real bedtime and a spread of
    /// 150+ minutes for a perfectly regular sleeper. Implementations must return exactly one
    /// session per sleep day (the longest one) and drop anything shorter than a real night's
    /// sleep. Null if authorization was never granted/denied.</summary>
    Task<IReadOnlyList<SleepNight>?> GetRecentSleepNightsAsync(int nights) => Task.FromResult<IReadOnlyList<SleepNight>?>(null);

    /// <summary>Step count over the last <paramref name="minutesAgo"/> minutes up to now - used
    /// by the Focus Timer's movement-break nudge (OnFocusMilestone) to check whether the user
    /// has moved at all during a long uninterrupted focus stretch. Null if authorization was
    /// never granted/denied (distinct from a genuine 0 steps).</summary>
    Task<int?> GetStepsSinceAsync(int minutesAgo) => Task.FromResult<int?>(null);

    /// <summary>Cardio Fitness (VO2max, ml/(kg·min)) history for the last <paramref name="days"/>
    /// days, oldest first - unlike HRV/sleep, watchOS computes these roughly monthly (from
    /// outdoor walk/run workouts), so readings are sparse rather than daily. Null if
    /// authorization was never granted/denied, or if there simply are no readings in the
    /// window (e.g. no Watch workout history) - the Stats page card treats both the same way.</summary>
    Task<IReadOnlyList<CardioFitnessPoint>?> GetCardioFitnessPointsAsync(int days) =>
        Task.FromResult<IReadOnlyList<CardioFitnessPoint>?>(null);
}

/// <summary>Default registration in the browser client (Program.cs).</summary>
public sealed class NoNativeHealthData : INativeHealthData
{
}
