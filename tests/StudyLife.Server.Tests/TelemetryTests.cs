using System.Diagnostics.Metrics;
using System.Net;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Phase 1 of the telemetry plan (docs/ARCHITECTURE.md "Telemetry"): the server's own meter
/// records what the framework does not, and the Prometheus scrape surface never appears on the
/// application ports. The test host has no Telemetry:MetricsPort, exactly like the Pi/compose
/// deployments - so /metrics on the app port must behave like any unknown path.
/// </summary>
public class TelemetryTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TelemetryTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Metrics_are_not_served_on_the_application_port()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/metrics");

        // Unknown non-/api path -> SPA shell (index.html), never Prometheus exposition text.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("# TYPE", body);
        Assert.DoesNotContain("studylife_cache_requests", body);
    }

    [Fact]
    public async Task Cache_helper_records_miss_then_not_modified()
    {
        var observed = new List<(long Value, string Result)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == StudyLifeMetrics.MeterName && instrument.Name == "studylife.cache.requests")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "result") lock (observed) observed.Add((value, (string)tag.Value!));
        });
        listener.Start();

        using var client = _factory.CreateClient(); // CreateClient() attaches the seeded session token
        var first = await client.GetAsync("/api/settings");
        first.EnsureSuccessStatusCode();
        var etag = first.Headers.ETag!.Tag;

        client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);

        lock (observed)
        {
            Assert.Contains(observed, o => o.Result == "not_modified" && o.Value == 1);
            Assert.Contains(observed, o => (o.Result == "miss" || o.Result == "hit") && o.Value == 1);
        }
    }
}
