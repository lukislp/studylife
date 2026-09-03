using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

public class TelemetryClientSampleRatioTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TelemetryClientSampleRatioTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Capabilities_expose_the_default_client_sample_ratio()
    {
        using var client = _factory.CreateClient();
        var capabilities = await client.GetFromJsonAsync<SystemCapabilitiesResponseDto>("/api/system/capabilities");

        Assert.NotNull(capabilities);
        Assert.Equal(0.10, capabilities!.TelemetryClientSampleRatio, 3);
    }
}
