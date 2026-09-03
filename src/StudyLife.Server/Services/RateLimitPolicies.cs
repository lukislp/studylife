namespace StudyLife.Server.Services;

/// <summary>Named rate-limit policies registered in Program.cs (AddRateLimiter) and applied per
/// action via [EnableRateLimiting] - see the AddPolicy call for the reasoning behind each.</summary>
public static class RateLimitPolicies
{
    /// <summary>Per-user concurrency cap for CPU-heavy work (STT, TTS, AI proxy, JSON export, exam planner).</summary>
    public const string Expensive = "expensive";

    /// <summary>Per-user throughput cap for POST /api/telemetry (30 batches/minute) - a buggy or
    /// misbehaving client flushing far more often than the normal 20s/25-event cadence shouldn't
    /// be able to spam the meter/log pipeline, see TelemetryController.</summary>
    public const string Telemetry = "telemetry";
}
