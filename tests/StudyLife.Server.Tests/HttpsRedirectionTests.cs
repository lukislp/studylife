using Microsoft.AspNetCore.Mvc.Testing;

namespace StudyLife.Server.Tests;

/// <summary>
/// Covers the exact production incident (2026-08-14): after configuring
/// HttpsRedirectionOptions.HttpsPort so the redirect middleware could actually determine a
/// target, Kubernetes' own httpGet readiness/liveness probes (genuine plain HTTP, kubelet
/// never sends X-Forwarded-Proto) started getting redirected to a port nothing listens on -
/// every pod failed readiness forever. Fixed by skipping the redirect specifically for
/// kubelet's "kube-probe/*" User-Agent. These two tests pin both halves of that fix: probes
/// must NOT be redirected, but the actual security behavior (catching a real direct-bypass
/// attempt reaching Kestrel without going through nginx/NPM) must still work.
/// </summary>
public class HttpsRedirectionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public HttpsRedirectionTests(CustomWebApplicationFactory factory) => _factory = factory;

    // CustomWebApplicationFactory.ConfigureClient adds X-Forwarded-Proto: https by default
    // (simulating the normal nginx/NPM hop) - removed here to simulate genuine plain HTTP
    // reaching Kestrel directly, which is what both scenarios below are actually about.
    // AllowAutoRedirect: false so the test can inspect the redirect response itself instead
    // of transparently following it.
    private HttpClient CreateUnproxiedClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
        return client;
    }

    [Fact]
    public async Task KubeletProbe_WithoutForwardedProto_IsNotRedirected()
    {
        using var client = CreateUnproxiedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.UserAgent.ParseAdd("kube-probe/1.36");

        var response = await client.SendAsync(request);

        Assert.False((int)response.StatusCode is >= 300 and < 400);
    }

    [Fact]
    public async Task DirectPlainHttpWithoutForwardedProto_IsRedirectedToHttps()
    {
        using var client = CreateUnproxiedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");

        var response = await client.SendAsync(request);

        Assert.True((int)response.StatusCode is >= 300 and < 400);
        Assert.Equal("https", response.Headers.Location?.Scheme);
    }
}
