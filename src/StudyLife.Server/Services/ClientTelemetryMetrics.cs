using System.Diagnostics.Metrics;

namespace StudyLife.Server.Services;

/// <summary>
/// Phase 2 of the telemetry plan (docs/ARCHITECTURE.md "Telemetry"): everything CLIENTS report
/// about themselves (boot timeline, Web Vitals, API timings, SSE health, navigation renders,
/// errors, native app launch/resource/health-query/push events) via POST /api/telemetry - see
/// TelemetryController for the consent/demo gating and the event-to-instrument mapping. A
/// separate meter from StudyLifeMetrics (StudyLife.Server, phase 1: what the SERVER measures
/// about itself) so "everything the clients reported" can be queried/dashboarded as one group,
/// even though both ultimately export through the same Prometheus scrape surface.
///
/// Tag cardinality is deliberately narrow (docs/ARCHITECTURE.md contract table): route/page/kind/
/// type/event values are either a small fixed enumeration or normalized against the server's own
/// route table (TelemetryRouteCatalog) before they ever reach an instrument - nothing here is a
/// free-form user string, and nothing carries a user id, IP, or content.
/// </summary>
public static class ClientTelemetryMetrics
{
    public const string MeterName = "StudyLife.Client";

    private static readonly Meter Meter = new(MeterName);

