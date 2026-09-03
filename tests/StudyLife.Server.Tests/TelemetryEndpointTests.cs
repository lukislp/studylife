using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Phase 2 of the telemetry plan (docs/ARCHITECTURE.md "Telemetry"): POST /api/telemetry always
/// answers 204 on anything that parses, but only actually records to the StudyLife.Client meter
/// (ClientTelemetryMetrics) when the seeded user has explicitly opted in
/// (UserSettingsEntity.TelemetryConsent == true, set here via the normal PUT /api/settings path,
/// same as a real client would). Own class (own factory/DB) for the "no consent yet" case - a
/// fresh database has TelemetryConsent == null (undecided), and that must stay true regardless of
/// what any other test in the suite happens to run first/after.
/// </summary>
public class TelemetryEndpointNoConsentTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public TelemetryEndpointNoConsentTests(CustomWebApplicationFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Post_WithoutConsent_ReturnsNoContentAndRecordsNothing()
    {
        var observed = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ClientTelemetryMetrics.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => { lock (observed) observed.Add(value); });
        listener.Start();

        var batch = new TelemetryBatchDto
        {
            SessionId = "abcdefghijklmnop1234",
            Platform = "web",
            AppVersion = "1.0.0",
            Language = "en",
            Connection = "wifi",
            Events = new List<TelemetryEventDto> { new() { Type = "boot", Cold = true, HtmlMs = 10 } },
        };
        var response = await _client.PostAsJsonAsync("/api/telemetry", batch);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        lock (observed) Assert.Empty(observed);
    }
}

public class TelemetryEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TelemetryEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Every consent-requiring test starts with this - idempotent and independent of
    /// xUnit's (undefined) execution order within the class, unlike relying on a constructor-time
    /// PUT (which can't be async anyway).</summary>
    private Task<HttpResponseMessage> GrantConsentAsync() =>
        _client.PutAsJsonAsync("/api/settings", new UserSettingsDto { TelemetryConsent = true });

    private static TelemetryBatchDto Batch(params TelemetryEventDto[] events) => new()
    {
        SessionId = "abcdefghijklmnop1234",
        Platform = "web",
        AppVersion = "1.0.0",
        Language = "en",
        Connection = "wifi",
        Events = events.ToList(),
    };

    [Fact]
    public async Task Post_WithConsent_RecordsApiDurationAndBoots()
    {
        (await GrantConsentAsync()).EnsureSuccessStatusCode();

        var observedInstruments = new HashSet<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ClientTelemetryMetrics.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => { lock (observedInstruments) observedInstruments.Add(instrument.Name); });
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => { lock (observedInstruments) observedInstruments.Add(instrument.Name); });
        listener.Start();

        var batch = Batch(
            new TelemetryEventDto { Type = "boot", Cold = true, HtmlMs = 120, SwCacheHit = false },
            new TelemetryEventDto { Type = "api", Route = "api/sessions/{id}", Method = "GET", Status = 200, DurationMs = 42 });
        var response = await _client.PostAsJsonAsync("/api/telemetry", batch);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        lock (observedInstruments)
        {
            Assert.Contains("studylife.client.api.duration", observedInstruments);
            Assert.Contains("studylife.client.boots", observedInstruments);
        }
    }

    [Fact]
    public async Task Post_MoreThan50Events_ReturnsBadRequest()
    {
        (await GrantConsentAsync()).EnsureSuccessStatusCode();

        // Raw, minimal-field JSON (not PostAsJsonAsync's full DTO serialization, which pads every
        // one of TelemetryEventDto's ~30 nullable properties as "null" per event) - 51 minimal
        // events must trip the >50 [MaxLength] check, not the unrelated 32 KB body guard.
        var events = string.Join(",", Enumerable.Repeat("""{"type":"vitals","ttfb":100}""", 51));
        var json = $$"""{"sessionId":"abcdefghijklmnop1234","platform":"web","appVersion":"1.0.0","language":"en","connection":"wifi","events":[{{events}}]}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/telemetry", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_OversizedBody_Returns413()
    {
        // Deliberately raw JSON via StringContent (not PostAsJsonAsync) so the declared
        // Content-Length is under our control - a single oversized "stack" field is the
        // simplest way to blow past the 32 KB guard while staying valid JSON shape-wise.
        var oversizedStack = new string('x', 40_000);
        var json = $$"""{"sessionId":"abcdefghijklmnop1234","platform":"web","appVersion":"1.0.0","language":"en","connection":"wifi","events":[{"type":"error","kind":"js","errorType":"Error","stack":"{{oversizedStack}}"}]}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/telemetry", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Post_UnknownEventType_IsCountedAsDropped()
    {
        (await GrantConsentAsync()).EnsureSuccessStatusCode();

        var observed = new List<(long Value, string Reason)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ClientTelemetryMetrics.MeterName && instrument.Name == "studylife.client.events.dropped")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "reason") lock (observed) observed.Add((value, (string)tag.Value!));
        });
        listener.Start();

        var response = await _client.PostAsJsonAsync("/api/telemetry", Batch(new TelemetryEventDto { Type = "bogus_event" }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        lock (observed) Assert.Contains(observed, o => o.Reason == "unknown_type" && o.Value == 1);
    }

    [Fact]
    public async Task Post_ApiRoute_NormalizesKnownRouteAndUnknownRouteToOther()
    {
        (await GrantConsentAsync()).EnsureSuccessStatusCode();

        var observedRoutes = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ClientTelemetryMetrics.MeterName && instrument.Name == "studylife.client.api.requests")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "route") lock (observedRoutes) observedRoutes.Add((string)tag.Value!);
        });
        listener.Start();

        var response = await _client.PostAsJsonAsync("/api/telemetry", Batch(
            new TelemetryEventDto { Type = "api", Route = "api/sessions/123", Method = "GET", Status = 200, DurationMs = 10 },
            new TelemetryEventDto { Type = "api", Route = "api/totally-not-a-real-route/123", Method = "GET", Status = 200, DurationMs = 10 }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        lock (observedRoutes)
        {
            Assert.Contains("api/sessions/{id}", observedRoutes);
            Assert.Contains("other", observedRoutes);
        }
    }

    [Fact]
    public async Task Post_ErrorEvent_IncrementsErrorsCounter()
    {
        (await GrantConsentAsync()).EnsureSuccessStatusCode();

        // No custom ILoggerProvider is wired into CustomWebApplicationFactory, so the
        // "ClientError" structured log itself isn't asserted on here - the counter is the
        // observable proxy for "the error event was recorded" (see docs/ARCHITECTURE.md).
        var observed = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ClientTelemetryMetrics.MeterName && instrument.Name == "studylife.client.errors")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => { lock (observed) observed.Add(value); });
        listener.Start();

        var response = await _client.PostAsJsonAsync("/api/telemetry", Batch(new TelemetryEventDto
        {
            Type = "error",
            Kind = "js",
            ErrorType = "TypeError",
            StackHash = "abc123",
            Stack = "at foo.js:1:1",
            Fatal = true,
        }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        lock (observed) Assert.Contains(observed, v => v == 1);
    }
}
