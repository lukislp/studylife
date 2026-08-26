using System.Net;

namespace StudyLife.Server.Tests;

/// <summary>
/// Audit finding O6: GET /healthz/ready (real DB round-trip) and GET /healthz/live
/// (dependency-free) - see HealthController's own comment for the full design rationale. Both
/// MUST be reachable with NO credential at all (kube-probe never sends a session token or API
/// key) - that's exactly what [AllowAnonymous] on HealthController overrides
/// StudyLifeAuthorizationPolicies.FallbackPolicy for, so every test here deliberately uses a
/// truly anonymous client (ApiKeyTestHelpers.CreateClientWithKey with apiKey: null, not the
/// factory's default session-token-carrying client) - covers both "reachable without auth" AND,
/// since it still exercises the full HTTP pipeline, "readiness succeeds against a real,
/// reachable DB" (CustomWebApplicationFactory's temp SQLite file is a genuine working
/// connection - the 503 branch in HealthController.Ready is exercised structurally by its own
/// try/catch, not worth a dedicated "kill the DB mid-test" harness here).
/// </summary>
public class HealthEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public HealthEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Ready_AnonymousRequest_WithWorkingDb_ReturnsOkAndNoStore()
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey: null);

        var response = await client.GetAsync("/healthz/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Live_AnonymousRequest_ReturnsOkAndNoStore()
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey: null);

        var response = await client.GetAsync("/healthz/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
}
