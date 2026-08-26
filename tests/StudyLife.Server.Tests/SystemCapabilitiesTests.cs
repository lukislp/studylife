using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// api/system/capabilities: unauthenticated capability query for the client UI
/// (SetupBackupCard/SetupRestoreCard hide raw-backup controls when the server would
/// answer them with 501 anyway). The test factory runs SQLite, so rawBackupSupported
/// is true here; the Postgres case (false) results from the same service-registration
/// switch that also drives BackupController's 501 behavior and is already covered there.
/// </summary>
public class SystemCapabilitiesTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SystemCapabilitiesTests(CustomWebApplicationFactory factory) => _factory = factory;

    private sealed record CapabilitiesResponse(bool RawBackupSupported);

    [Fact]
    public async Task Capabilities_OnSqlite_ReportsRawBackupSupported()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/system/capabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // no-store: must never come from an HTTP cache (NSURLCache poisoning, see controller).
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var caps = await response.Content.ReadFromJsonAsync<CapabilitiesResponse>();
        Assert.NotNull(caps);
        Assert.True(caps!.RawBackupSupported);
    }

    [Fact]
    public async Task UnknownApiPath_Returns404_NotSpaFallbackHtml()
    {
        // Regression for the NSURLCache poisoning of the native app: unknown /api paths
        // (client newer than server) used to fall into MapFallbackToFile and returned
        // 200+index.html without cache headers - HTTP caches were allowed to cache that
        // heuristically and serve it to the client permanently instead of the real (later
        // deployed) JSON response.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/system/does-not-exist-yet");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Capabilities_WithoutRawBackupServices_ReportsUnsupported()
    {
        // Recreates the Postgres DI state (Program.cs doesn't register DatabaseBackupService/
        // DatabaseRestoreService there): the optional controller parameters then must really
        // fall back to their null defaults instead of failing activation -
        // exactly the path through which SetupBackupCard/SetupRestoreCard hide themselves in prod.
        await using var factory = new NoRawBackupServicesFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system/capabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var caps = await response.Content.ReadFromJsonAsync<CapabilitiesResponse>();
        Assert.NotNull(caps);
        Assert.False(caps!.RawBackupSupported);
    }

    private sealed class NoRawBackupServicesFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Runs AFTER the base class's ConfigureServices - also removes its
            // temp-path replacement instances again, leaving it like prod: no registration at all.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(DatabaseBackupService));
                services.RemoveAll(typeof(DatabaseRestoreService));
            });
        }
    }

    [Fact]
    public async Task Version_IsPubliclyReachable_AndReturnsNonEmptyVersion()
    {
        // GET /api/system/version is one of the few explicit auth exemptions
        // ([Authorize(Policy = "PublicUnlessInvalidSession")], see Auth/StudyLifeAuthorizationPolicies.cs)
        // (pure build metadata for the setup page, no user context) - reachable without any
        // session token or API key.
        using var raw = _factory.CreateClient();
        raw.DefaultRequestHeaders.Clear();

        var response = await raw.GetAsync("/api/system/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<StudyLife.Shared.VersionResponseDto>();
        Assert.NotNull(dto);
        // Locally/in CI without -p:Version the InformationalVersion attribute still exists
        // (SDK default) - the contract is only "never null/empty" ("dev" fallback otherwise).
        Assert.False(string.IsNullOrEmpty(dto!.Version));
    }

    /// <summary>
    /// New pinning test (audit finding A3 refactor): version is reachable WITHOUT any
    /// credential, but an X-Session-Token that IS present and invalid must still be rejected -
    /// this is the one behavior the "PublicUnlessInvalidSession" policy exists to reproduce
    /// (StudyLifeAuthenticationHandler.InvalidSessionTokenItemKey) from the former resolution
    /// middleware, which is otherwise untested (no test previously exercised an invalid token on
    /// an exempt GET route).
    /// </summary>
    [Fact]
    public async Task Version_WithInvalidSessionToken_ReturnsUnauthorized()
    {
        using var raw = _factory.CreateClient();
        raw.DefaultRequestHeaders.Clear();
        raw.DefaultRequestHeaders.Add("X-Session-Token", "not-a-real-token");

        var response = await raw.GetAsync("/api/system/version");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Capabilities_RequiresSession()
    {
        // Deliberately behind the /api session gate (the querying setup cards only exist
        // when logged in) - this test explicitly documents that behavior.
        using var raw = _factory.CreateClient();
        raw.DefaultRequestHeaders.Clear();

        var response = await raw.GetAsync("/api/system/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
