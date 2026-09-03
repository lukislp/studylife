using System.Diagnostics.Metrics;

namespace StudyLife.Server.Services;

/// <summary>
/// The server's own OpenTelemetry meter. Everything the framework does not measure by itself
/// (ASP.NET Core, Kestrel, HttpClient, EF Core, Npgsql and the runtime all publish their own
/// meters, see Program.cs) is recorded here: response-cache outcomes, SSE stream counts, TTS
/// synthesis, webhook publishes, worker ticks and the per-pod auth-session cache.
///
/// A static Meter rather than IMeterFactory because the busiest call site (CacheHelper) is a
/// static extension class and the worker records from a hosted service; the OpenTelemetry
/// MeterProvider subscribes by meter NAME, so both styles are observed identically. Instrument
/// names follow the OTel convention (dot-separated, unit-less, the exporter appends
/// `_total`/`_seconds`), so a Prometheus scrape, an OTLP collector, or a future own dashboard
/// reading the Prometheus HTTP API all see the same, documented names. No instrument carries a
/// user id, IP or content - only outcome/result tags with a fixed, small set of values.
/// </summary>
public static class StudyLifeMetrics
{
    public const string MeterName = "StudyLife.Server";

    private static readonly Meter Meter = new(MeterName);

    // Seconds-scale boundaries: the OTel defaults were chosen for milliseconds and would put
    // every value of ours into the first two buckets.
    private static readonly InstrumentAdvice<double> DurationBuckets = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30],
    };

    /// <summary>Tag <c>result</c>: <c>not_modified</c> (ETag matched, bodyless 304),
    /// <c>hit</c> (served from IDistributedCache), <c>miss</c> (factory ran, DB was hit).</summary>
    public static readonly Counter<long> CacheRequests = Meter.CreateCounter<long>(
        "studylife.cache.requests", "{request}",
        "CacheHelper response-cache lookups by result (not_modified/hit/miss)");

    public static readonly Counter<long> SseStreamsStarted = Meter.CreateCounter<long>(
        "studylife.sse.streams.started", "{stream}", "SSE change streams opened (/api/events)");

    public static readonly UpDownCounter<int> SseStreamsOpen = Meter.CreateUpDownCounter<int>(
        "studylife.sse.streams.open", "{stream}", "SSE change streams currently open on this instance");

    /// <summary>Tag <c>result</c>: <c>cache_hit</c>, <c>synthesized</c>, <c>rejected</c>
    /// (note too long), <c>unavailable</c> (no voice for the language / TTS disabled).</summary>
    public static readonly Counter<long> TtsRequests = Meter.CreateCounter<long>(
        "studylife.tts.requests", "{request}", "Text-to-speech requests by result");

    public static readonly Histogram<double> TtsSynthesisDuration = Meter.CreateHistogram<double>(
        "studylife.tts.synthesis.duration", "s", "Wall time of one ONNX synthesis run (cache misses only)", advice: DurationBuckets);

    /// <summary>Tag <c>outcome</c>: <c>ok</c>, <c>http_error</c>, <c>failed</c> (exception/
    /// timeout), <c>dropped</c> (in-flight bound reached).</summary>
    public static readonly Counter<long> WebhookPublishes = Meter.CreateCounter<long>(
        "studylife.webhooks.publishes", "{publish}", "Event publishes to studylife-webhooks by outcome");

    public static readonly Histogram<double> WebhookPublishDuration = Meter.CreateHistogram<double>(
        "studylife.webhooks.publish.duration", "s", "Round-trip of one publish to studylife-webhooks", advice: DurationBuckets);

    public static readonly Histogram<double> WorkerTickDuration = Meter.CreateHistogram<double>(
        "studylife.worker.tick.duration", "s", "Wall time of one BackgroundTaskService tick (all users, all due jobs)", advice: DurationBuckets);

    /// <summary>Tag <c>result</c>: <c>hit</c> / <c>miss</c>.</summary>
    public static readonly Counter<long> AuthSessionCacheLookups = Meter.CreateCounter<long>(
        "studylife.auth.session_cache.lookups", "{lookup}", "Per-pod session-token cache lookups by result");

    public static KeyValuePair<string, object?> Result(string value) => new("result", value);
    public static KeyValuePair<string, object?> Outcome(string value) => new("outcome", value);
}