    // Seconds-scale boundaries, same rationale as StudyLifeMetrics.DurationBuckets: client timings
    // (boot phases, API calls) are typically tens of ms to a few seconds, never sub-millisecond.
    private static readonly InstrumentAdvice<double> DurationBuckets = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30],
    };

    // ── boot ─────────────────────────────────────────────────────────────────
    public static readonly Histogram<double> BootHtmlDuration = Meter.CreateHistogram<double>(
        "studylife.client.boot.html.duration", "s", "Time to first HTML byte/parse on cold/warm boot", advice: DurationBuckets);
    public static readonly Histogram<double> BootScriptDuration = Meter.CreateHistogram<double>(
        "studylife.client.boot.boot_script.duration", "s", "Time spent in the boot-loading overlay's own script", advice: DurationBuckets);
    public static readonly Histogram<double> BootWasmDownloadDuration = Meter.CreateHistogram<double>(
        "studylife.client.boot.wasm_download.duration", "s", "Blazor WASM asset download time", advice: DurationBuckets);
    public static readonly Histogram<double> BootRuntimeReadyDuration = Meter.CreateHistogram<double>(
        "studylife.client.boot.runtime_ready.duration", "s", "Time until the Mono/WASM runtime is ready", advice: DurationBuckets);
    public static readonly Histogram<double> BootFirstRenderDuration = Meter.CreateHistogram<double>(
        "studylife.client.boot.first_render.duration", "s", "Time to the first Blazor render", advice: DurationBuckets);
    public static readonly Histogram<double> BootDashboardReadyDuration = Meter.CreateHistogram<double>(
        "studylife.client.boot.dashboard_ready.duration", "s", "Time until the dashboard has real data (not just the shell)", advice: DurationBuckets);
    public static readonly Histogram<double> BootDownloadBytes = Meter.CreateHistogram<double>(
        "studylife.client.boot.download.bytes", "By", "Total bytes downloaded for one boot");
    /// <summary>Tags: <c>cold</c>, <c>sw_cache_hit</c>, <c>platform</c>, <c>app_version</c>,
    /// <c>language</c> - the only client instrument besides errors allowed appVersion/language
    /// tags (contract cardinality rule).</summary>
    public static readonly Counter<long> Boots = Meter.CreateCounter<long>(
        "studylife.client.boots", "{boot}", "Client app boots, one per flushed boot event");

    // ── vitals ───────────────────────────────────────────────────────────────
    public static readonly Histogram<double> VitalsTtfb = Meter.CreateHistogram<double>(
        "studylife.client.vitals.ttfb", "ms", "Time to first byte (Navigation Timing)");
    public static readonly Histogram<double> VitalsFcp = Meter.CreateHistogram<double>(
        "studylife.client.vitals.fcp", "ms", "First Contentful Paint");
    public static readonly Histogram<double> VitalsLcp = Meter.CreateHistogram<double>(
        "studylife.client.vitals.lcp", "ms", "Largest Contentful Paint");
    public static readonly Histogram<double> VitalsInp = Meter.CreateHistogram<double>(
        "studylife.client.vitals.inp", "ms", "Interaction to Next Paint");
    public static readonly Histogram<double> VitalsCls = Meter.CreateHistogram<double>(
        "studylife.client.vitals.cls", "1", "Cumulative Layout Shift (unitless ratio, not ms)");

    // ── api ──────────────────────────────────────────────────────────────────
    /// <summary>Tags: <c>route</c> (normalized template or "other"), <c>method</c>,
    /// <c>status_class</c> (2xx/3xx/4xx/5xx), <c>platform</c>.</summary>
    public static readonly Histogram<double> ApiDuration = Meter.CreateHistogram<double>(
        "studylife.client.api.duration", "s", "Client-observed round-trip of one API call", advice: DurationBuckets);
    /// <summary>Tags: <c>route</c>, <c>not_modified</c>, <c>platform</c>.</summary>
    public static readonly Counter<long> ApiRequests = Meter.CreateCounter<long>(
        "studylife.client.api.requests", "{request}", "Client-observed API calls by route/cache outcome");

    // ── sse ──────────────────────────────────────────────────────────────────
    /// <summary>Tag <c>event</c>: connected/reconnect/fallback_poll/closed.</summary>
    public static readonly Counter<long> SseEvents = Meter.CreateCounter<long>(
        "studylife.client.sse.events", "{event}", "Client-observed SSE change-stream lifecycle events");
    public static readonly Histogram<double> SseDuration = Meter.CreateHistogram<double>(
        "studylife.client.sse.duration", "s", "Time to connect, or time spent disconnected before reconnecting", advice: DurationBuckets);

    // ── navigation ───────────────────────────────────────────────────────────
    /// <summary>Tag <c>page</c>: the Blazor route template, as sent (not matched against
    /// EndpointDataSource - that only applies to the server API's own <c>route</c> tag).</summary>
    public static readonly Histogram<double> NavigationRenderDuration = Meter.CreateHistogram<double>(
        "studylife.client.navigation.render.duration", "s", "Time from NavigationManager.LocationChanged to the next render", advice: DurationBuckets);

    // ── errors ───────────────────────────────────────────────────────────────
    /// <summary>Tags: <c>kind</c> (dotnet/js/native_crash/native_hang/native_anr), <c>type</c>
    /// (exception type name), <c>fatal</c>, <c>platform</c>, <c>app_version</c> - the only client
    /// instrument besides boots allowed an appVersion tag. Never sampled out (always sent).</summary>
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>(
        "studylife.client.errors", "{error}", "Client-reported errors/crashes by kind/type/fatal");

    // ── native app ───────────────────────────────────────────────────────────
    public static readonly Histogram<double> AppLaunchColdDuration = Meter.CreateHistogram<double>(
        "studylife.client.app.launch.cold.duration", "s", "Native app cold launch time", advice: DurationBuckets);
    public static readonly Histogram<double> AppLaunchWarmDuration = Meter.CreateHistogram<double>(
        "studylife.client.app.launch.warm.duration", "s", "Native app warm launch time", advice: DurationBuckets);
    public static readonly Histogram<double> AppLaunchWebviewDuration = Meter.CreateHistogram<double>(
        "studylife.client.app.launch.webview.duration", "s", "Time until the embedded Blazor WebView is interactive", advice: DurationBuckets);

    public static readonly Histogram<double> AppResourcePeakMemoryMb = Meter.CreateHistogram<double>(
        "studylife.client.app.resource.peak_memory_mb", "MBy", "MetricKit daily peak memory");
    public static readonly Histogram<double> AppResourceCpuSeconds = Meter.CreateHistogram<double>(
        "studylife.client.app.resource.cpu_seconds", "s", "MetricKit daily CPU time");
    public static readonly Histogram<double> AppResourceCellularBytes = Meter.CreateHistogram<double>(
        "studylife.client.app.resource.cellular_bytes", "By", "MetricKit daily cellular network usage");
    public static readonly Histogram<double> AppResourceWifiBytes = Meter.CreateHistogram<double>(
        "studylife.client.app.resource.wifi_bytes", "By", "MetricKit daily WiFi network usage");

    // ── health queries (never a sample/value count - see the contract's explicit warning) ──────
    /// <summary>Tag <c>kind</c>: hrv/sleep/steps/vo2max.</summary>
    public static readonly Histogram<double> HealthQueryDuration = Meter.CreateHistogram<double>(
        "studylife.client.health.query.duration", "s", "HealthKit query wall time", advice: DurationBuckets);
    /// <summary>Tags: <c>kind</c>, <c>authorization</c> (granted/denied/undetermined), <c>result</c>
    /// (error/empty/below_minimum/sufficient), <c>outlier_filtered</c>. Never a sample/night count
    /// or any health value - see docs/ARCHITECTURE.md.</summary>
    public static readonly Counter<long> HealthQueries = Meter.CreateCounter<long>(
        "studylife.client.health.queries", "{query}", "HealthKit queries by kind/authorization/result");

    // ── push ─────────────────────────────────────────────────────────────────
    /// <summary>Tag <c>event</c>: registered/received/shown.</summary>
    public static readonly Counter<long> PushEvents = Meter.CreateCounter<long>(
        "studylife.client.push.events", "{event}", "Native push lifecycle events");
    public static readonly Histogram<double> PushLatency = Meter.CreateHistogram<double>(
        "studylife.client.push.latency", "ms", "Time from APNs delivery to the notification being shown");

    // ── dropped/unrecognized events ──────────────────────────────────────────
    /// <summary>Tag <c>reason</c>: unknown_type (Type doesn't match any of the ten known event
    /// types) or invalid (a numeric field that must be non-negative wasn't).</summary>
    public static readonly Counter<long> EventsDropped = Meter.CreateCounter<long>(
        "studylife.client.events.dropped", "{event}", "Telemetry events that couldn't be mapped to an instrument");

    public static KeyValuePair<string, object?> Platform(string value) => new("platform", value);
    public static KeyValuePair<string, object?> Cold(bool value) => new("cold", value);
    public static KeyValuePair<string, object?> SwCacheHit(bool value) => new("sw_cache_hit", value);
    public static KeyValuePair<string, object?> AppVersion(string value) => new("app_version", value);
    public static KeyValuePair<string, object?> Language(string value) => new("language", value);
    public static KeyValuePair<string, object?> Route(string value) => new("route", value);
    public static KeyValuePair<string, object?> Method(string value) => new("method", value);
    public static KeyValuePair<string, object?> StatusClass(string value) => new("status_class", value);
    public static KeyValuePair<string, object?> NotModified(bool value) => new("not_modified", value);
    public static KeyValuePair<string, object?> Event(string value) => new("event", value);
    public static KeyValuePair<string, object?> Page(string value) => new("page", value);
    public static KeyValuePair<string, object?> Kind(string value) => new("kind", value);
    public static KeyValuePair<string, object?> Type(string value) => new("type", value);
    public static KeyValuePair<string, object?> Fatal(bool value) => new("fatal", value);
    public static KeyValuePair<string, object?> AuthorizationTag(string value) => new("authorization", value);
    public static KeyValuePair<string, object?> Result(string value) => new("result", value);
    public static KeyValuePair<string, object?> OutlierFiltered(bool value) => new("outlier_filtered", value);
    public static KeyValuePair<string, object?> Reason(string value) => new("reason", value);
}
