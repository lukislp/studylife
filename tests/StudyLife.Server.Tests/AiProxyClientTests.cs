using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// AiProxyClient's HTTP-facing behavior, verified against a stub handler that records
/// requests - same style as ApnsPushTests' StubHttpHandler (deliberately plainly-built
/// instead of a mocking framework, matching the repo's test style). Real delivery to a real
/// studylife-ai instance is covered by the live end-to-end check instead (see
/// docs/decisions.md - "M4.5 Multi-user support").
/// </summary>
public class AiProxyClientTests
{
    private static AiProxyClient CreateClient(
        StubHttpHandler? handler = null,
        string? baseUrl = "https://ai.test",
        string? sharedSecret = "shared-secret")
    {
        var config = new Dictionary<string, string?>
        {
            ["StudyLifeAi:BaseUrl"] = baseUrl,
            ["StudyLifeAi:SharedSecret"] = sharedSecret,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var httpClient = handler != null ? new HttpClient(handler) : null;
        return new AiProxyClient(configuration, NullLogger<AiProxyClient>.Instance, httpClient);
    }

    [Fact]
    public void WithoutConfiguration_IsDisabled()
    {
        var client = CreateClient(baseUrl: null, sharedSecret: null);
        Assert.False(client.Enabled);
    }

    [Fact]
    public void WithPartialConfiguration_StaysDisabled()
    {
        var client = CreateClient(baseUrl: "https://ai.test", sharedSecret: null);
        Assert.False(client.Enabled);
    }

    [Fact]
    public async Task ProxyAsync_SendsProxyTokenHeaderAndForwardsBody()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"answer\":\"hi\"}"),
        });
        var client = CreateClient(handler);
        var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"message\":\"hallo\"}"));

        var response = await client.ProxyAsync("/agent", 42, body, "application/json", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ai.test/agent", request.Uri);
        Assert.True(request.Headers.ContainsKey("x-studylife-proxy-token"));
        var token = request.Headers["x-studylife-proxy-token"];
        Assert.StartsWith("42.", token);
        Assert.Equal("{\"message\":\"hallo\"}", request.Body);
    }

    [Fact]
    public async Task ProxyAsync_DifferentUsers_MintDifferentTokens()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);

        await client.ProxyAsync("/chat", 1, new MemoryStream(), "application/json", CancellationToken.None);
        await client.ProxyAsync("/chat", 2, new MemoryStream(), "application/json", CancellationToken.None);

        Assert.StartsWith("1.", handler.Requests[0].Headers["x-studylife-proxy-token"]);
        Assert.StartsWith("2.", handler.Requests[1].Headers["x-studylife-proxy-token"]);
    }

    [Fact]
    public async Task RegisterKeyAsync_SendsSharedSecretAndUserIdAndKey()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);

        await client.RegisterKeyAsync(7, "plaintext-key", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ai.test/internal/register-key", request.Uri);
        Assert.Equal("shared-secret", request.Headers["x-studylife-shared-secret"]);
        Assert.Contains("\"user_id\":\"7\"", request.Body);
        Assert.Contains("\"ai_api_key\":\"plaintext-key\"", request.Body);
    }

    [Fact]
    public async Task RevokeKeyAsync_SendsSharedSecretAndUserId()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);

        await client.RevokeKeyAsync(7, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ai.test/internal/revoke-key", request.Uri);
        Assert.Contains("\"user_id\":\"7\"", request.Body);
    }

    [Fact]
    public async Task RegisterKeyAsync_WhenDisabled_NeverCallsOut()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler, baseUrl: null, sharedSecret: null);

        await client.RegisterKeyAsync(7, "key", CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RegisterKeyAsync_UpstreamFailure_DoesNotThrow()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        // Must not throw - a studylife-ai outage must not fail StudyLife's own key generation.
        await client.RegisterKeyAsync(7, "key", CancellationToken.None);
    }

    [Fact]
    public async Task RegisterKeyAsync_NetworkFailure_DoesNotThrow()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        await client.RegisterKeyAsync(7, "key", CancellationToken.None);
    }

    [Fact]
    public async Task EnrichCaptureAsync_SendsSharedSecretAndFields()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"course_id\":null,\"course_confidence\":null,\"tags\":[],\"summary\":null}"),
        });
        var client = CreateClient(handler);

        await client.EnrichCaptureAsync(7, 99, "Title", "Content", "https://example.com/a", new List<int> { 1, 2 }, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ai.test/internal/enrich-capture", request.Uri);
        Assert.Equal("shared-secret", request.Headers["x-studylife-shared-secret"]);
        Assert.Contains("\"user_id\":\"7\"", request.Body);
        Assert.Contains("\"note_id\":99", request.Body);
        Assert.Contains("\"title\":\"Title\"", request.Body);
        Assert.Contains("\"content\":\"Content\"", request.Body);
        Assert.Contains("\"source_url\":\"https://example.com/a\"", request.Body);
        Assert.Contains("\"active_course_ids\":[1,2]", request.Body);
    }

    [Fact]
    public async Task EnrichCaptureAsync_ParsesSuccessfulResponse()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"course_id\":3,\"course_confidence\":0.91,\"tags\":[\"eigenvalues\",\"matrices\"],\"summary\":\"A summary.\",\"related_note_ids\":[12,34]}"),
        });
        var client = CreateClient(handler);

        var result = await client.EnrichCaptureAsync(7, 99, "Title", "Content", null, new List<int>(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result!.CourseId);
        Assert.Equal(0.91, result.CourseConfidence);
        Assert.Equal(new List<string> { "eigenvalues", "matrices" }, result.Tags);
        Assert.Equal("A summary.", result.Summary);
        Assert.Equal(new List<int> { 12, 34 }, result.RelatedNoteIds);
    }

    [Fact]
    public async Task EnrichCaptureAsync_WhenDisabled_NeverCallsOut()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler, baseUrl: null, sharedSecret: null);

        var result = await client.EnrichCaptureAsync(7, 99, "Title", "Content", null, new List<int>(), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EnrichCaptureAsync_UpstreamFailure_ReturnsNull()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        var result = await client.EnrichCaptureAsync(7, 99, "Title", "Content", null, new List<int>(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnrichCaptureAsync_NetworkFailure_ReturnsNull()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        var result = await client.EnrichCaptureAsync(7, 99, "Title", "Content", null, new List<int>(), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>Records all requests (including headers/body) and returns predefined responses -
    /// same deliberately plainly-built stub as ApnsPushTests.StubHttpHandler.</summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public sealed record RecordedRequest(string Uri, Dictionary<string, string> Headers, string Body);

        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<RecordedRequest> Requests { get; } = [];

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers
                .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add(new RecordedRequest(request.RequestUri!.ToString(), headers, body));
            return _responder(request);
        }
    }
}
