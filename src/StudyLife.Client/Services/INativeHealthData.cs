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

    /// <summary>
    /// Value-tuple variant kept for backward compatibility with cross-repo implementers still
    /// mid-migration (see the MAUI app's ProjectReference to this repo's main branch in CI) -
    /// prefer overriding <see cref="GetCardioFitnessPointsAsync"/> instead, which this repo's own
    /// consumers now use exclusively. Do not add new callers/implementers of this member; it
    /// exists purely so old implementations keep compiling until they're migrated.
    /// </summary>
    [Obsolete("Implement/call GetCardioFitnessPointsAsync instead - value tuples in LINQ pipelines can crash iOS AOT compilation.", error: false)]
    Task<IReadOnlyList<(DateTime Date, double Vo2Max)>?> GetCardioFitnessHistoryAsync(int days) =>
        Task.FromResult<IReadOnlyList<(DateTime Date, double Vo2Max)>?>(null);

    /// <summary>Cardio Fitness (VO2max, ml/(kg·min)) history for the last <paramref name="days"/>
    /// days, oldest first - unlike HRV/sleep, watchOS computes these roughly monthly (from
    /// outdoor walk/run workouts), so readings are sparse rather than daily. Null if
    /// authorization was never granted/denied, or if there simply are no readings in the
    /// window (e.g. no Watch workout history) - the Stats page card treats both the same way.
    /// <para>
    /// Default implementation adapts <see cref="GetCardioFitnessHistoryAsync"/> so that any
    /// implementer which only overrides the old tuple-based member (e.g. the MAUI app's
    /// NativeHealthData, until it migrates) keeps working unchanged through this new member too -
    /// override this one directly instead once the old member is gone.
    /// </para></summary>
#pragma warning disable CS0618 // deliberate bridge to the obsolete member, for old implementers
    async Task<IReadOnlyList<CardioFitnessPoint>?> GetCardioFitnessPointsAsync(int days)
    {
        var legacy = await GetCardioFitnessHistoryAsync(days);
        return legacy?.Select(p => new CardioFitnessPoint(p.Date, p.Vo2Max)).ToList();
    }
#pragma warning restore CS0618
}

/// <summary>Default registration in the browser client (Program.cs).</summary>
public sealed class NoNativeHealthData : INativeHealthData
{
}
