namespace StudyLife.Server.Services;

/// <summary>Named rate-limit policies registered in Program.cs (AddRateLimiter) and applied per
/// action via [EnableRateLimiting] - see the AddPolicy call for the reasoning behind each.</summary>
public static class RateLimitPolicies
{
    /// <summary>Per-user concurrency cap for CPU-heavy work (STT, TTS, AI proxy, JSON export, exam planner).</summary>
    public const string Expensive = "expensive";
}
