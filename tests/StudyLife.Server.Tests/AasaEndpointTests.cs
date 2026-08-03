using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace StudyLife.Server.Tests;

/// <summary>
/// GET /.well-known/apple-app-site-association (see docs/PLAN-paid-umschaltung.md step B):
/// enables the app's native in-app passkey dialog once the Associated Domains
/// entitlement is carried. Deliberately 404 without Apple:TeamId config (free signing) - the
/// standard CustomWebApplicationFactory runs without this config, so it covers the default case.
/// </summary>
public class AasaEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AasaEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task WithoutTeamIdConfig_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/.well-known/apple-app-site-association");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WithTeamIdConfig_ReturnsWebcredentialsJson()
    {
        using var configuredFactory = new WithAppleTeamIdFactory();
        var client = configuredFactory.CreateClient();

        var response = await client.GetAsync("/.well-known/apple-app-site-association");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var dto = await response.Content.ReadFromJsonAsync<AasaResponse>();
        Assert.NotNull(dto);
        var app = Assert.Single(dto!.Webcredentials.Apps);
        Assert.Equal("GAP8W46W8A.app.studylife.mobile", app);
    }

    private sealed record AasaResponse(WebcredentialsSection Webcredentials);
    private sealed record WebcredentialsSection(List<string> Apps);

    private sealed class WithAppleTeamIdFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Apple:TeamId"] = "GAP8W46W8A",
                }));
        }
    }
}
