// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// The per-event-type mapping in TelemetryController.RecordEvent: every numeric field is only
/// recorded when it is present AND non-negative (a negative duration from a skewed client clock
/// must never reach a histogram), the tag values are clamped to their allow-lists, and an
/// unknown type only bumps the dropped counter.
///
/// The StudyLife.Client meter is process-wide and other test classes post telemetry in parallel,
/// so every assertion keys on measurement values no other test sends (the 7xxx.xxx range below),
/// never on "nothing at all was recorded".
/// </summary>
public class TelemetryEventMappingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public TelemetryEventMappingTests(CustomWebApplicationFactory factory) => _client = factory.CreateClient();

    private sealed record Measurement(Instrument Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>Collects every StudyLife.Client measurement while it is alive.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Measurement> _measurements = [];

        public Recorder()
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ClientTelemetryMetrics.MeterName) l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
            _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
            _listener.Start();
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value;
            lock (_measurements) _measurements.Add(new Measurement(instrument, value, dict));
        }

        public List<Measurement> On(Instrument instrument)
        {
            lock (_measurements) return _measurements.Where(m => ReferenceEquals(m.Instrument, instrument)).ToList();
        }

        public bool AnyValue(double value)
        {
            lock (_measurements) return _measurements.Any(m => Close(m.Value, value));
        }

        public void Dispose() => _listener.Dispose();
    }

    private static bool Close(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static void AssertRecorded(Recorder recorder, Instrument instrument, double expected) =>
        Assert.Contains(recorder.On(instrument), m => Close(m.Value, expected));

    private async Task PostAsync(params TelemetryEventDto[] events)
    {
        (await _client.PutAsJsonAsync("/api/settings", new UserSettingsDto { TelemetryConsent = true })).EnsureSuccessStatusCode();
        var response = await _client.PostAsJsonAsync("/api/telemetry", new TelemetryBatchDto
        {
            SessionId = "mappingtests12345678",
            Platform = "ios",
            AppVersion = "3.1.0",
            Language = "de",
            Events = events.ToList(),
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Boot_RecordsEveryPhaseInSeconds_AndSkipsNegativeOrMissingPhases()
    {
        using var recorder = new Recorder();

        await PostAsync(
            new TelemetryEventDto
            {
                Type = "boot",
                Cold = false,
                SwCacheHit = true,
                HtmlMs = 7101,
                BootScriptMs = 7102,
                WasmDownloadMs = 7103,
                RuntimeReadyMs = 7104,
                FirstRenderMs = 7105,
                DashboardReadyMs = 7106,
                DownloadBytes = 7107,
            },
            new TelemetryEventDto
            {
                Type = "boot",
                HtmlMs = -7111,
                BootScriptMs = -7112,
                WasmDownloadMs = -7113,
                RuntimeReadyMs = -7114,
                FirstRenderMs = -7115,
                DashboardReadyMs = -7116,
                DownloadBytes = -7117,
            },
            new TelemetryEventDto { Type = "boot" });

        AssertRecorded(recorder, ClientTelemetryMetrics.BootHtmlDuration, 7.101);
        AssertRecorded(recorder, ClientTelemetryMetrics.BootScriptDuration, 7.102);
        AssertRecorded(recorder, ClientTelemetryMetrics.BootWasmDownloadDuration, 7.103);
        AssertRecorded(recorder, ClientTelemetryMetrics.BootRuntimeReadyDuration, 7.104);
        AssertRecorded(recorder, ClientTelemetryMetrics.BootFirstRenderDuration, 7.105);
        AssertRecorded(recorder, ClientTelemetryMetrics.BootDashboardReadyDuration, 7.106);
        AssertRecorded(recorder, ClientTelemetryMetrics.BootDownloadBytes, 7107); // bytes, not scaled
        for (var v = 7111; v <= 7117; v++)
        {
            Assert.False(recorder.AnyValue(-v), $"negative value {-v} must be dropped");
            Assert.False(recorder.AnyValue(-v / 1000.0), $"negative value {-v} must be dropped");
        }

        // The warm boot keeps its cold=false tag; the other two default to cold=false as well,
        // and every boot (also the one without any phase) is counted.
        var html = recorder.On(ClientTelemetryMetrics.BootHtmlDuration).Single(m => Close(m.Value, 7.101));
        Assert.Equal("false", html.Tags["cold"]?.ToString()?.ToLowerInvariant());
        Assert.Equal("ios", html.Tags["platform"]);
        var boots = recorder.On(ClientTelemetryMetrics.Boots).Where(m => Equals(m.Tags.GetValueOrDefault("app_version"), "3.1.0")).ToList();
        Assert.True(boots.Count >= 3);
    }

    [Fact]
    public async Task Vitals_RecordsPresentNonNegativeValuesOnly()
    {
        using var recorder = new Recorder();

        await PostAsync(
            new TelemetryEventDto { Type = "vitals", Ttfb = 7201, Fcp = 7202, Lcp = 7203, Inp = 7204, Cls = 0.7205 },
            new TelemetryEventDto { Type = "vitals", Ttfb = -7211, Fcp = -7212, Lcp = -7213, Inp = -7214, Cls = -0.7215 },
            new TelemetryEventDto { Type = "vitals" });

        // Web vitals are recorded in their native unit (ms / unitless), not converted to seconds.
        AssertRecorded(recorder, ClientTelemetryMetrics.VitalsTtfb, 7201);
        AssertRecorded(recorder, ClientTelemetryMetrics.VitalsFcp, 7202);
        AssertRecorded(recorder, ClientTelemetryMetrics.VitalsLcp, 7203);
        AssertRecorded(recorder, ClientTelemetryMetrics.VitalsInp, 7204);
        AssertRecorded(recorder, ClientTelemetryMetrics.VitalsCls, 0.7205);
        foreach (var negative in new[] { -7211, -7212, -7213, -7214, -0.7215 })
            Assert.False(recorder.AnyValue(negative));
    }

    [Theory]
    [InlineData(204, "2xx")]
    [InlineData(503, "5xx")]
    [InlineData(99, "unknown")]
    [InlineData(600, "unknown")]
    [InlineData(null, "unknown")]
    public async Task Api_ClassifiesTheStatus_AndClampsTheMethod(int? status, string expectedClass)
    {
        using var recorder = new Recorder();
        var duration = 7300 + (status ?? 0) / 1000.0;

        await PostAsync(
            new TelemetryEventDto { Type = "api", Route = "api/notes", Method = "delete", Status = status, DurationMs = duration, NotModified = true },
            new TelemetryEventDto { Type = "api", Route = "api/notes", Method = "BREW", Status = status, DurationMs = -duration });

        var recorded = recorder.On(ClientTelemetryMetrics.ApiDuration).Single(m => Close(m.Value, duration / 1000.0));
        Assert.Equal(expectedClass, recorded.Tags["status_class"]);
        Assert.Equal("DELETE", recorded.Tags["method"]); // upper-cased, then allow-listed
        Assert.False(recorder.AnyValue(-duration / 1000.0));
    }

    [Fact]
    public async Task Api_UnknownOrMissingMethod_IsTaggedUnknown_AndTheRequestIsStillCounted()
    {
        using var recorder = new Recorder();

        await PostAsync(
            new TelemetryEventDto { Type = "api", Route = "api/notes", Method = "BREW", Status = 200, DurationMs = 7351 },
            new TelemetryEventDto { Type = "api", Route = "api/notes", Status = 200, DurationMs = 7352 },
            new TelemetryEventDto { Type = "api", Route = "api/notes", Method = "GET" });

        Assert.Equal("unknown", recorder.On(ClientTelemetryMetrics.ApiDuration).Single(m => Close(m.Value, 7.351)).Tags["method"]);
        Assert.Equal("unknown", recorder.On(ClientTelemetryMetrics.ApiDuration).Single(m => Close(m.Value, 7.352)).Tags["method"]);
        // The event without a duration still counts as a request (3 requests, 2 durations).
        Assert.True(recorder.On(ClientTelemetryMetrics.ApiRequests).Count(m => Equals(m.Tags.GetValueOrDefault("platform"), "ios")) >= 3);
    }

    [Fact]
    public async Task Sse_CountsEveryEvent_ClampsTheKind_AndRecordsOnlyValidDurations()
    {
        using var recorder = new Recorder();

        await PostAsync(
            new TelemetryEventDto { Type = "sse", Event = "reconnect", DurationMs = 7401 },
            new TelemetryEventDto { Type = "sse", Event = "made-up", DurationMs = 7402 },
            new TelemetryEventDto { Type = "sse", Event = "closed", DurationMs = -7403 },
            new TelemetryEventDto { Type = "sse" });

        Assert.Equal("reconnect", recorder.On(ClientTelemetryMetrics.SseDuration).Single(m => Close(m.Value, 7.401)).Tags["event"]);
        Assert.Equal("unknown", recorder.On(ClientTelemetryMetrics.SseDuration).Single(m => Close(m.Value, 7.402)).Tags["event"]);
        Assert.False(recorder.AnyValue(-7.403));
        Assert.Contains(recorder.On(ClientTelemetryMetrics.SseEvents), m => Equals(m.Tags["event"], "closed"));
    }

    [Theory]
    [InlineData("/notes/123?tab=2", "/notes", 1)]
    [InlineData("/Stats#top", "/stats", 2)]
    [InlineData("/", "/", 3)]
    [InlineData("/<script>", "other", 4)]
    [InlineData("notes", "unknown", 5)]
    [InlineData(null, "unknown", 6)]
    public async Task Navigation_ReducesThePageToItsFirstSegment(string? page, string expectedPage, int id)
    {
        using var recorder = new Recorder();
        var render = 7500 + id;

        await PostAsync(
            new TelemetryEventDto { Type = "navigation", Page = page, RenderMs = render },
            new TelemetryEventDto { Type = "navigation", Page = page, RenderMs = -render },
            new TelemetryEventDto { Type = "navigation", Page = page });

        var recorded = recorder.On(ClientTelemetryMetrics.NavigationRenderDuration).Single(m => Close(m.Value, render / 1000.0));
        Assert.Equal(expectedPage, recorded.Tags["page"]);
        Assert.False(recorder.AnyValue(-render / 1000.0));
    }

    [Fact]
    public async Task Error_ClampsKindAndType_AndDefaultsFatalToFalse()
    {
        using var recorder = new Recorder();

        await PostAsync(
            new TelemetryEventDto { Type = "error", Kind = "js", ErrorType = "TypeError", Fatal = true, Stack = "at a\nat b", StackHash = "ab\r\ncd", Page = "/notes" },
            new TelemetryEventDto { Type = "error", Kind = "kernel-panic", ErrorType = "not a type name!" });

        var errors = recorder.On(ClientTelemetryMetrics.Errors).Where(m => Equals(m.Tags.GetValueOrDefault("app_version"), "3.1.0")).ToList();
        Assert.Contains(errors, m => Equals(m.Tags["kind"], "js") && Equals(m.Tags["type"], "TypeError") && Equals(m.Tags["fatal"]?.ToString()?.ToLowerInvariant(), "true"));
        Assert.Contains(errors, m => Equals(m.Tags["kind"], "unknown") && Equals(m.Tags["type"], "other") && Equals(m.Tags["fatal"]?.ToString()?.ToLowerInvariant(), "false"));
    }

    [Fact]
    public async Task AppLaunchAndAppResource_RecordPresentNonNegativeValuesOnly()
    {
        using var recorder = new Recorder();

        await PostAsync(
            new TelemetryEventDto { Type = "app_launch", ColdMs = 7601, WarmMs = 7602, WebviewReadyMs = 7603 },
            new TelemetryEventDto { Type = "app_launch", ColdMs = -7611, WarmMs = -7612, WebviewReadyMs = -7613 },
            new TelemetryEventDto { Type = "app_launch" },
            new TelemetryEventDto { Type = "app_resource", PeakMemoryMb = 7621, CpuSeconds = 7622, CellularBytes = 7623, WifiBytes = 7624 },
            new TelemetryEventDto { Type = "app_resource", PeakMemoryMb = -7631, CpuSeconds = -7632, CellularBytes = -7633, WifiBytes = -7634 },
            new TelemetryEventDto { Type = "app_resource" });

        AssertRecorded(recorder, ClientTelemetryMetrics.AppLaunchColdDuration, 7.601);
        AssertRecorded(recorder, ClientTelemetryMetrics.AppLaunchWarmDuration, 7.602);
        AssertRecorded(recorder, ClientTelemetryMetrics.AppLaunchWebviewDuration, 7.603);
        // MetricKit aggregates keep their own units.
        AssertRecorded(recorder, ClientTelemetryMetrics.AppResourcePeakMemoryMb, 7621);
        AssertRecorded(recorder, ClientTelemetryMetrics.AppResourceCpuSeconds, 7622);
        AssertRecorded(recorder, ClientTelemetryMetrics.AppResourceCellularBytes, 7623);
        AssertRecorded(recorder, ClientTelemetryMetrics.AppResourceWifiBytes, 7624);
        foreach (var negative in new[] { -7.611, -7.612, -7.613, -7631, -7632, -7633, -7634 })
            Assert.False(recorder.AnyValue(negative));
    }

    [Fact]
    public async Task HealthQuery_ClampsEveryTag_AndRecordsOnlyValidDurations()
    {
        using var recorder = new Recorder();

        await PostAsync(
            new TelemetryEventDto { Type = "health_query", Kind = "sleep", Authorization = "granted", Result = "sufficient", OutlierFiltered = true, DurationMs = 7701 },
            new TelemetryEventDto { Type = "health_query", Kind = "blood", Authorization = "maybe", Result = "great", DurationMs = 7702 },
            new TelemetryEventDto { Type = "health_query", Kind = "hrv", DurationMs = -7703 });

        Assert.Equal("sleep", recorder.On(ClientTelemetryMetrics.HealthQueryDuration).Single(m => Close(m.Value, 7.701)).Tags["kind"]);
        Assert.Equal("unknown", recorder.On(ClientTelemetryMetrics.HealthQueryDuration).Single(m => Close(m.Value, 7.702)).Tags["kind"]);
        Assert.False(recorder.AnyValue(-7.703));
        var queries = recorder.On(ClientTelemetryMetrics.HealthQueries);
        Assert.Contains(queries, m => Equals(m.Tags["kind"], "sleep") && Equals(m.Tags["result"], "sufficient"));
        Assert.Contains(queries, m => Equals(m.Tags["kind"], "unknown") && Equals(m.Tags["result"], "unknown"));
    }

    [Fact]
    public async Task Push_ClampsTheEvent_AndRecordsTheLatencyInMilliseconds()
    {
        using var recorder = new Recorder();

        await PostAsync(
            new TelemetryEventDto { Type = "push", Event = "shown", LatencyMs = 7801 },
            new TelemetryEventDto { Type = "push", Event = "exploded", LatencyMs = 7802 },
            new TelemetryEventDto { Type = "push", Event = "received", LatencyMs = -7803 },
            new TelemetryEventDto { Type = "push" });

        Assert.Equal("shown", recorder.On(ClientTelemetryMetrics.PushLatency).Single(m => Close(m.Value, 7801)).Tags["event"]);
        Assert.Equal("unknown", recorder.On(ClientTelemetryMetrics.PushLatency).Single(m => Close(m.Value, 7802)).Tags["event"]);
        Assert.False(recorder.AnyValue(-7803));
    }
}
