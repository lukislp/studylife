using System.Net;
using System.Net.Http.Json;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// 2026-09-11 audit: a webhook target is a URL studylife-webhooks later POSTs to from inside
/// the cluster network, and neither it nor WebhooksProxyController validated the host - so a
/// registration could point at cluster-internal services or a cloud metadata address.
/// OutboundUrlPolicy.IsAcceptableWebhookTarget closes that the same way
/// IsAcceptablePushEndpoint does for Web Push, minus the https-only rule.
/// </summary>
public class WebhookTargetUrlPolicyTests
{
    [Theory]
    [InlineData("https://hooks.example.com/studylife", true)]
    [InlineData("http://home.example.org:8123/api/webhook/abc", true)] // plain http to a public host is the user's call
    [InlineData("http://10.0.0.5:8080/", false)]
    [InlineData("http://169.254.169.254/latest/meta-data/", false)]
    [InlineData("http://127.0.0.1:8001/internal/events", false)]
    [InlineData("http://localhost:8001/internal/events", false)]
    [InlineData("http://studylife-ai.studylife-ai.svc.cluster.local:8001/", false)]
    [InlineData("http://redis/", false)] // bare single-label name only resolves inside a private network
    [InlineData("ftp://hooks.example.com/", false)]
    [InlineData("https://user:secret@hooks.example.com/", false)]
    [InlineData("/relative/path", false)]
    [InlineData("", false)]
    public void IsAcceptableWebhookTarget_ClassifiesTargets(string url, bool expected) =>
        Assert.Equal(expected, OutboundUrlPolicy.IsAcceptableWebhookTarget(url));

    [Fact]
    public void IsAcceptableWebhookTarget_RejectsOverlongUrl() =>
        Assert.False(OutboundUrlPolicy.IsAcceptableWebhookTarget("https://hooks.example.com/" + new string('x', 2100)));
}

public class WebhooksProxyTargetUrlTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public WebhooksProxyTargetUrlTests(CustomWebApplicationFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Create_WithPrivateTarget_ReturnsBadRequest_BeforeTheEnabledGate()
    {
        // 400, not 503: the test host has no StudyLifeWebhooks:* configuration, so anything that
        // reaches ProxyAsync answers 503 - the validation has to run before that gate.
        var response = await _client.PostAsJsonAsync("/api/webhooks", new CreateWebhookRequestDto
        {
            TargetUrl = "http://10.0.0.5:8080/hook",
            Events = new List<string> { "session.completed" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithPublicTarget_ReachesTheProxy()
    {
        var response = await _client.PostAsJsonAsync("/api/webhooks", new CreateWebhookRequestDto
        {
            TargetUrl = "https://hooks.example.com/studylife",
            Events = new List<string> { "session.completed" },
        });

        // Unconfigured integration in the test host - proves the request passed validation.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
