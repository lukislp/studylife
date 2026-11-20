// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// POST /api/telemetry - phase 2 of the telemetry plan (docs/ARCHITECTURE.md "Telemetry"): the
/// client-beacon counterpart to phase 1's server-only StudyLifeMetrics. SessionOnly (a bare API
/// key must not be able to spend the per-user rate-limit bucket or write logs on the account's
/// behalf) plus a 32 KB/50-event size guard and a dedicated 30/min rate limit - a misbehaving
/// client flushing far more often than its normal 20s/25-event cadence must not be able to spam
/// the meter or the log pipeline.
///
/// Always answers 204 on anything that parses (never surfaces a metrics-pipeline detail to the
/// client) EXCEPT the size/shape/rate guards above, which answer their own normal status codes -
/// consistent with "telemetry must never affect the app's own behavior".
/// </summary>
[ApiController]
[Route("api/telemetry")]
[Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(RateLimitPolicies.Telemetry)]
public class TelemetryController : ControllerBase
{
    private const long MaxBodyBytes = 32 * 1024;

    private static readonly HashSet<string> KnownPlatforms =
        new(StringComparer.OrdinalIgnoreCase) { "web", "ios", "android", "windows", "maccatalyst" };

    private readonly StudyLifeDb _db;
    private readonly IConfiguration _config;
    private readonly TelemetryRouteCatalog _routeCatalog;
    private readonly ILogger<TelemetryController> _logger;

    public TelemetryController(StudyLifeDb db, IConfiguration config, TelemetryRouteCatalog routeCatalog, ILogger<TelemetryController> logger)
    {
        _db = db;
        _config = config;
        _routeCatalog = routeCatalog;
        _logger = logger;
    }

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [RejectOversizedBody(MaxBodyBytes)]
    public async Task<IActionResult> Post([FromBody] TelemetryBatchDto batch)
    {
        // Defense in depth alongside [RequestSizeLimit]/[RejectOversizedBody] above - same
        // pattern as BackupDataService.ImportJsonAsync.
        if (Request.ContentLength is { } contentLength && contentLength > MaxBodyBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "Request body is too large (max 32 KB)." });

        // Public demo instances never persist telemetry - same "always 204, nothing recorded"
        // shape as a genuine opt-out below, so the demo client's beacon never even notices it's
        // being dropped.
        if (DemoModeGuard.IsEnabled(_config))
            return NoContent();

        // Consent gate (UserSettingsEntity.TelemetryConsent): only an explicit `true` records
        // anything - both "undecided" (null, the modal hasn't been answered) and an explicit
        // decline drop the whole batch silently. AsNoTracking probe, no write happens on this path.
        var consent = await _db.Settings.AsNoTracking().Select(s => s.TelemetryConsent).FirstOrDefaultAsync();
        if (consent != true)
            return NoContent();

        var platform = SanitizeEnum(batch.Platform, KnownPlatforms, "unknown");
        var appVersion = SanitizeShape(batch.AppVersion, AppVersionShape, "unknown");
        var language = SanitizeShape(batch.Language, LanguageShape, "unknown");
        var platformTag = ClientTelemetryMetrics.Platform(platform);

        foreach (var ev in batch.Events)
            RecordEvent(ev, platform, platformTag, appVersion, language, batch.SessionId);

