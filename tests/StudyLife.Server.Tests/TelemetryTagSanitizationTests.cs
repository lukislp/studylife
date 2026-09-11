using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// 2026-09-11 audit, finding 7: every free-form client string that reached an OpenTelemetry
/// tag (app_version, language, method, page, type) is now normalised to a bounded shape, so a
/// misbehaving client cannot grow the meter's series set without limit. The meter is
/// process-global and other telemetry test classes run in parallel, so assertions look for
/// this class's own distinctive tag combinations instead of counting measurements.
/// </summary>
public class TelemetryTagSanitizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public TelemetryTagSanitizationTests(CustomWebApplicationFactory factory) => _client = factory.CreateClient();

    private static TelemetryBatchDto Batch(string appVersion, string language, params TelemetryEventDto[] events) => new()
    {
        SessionId = "sanitizer-session-0001",
        Platform = "maccatalyst",
        AppVersion = appVersion,
        Language = language,
        Connection = "wifi",
        Events = events.ToList(),
    };

    private static List<(string Instrument, Dictionary<string, object?> Tags)> Capture(Action send)
    {
        var seen = new List<(string, Dictionary<string, object?>)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ClientTelemetryMetrics.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var t in tags) dict[t.Key] = t.Value;
            lock (seen) seen.Add((instrument.Name, dict));
        }
        listener.SetMeasurementEventCallback<long>(Record);
        listener.SetMeasurementEventCallback<double>(Record);
        listener.Start();
        send();
        lock (seen) return seen.ToList();
    }

    private static bool Has(List<(string Instrument, Dictionary<string, object?> Tags)> seen, string instrument, params (string Key, string Value)[] tags) =>
        seen.Any(m => m.Instrument == instrument && m.Tags.TryGetValue("platform", out var p) && (string?)p == "maccatalyst"
                      && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && (string?)v == t.Value));

    [Fact]
    public async Task FreeFormClientStrings_AreNormalisedBeforeBecomingTags()
    {
        Assert.True((await _client.PutAsJsonAsync("/api/settings", new UserSettingsDto { TelemetryConsent = true })).IsSuccessStatusCode);

        var seen = Capture(() =>
        {
            var response = _client.PostAsJsonAsync("/api/telemetry", Batch(
                appVersion: "1.0.0\nlevel=error injected", language: "EN-xx",
                new TelemetryEventDto { Type = "boot", Cold = true, HtmlMs = 5 },
                new TelemetryEventDto { Type = "api", Route = "/api/notes", Method = "hack\r\n", DurationMs = 3, Status = 200 },
                new TelemetryEventDto { Type = "navigation", Page = "/notes/12345?x=1#frag", RenderMs = 2 },
                new TelemetryEventDto { Type = "error", Kind = "js", ErrorType = "TypeError\nfake", StackHash = "abc", Stack = "line1\nline2" })).Result;
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        });

        Assert.True(Has(seen, "studylife.client.boots", ("app_version", "unknown"), ("language", "unknown")));
        Assert.True(Has(seen, "studylife.client.api.duration", ("method", "unknown")));
        Assert.True(Has(seen, "studylife.client.navigation.render.duration", ("page", "/notes")));
        Assert.True(Has(seen, "studylife.client.errors", ("type", "other"), ("kind", "js")));
    }

    [Fact]
    public async Task WellFormedClientStrings_PassThroughUnchanged()
    {
        Assert.True((await _client.PutAsJsonAsync("/api/settings", new UserSettingsDto { TelemetryConsent = true })).IsSuccessStatusCode);

        var seen = Capture(() =>
        {
            var response = _client.PostAsJsonAsync("/api/telemetry", Batch(
                appVersion: "3.16.99-rc1", language: "fr",
                new TelemetryEventDto { Type = "boot", Cold = false, HtmlMs = 5 },
                new TelemetryEventDto { Type = "api", Route = "/api/notes", Method = "get", DurationMs = 3, Status = 200 },
                new TelemetryEventDto { Type = "navigation", Page = "/sanitizer-ok", RenderMs = 2 },
                new TelemetryEventDto { Type = "error", Kind = "dotnet", ErrorType = "Sanitizer.WellFormedException", StackHash = "abc" })).Result;
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        });

        Assert.True(Has(seen, "studylife.client.boots", ("app_version", "3.16.99-rc1"), ("language", "fr")));
        Assert.True(Has(seen, "studylife.client.api.duration", ("method", "GET")));
        Assert.True(Has(seen, "studylife.client.navigation.render.duration", ("page", "/sanitizer-ok")));
        Assert.True(Has(seen, "studylife.client.errors", ("type", "Sanitizer.WellFormedException"), ("kind", "dotnet")));
    }
}