        return NoContent();
    }

    private void RecordEvent(TelemetryEventDto ev, string platform, KeyValuePair<string, object?> platformTag, string appVersion, string language, string sessionId)
    {
        switch (ev.Type)
        {
            case "boot":
                RecordBoot(ev, platformTag, appVersion, language);
                break;
            case "vitals":
                RecordVitals(ev, platformTag);
                break;
            case "api":
                RecordApi(ev, platformTag);
                break;
            case "sse":
                RecordSse(ev, platformTag);
                break;
            case "navigation":
                RecordNavigation(ev, platformTag);
                break;
            case "error":
                RecordError(ev, platform, platformTag, appVersion, sessionId);
                break;
            case "app_launch":
                RecordAppLaunch(ev, platformTag);
                break;
            case "app_resource":
                RecordAppResource(ev, platformTag);
                break;
            case "health_query":
                RecordHealthQuery(ev, platformTag);
                break;
            case "push":
                RecordPush(ev, platformTag);
                break;
            default:
                ClientTelemetryMetrics.EventsDropped.Add(1, ClientTelemetryMetrics.Reason("unknown_type"));
                break;
        }
    }

    private static void RecordBoot(TelemetryEventDto ev, KeyValuePair<string, object?> platformTag, string appVersion, string language)
    {
        var coldTag = ClientTelemetryMetrics.Cold(ev.Cold ?? false);
        if (ev.HtmlMs is double htmlMs && htmlMs >= 0) ClientTelemetryMetrics.BootHtmlDuration.Record(htmlMs / 1000.0, coldTag, platformTag);
        if (ev.BootScriptMs is double bootScriptMs && bootScriptMs >= 0) ClientTelemetryMetrics.BootScriptDuration.Record(bootScriptMs / 1000.0, coldTag, platformTag);
        if (ev.WasmDownloadMs is double wasmMs && wasmMs >= 0) ClientTelemetryMetrics.BootWasmDownloadDuration.Record(wasmMs / 1000.0, coldTag, platformTag);
        if (ev.RuntimeReadyMs is double runtimeMs && runtimeMs >= 0) ClientTelemetryMetrics.BootRuntimeReadyDuration.Record(runtimeMs / 1000.0, coldTag, platformTag);
        if (ev.FirstRenderMs is double firstRenderMs && firstRenderMs >= 0) ClientTelemetryMetrics.BootFirstRenderDuration.Record(firstRenderMs / 1000.0, coldTag, platformTag);
        if (ev.DashboardReadyMs is double dashboardMs && dashboardMs >= 0) ClientTelemetryMetrics.BootDashboardReadyDuration.Record(dashboardMs / 1000.0, coldTag, platformTag);
        if (ev.DownloadBytes is double bytes && bytes >= 0) ClientTelemetryMetrics.BootDownloadBytes.Record(bytes, platformTag);

        ClientTelemetryMetrics.Boots.Add(1, coldTag,
            ClientTelemetryMetrics.SwCacheHit(ev.SwCacheHit ?? false),
            platformTag,
            ClientTelemetryMetrics.AppVersion(appVersion),
            ClientTelemetryMetrics.Language(language));
    }

    private static void RecordVitals(TelemetryEventDto ev, KeyValuePair<string, object?> platformTag)
    {
        if (ev.Ttfb is double ttfb && ttfb >= 0) ClientTelemetryMetrics.VitalsTtfb.Record(ttfb, platformTag);
        if (ev.Fcp is double fcp && fcp >= 0) ClientTelemetryMetrics.VitalsFcp.Record(fcp, platformTag);
        if (ev.Lcp is double lcp && lcp >= 0) ClientTelemetryMetrics.VitalsLcp.Record(lcp, platformTag);
        if (ev.Inp is double inp && inp >= 0) ClientTelemetryMetrics.VitalsInp.Record(inp, platformTag);
        if (ev.Cls is double cls && cls >= 0) ClientTelemetryMetrics.VitalsCls.Record(cls, platformTag);
    }

    private void RecordApi(TelemetryEventDto ev, KeyValuePair<string, object?> platformTag)
    {
        var routeTag = ClientTelemetryMetrics.Route(_routeCatalog.Normalize(ev.Route));
        var methodTag = ClientTelemetryMetrics.Method(SanitizeEnum(ev.Method?.ToUpperInvariant(), KnownMethods, "unknown"));
        var notModifiedTag = ClientTelemetryMetrics.NotModified(ev.NotModified ?? false);

        if (ev.DurationMs is double durationMs && durationMs >= 0)
        {
            var statusClass = ev.Status is >= 100 and < 600 ? $"{ev.Status / 100}xx" : "unknown";
            ClientTelemetryMetrics.ApiDuration.Record(durationMs / 1000.0, routeTag, methodTag,
                new KeyValuePair<string, object?>("status_class", statusClass), platformTag);
        }
        ClientTelemetryMetrics.ApiRequests.Add(1, routeTag, notModifiedTag, platformTag);
    }

    private static void RecordSse(TelemetryEventDto ev, KeyValuePair<string, object?> platformTag)
    {
        var eventTag = ClientTelemetryMetrics.Event(SanitizeEnum(ev.Event, SseEventKinds, "unknown"));
        ClientTelemetryMetrics.SseEvents.Add(1, eventTag, platformTag);
        if (ev.DurationMs is double durationMs && durationMs >= 0)
            ClientTelemetryMetrics.SseDuration.Record(durationMs / 1000.0, eventTag, platformTag);
    }

    private static void RecordNavigation(TelemetryEventDto ev, KeyValuePair<string, object?> platformTag)
    {
        if (ev.RenderMs is not double renderMs || renderMs < 0) return;
        var pageTag = ClientTelemetryMetrics.Page(SanitizePage(ev.Page));
        ClientTelemetryMetrics.NavigationRenderDuration.Record(renderMs / 1000.0, pageTag, platformTag);
    }

    private void RecordError(TelemetryEventDto ev, string platform, KeyValuePair<string, object?> platformTag, string appVersion, string sessionId)
    {
        var kind = SanitizeEnum(ev.Kind, ErrorKinds, "unknown");
        var errorType = SanitizeErrorType(ev.ErrorType);
        var fatal = ev.Fatal ?? false;

        ClientTelemetryMetrics.Errors.Add(1,
            ClientTelemetryMetrics.Kind(kind),
            ClientTelemetryMetrics.Type(errorType),
            ClientTelemetryMetrics.Fatal(fatal),
            platformTag,
            ClientTelemetryMetrics.AppVersion(appVersion));

        // Structured "ClientError" log event (contract: kept 14 days in Loki) - deliberately
        // never the auth user id, only the client-generated, non-identifying sessionId. Every
        // value here is either allow-listed above or run through ForLog: a client-supplied
        // newline must not be able to forge a second log record (2026-09-11 audit, CodeQL
        // cs/log-forging). The stack keeps its line breaks but every continuation line is
        // folded onto one line and length-capped (audit L11), so neither a continuation line nor an
        // oversized stack can add or flood log records.
        _logger.LogInformation(
            "ClientError kind={Kind} type={Type} stackHash={StackHash} platform={Platform} appVersion={AppVersion} sessionId={SessionId} page={Page} stack={Stack}",
            kind, errorType, ForLog(ev.StackHash), platform, appVersion, ForLog(sessionId), SanitizePage(ev.Page), ForLogSingleLine(ev.Stack, MaxLoggedStackLength));
    }

    private static void RecordAppLaunch(TelemetryEventDto ev, KeyValuePair<string, object?> platformTag)
    {
        if (ev.ColdMs is double coldMs && coldMs >= 0) ClientTelemetryMetrics.AppLaunchColdDuration.Record(coldMs / 1000.0, platformTag);
        if (ev.WarmMs is double warmMs && warmMs >= 0) ClientTelemetryMetrics.AppLaunchWarmDuration.Record(warmMs / 1000.0, platformTag);
        if (ev.WebviewReadyMs is double webviewMs && webviewMs >= 0) ClientTelemetryMetrics.AppLaunchWebviewDuration.Record(webviewMs / 1000.0, platformTag);
    }

    private static void RecordAppResource(TelemetryEventDto ev, KeyValuePair<string, object?> platformTag)
    {
        if (ev.PeakMemoryMb is double peakMemory && peakMemory >= 0) ClientTelemetryMetrics.AppResourcePeakMemoryMb.Record(peakMemory, platformTag);
        if (ev.CpuSeconds is double cpuSeconds && cpuSeconds >= 0) ClientTelemetryMetrics.AppResourceCpuSeconds.Record(cpuSeconds, platformTag);
        if (ev.CellularBytes is double cellularBytes && cellularBytes >= 0) ClientTelemetryMetrics.AppResourceCellularBytes.Record(cellularBytes, platformTag);
        if (ev.WifiBytes is double wifiBytes && wifiBytes >= 0) ClientTelemetryMetrics.AppResourceWifiBytes.Record(wifiBytes, platformTag);
    }

    private static void RecordHealthQuery(TelemetryEventDto ev, KeyValuePair<string, object?> platformTag)
    {
        var kindTag = ClientTelemetryMetrics.Kind(SanitizeEnum(ev.Kind, HealthQueryKinds, "unknown"));
        ClientTelemetryMetrics.HealthQueries.Add(1,
            kindTag,
            ClientTelemetryMetrics.AuthorizationTag(SanitizeEnum(ev.Authorization, HealthAuthorizationValues, "unknown")),
            ClientTelemetryMetrics.Result(SanitizeEnum(ev.Result, HealthResultValues, "unknown")),
            ClientTelemetryMetrics.OutlierFiltered(ev.OutlierFiltered ?? false),
            platformTag);
        if (ev.DurationMs is double durationMs && durationMs >= 0)
            ClientTelemetryMetrics.HealthQueryDuration.Record(durationMs / 1000.0, kindTag, platformTag);
    }

    private static void RecordPush(TelemetryEventDto ev, KeyValuePair<string, object?> platformTag)
    {
        var eventTag = ClientTelemetryMetrics.Event(SanitizeEnum(ev.Event, PushEventKinds, "unknown"));
        ClientTelemetryMetrics.PushEvents.Add(1, eventTag, platformTag);
        if (ev.LatencyMs is double latencyMs && latencyMs >= 0)
            ClientTelemetryMetrics.PushLatency.Record(latencyMs, eventTag, platformTag);
    }

    private static readonly HashSet<string> SseEventKinds =
        new(StringComparer.OrdinalIgnoreCase) { "connected", "reconnect", "fallback_poll", "closed" };
    private static readonly HashSet<string> ErrorKinds =
        new(StringComparer.OrdinalIgnoreCase) { "dotnet", "js", "native_crash", "native_hang", "native_anr" };
    private static readonly HashSet<string> HealthQueryKinds =
        new(StringComparer.OrdinalIgnoreCase) { "hrv", "sleep", "steps", "vo2max" };
    private static readonly HashSet<string> HealthAuthorizationValues =
        new(StringComparer.OrdinalIgnoreCase) { "granted", "denied", "undetermined" };
    // "ok"/"error" is the current contract (app #234); empty/below_minimum/sufficient and the
    // authorization field are what older app builds still send, so they stay accepted.
    private static readonly HashSet<string> HealthResultValues =
        new(StringComparer.OrdinalIgnoreCase) { "ok", "error", "empty", "below_minimum", "sufficient" };
    private static readonly HashSet<string> PushEventKinds =
        new(StringComparer.OrdinalIgnoreCase) { "registered", "received", "shown" };

    private static string SanitizeEnum(string? value, HashSet<string> allowed, string fallback) =>
        value is { Length: > 0 } && allowed.Contains(value) ? value : fallback;

    // Bounded shapes for the tag values that have no closed enum (2026-09-11 audit, finding 7):
    // a client that sends 50 unique values per batch, 30 batches a minute, would otherwise grow
    // the in-process meter state and the Prometheus series set without limit. ClientTelemetryMetrics'
    // "nothing here is a free-form user string" contract holds again with these in place.
    private static readonly HashSet<string> KnownMethods =
        new(StringComparer.Ordinal) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
    private static readonly System.Text.RegularExpressions.Regex AppVersionShape =
        new(@"^[0-9]{1,5}(\.[0-9]{1,5}){0,3}([-+][A-Za-z0-9.]{1,20})?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LanguageShape =
        new(@"^[a-z]{2,3}(-[A-Za-z]{2,4})?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    /// <summary>Exception types worth their own series; every other client-invented name is the
    /// "other" bucket, so the label set stays closed (audit L11).</summary>
    private static readonly HashSet<string> KnownErrorTypes = new(StringComparer.Ordinal)
    {
        "Error", "TypeError", "ReferenceError", "SyntaxError", "RangeError", "EvalError", "URIError",
        "AggregateError", "DOMException", "NetworkError", "AbortError", "SecurityError", "QuotaExceededError",
        "System.Exception", "System.InvalidOperationException", "System.ArgumentException",
        "System.ArgumentNullException", "System.ArgumentOutOfRangeException", "System.NullReferenceException",
        "System.InvalidCastException", "System.FormatException", "System.IndexOutOfRangeException",
        "System.Collections.Generic.KeyNotFoundException", "System.NotSupportedException",
        "System.NotImplementedException", "System.ObjectDisposedException",
        "System.OperationCanceledException", "System.Threading.Tasks.TaskCanceledException",
        "System.TimeoutException", "System.UnauthorizedAccessException", "System.OutOfMemoryException",
        "System.AggregateException", "System.IO.IOException", "System.IO.FileNotFoundException",
        "System.Net.Http.HttpRequestException", "System.Text.Json.JsonException",
        "Microsoft.JSInterop.JSException", "Microsoft.JSInterop.JSDisconnectedException",
        "Microsoft.AspNetCore.Components.NavigationException",
    };

    /// <summary>The first path segment of every routable Blazor page, read from the page
    /// components' own [Route] attributes so the list cannot drift from the app.</summary>
    private static readonly HashSet<string> KnownPageSegments = typeof(StudyLife.Client.Services.AppStateService).Assembly
        .GetTypes()
        .SelectMany(t => t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), inherit: false))
        .Cast<Microsoft.AspNetCore.Components.RouteAttribute>()
        .Select(r => FirstSegment(r.Template))
        .ToHashSet(StringComparer.Ordinal);

    private const int MaxLoggedStackLength = 2000;

    internal static string SanitizeErrorType(string? type) => SanitizeEnum(type, KnownErrorTypes, "other");

    private static string SanitizeShape(string? value, System.Text.RegularExpressions.Regex shape, string fallback) =>
        value is { Length: > 0 } && shape.IsMatch(value) ? value : fallback;

    /// <summary>A page is reported by its first path segment only ("/notes/123?x=1" -> "/notes"):
    /// that is the route the Blazor page belongs to, and it keeps ids, query strings and
    /// anything a client invents out of the tag.</summary>
    internal static string SanitizePage(string? page)
    {
        if (string.IsNullOrWhiteSpace(page) || page[0] != '/') return "unknown";
        var segment = FirstSegment(page).ToLowerInvariant();
        return KnownPageSegments.Contains(segment) ? "/" + segment : "other";
    }

    /// <summary>"/notes/123?x=1" gives "notes"; "/" gives "".</summary>
    private static string FirstSegment(string path)
    {
        var end = path.IndexOfAny(new[] { '?', '#' });
        if (end >= 0) path = path[..end];
        path = path.TrimStart('/');
        var slash = path.IndexOf('/');
        return slash >= 0 ? path[..slash] : path;
    }

    private static string ForLog(string? value) =>
        value is null ? "" : value.Replace("\r", " ").Replace("\n", " ");

    /// <summary>One line, at most <paramref name="maxLength"/> characters: every CR/LF (and other
    /// control character) becomes a space, so client text cannot start a new log record.</summary>
    internal static string ForLogSingleLine(string? value, int maxLength)
    {
        if (value is null) return "";
        var capped = value.Length > maxLength ? value[..maxLength] : value;
        return string.Create(capped.Length, capped, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++) span[i] = char.IsControl(src[i]) ? ' ' : src[i];
        });
    }
}
