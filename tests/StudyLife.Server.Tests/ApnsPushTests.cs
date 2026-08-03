using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// APNs channel (native app shell): registration endpoints + ApnsSender. Real delivery
/// to Apple can't be simulated (as with web push, see the PushControllerTests comment) -
/// the sender's HTTP part is therefore verified against a stub handler that records the
/// requests: that way JWT construction, headers, and payload mapping are still fully covered.
/// </summary>
public class ApnsPushTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ApnsPushTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<PushSubscriptionEntity?> FindByEndpoint(string endpoint)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        return await db.PushSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Endpoint == endpoint);
    }

    // ===== Registration endpoints =====

    [Fact]
    public async Task SubscribeApns_CreatesApnsChannelRow()
    {
        var response = await _client.PostAsJsonAsync("/api/push/subscribe-apns",
            new ApnsSubscribeRequest("token-create-1", "iPhone von Testnutzer"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sub = await FindByEndpoint("apns:token-create-1");
        Assert.NotNull(sub);
        Assert.Equal(PushSubscriptionEntity.ChannelApns, sub!.Channel);
        Assert.Equal("token-create-1", sub.ApnsToken);
        Assert.Equal("iPhone von Testnutzer", sub.UserAgent);
        Assert.NotNull(sub.CreatedAt);
    }

    [Fact]
    public async Task SubscribeApns_SameTokenTwice_DeduplicatesAndRefreshesName()
    {
        await _client.PostAsJsonAsync("/api/push/subscribe-apns", new ApnsSubscribeRequest("token-dup", "Altname"));
        var first = await FindByEndpoint("apns:token-dup");

        var response = await _client.PostAsJsonAsync("/api/push/subscribe-apns", new ApnsSubscribeRequest("token-dup", "Neuname"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        var rows = await db.PushSubscriptions.AsNoTracking()
            .Where(s => s.Endpoint == "apns:token-dup").ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Neuname", rows[0].UserAgent);
        Assert.Equal(first!.CreatedAt, rows[0].CreatedAt); // original registration timestamp remains
    }

    [Fact]
    public async Task SubscribeApns_MissingDeviceName_FallsBackToGenericLabel()
    {
        await _client.PostAsJsonAsync("/api/push/subscribe-apns", new ApnsSubscribeRequest("token-noname", null));

        var sub = await FindByEndpoint("apns:token-noname");
        Assert.Equal("StudyLife App", sub!.UserAgent);
    }

    [Fact]
    public async Task SubscribeApns_EmptyToken_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/push/subscribe-apns", new ApnsSubscribeRequest("  ", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnsubscribeApns_RemovesRow()
    {
        await _client.PostAsJsonAsync("/api/push/subscribe-apns", new ApnsSubscribeRequest("token-remove", null));
        Assert.NotNull(await FindByEndpoint("apns:token-remove"));

        var response = await _client.PostAsJsonAsync("/api/push/unsubscribe-apns", new ApnsSubscribeRequest("token-remove", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await FindByEndpoint("apns:token-remove"));
    }

    [Fact]
    public async Task Subscriptions_ListContainsApnsDeviceWithSyntheticEndpointHash()
    {
        await _client.PostAsJsonAsync("/api/push/subscribe-apns", new ApnsSubscribeRequest("token-list", "iPad"));

        var items = await _client.GetFromJsonAsync<List<StudyLife.Shared.PushSubscriptionListItemDto>>("/api/push/subscriptions");

        // App's "this device" marker: hash over the synthetic endpoint, identical to the
        // computation the native shell performs locally for its own token.
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("apns:token-list")));
        Assert.Contains(items!, i => i.EndpointHash == expectedHash && i.UserAgent == "iPad");
    }

    // ===== ApnsSender =====

    private static ApnsSender CreateSender(
        StubHttpHandler? handler = null,
        Dictionary<string, string?>? configOverrides = null,
        string? keyPath = null)
    {
        var config = new Dictionary<string, string?>();
        if (keyPath != null)
        {
            config["Apns:KeyPath"] = keyPath;
            config["Apns:KeyId"] = "TESTKEY123";
            config["Apns:TeamId"] = "TEAM123456";
            config["Apns:BundleId"] = "app.studylife.mobile";
            config["Apns:Endpoint"] = "https://apns.test";
        }
        if (configOverrides != null)
            foreach (var (k, v) in configOverrides) config[k] = v;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var httpClient = handler != null ? new HttpClient(handler) : null;
        return new ApnsSender(configuration, NullLogger<ApnsSender>.Instance, httpClient);
    }

    private static string WriteTempP8Key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportPkcs8PrivateKeyPem();
        var path = Path.Combine(Path.GetTempPath(), $"studylife-apns-test-{Guid.NewGuid():N}.p8");
        File.WriteAllText(path, pem);
        return path;
    }

    [Fact]
    public async Task ApnsSender_WithoutConfiguration_IsDisabledAndSendIsNoOp()
    {
        var sender = CreateSender();

        Assert.False(sender.Enabled);
        Assert.Equal(ApnsSendOutcome.Failed, await sender.SendPayloadAsync("tok", "{\"title\":\"x\",\"body\":\"y\"}"));
    }

    [Fact]
    public void ApnsSender_WithPartialConfiguration_StaysDisabled()
    {
        var sender = CreateSender(configOverrides: new() { ["Apns:KeyId"] = "NUR-EINER" });
        Assert.False(sender.Enabled);
    }

    [Fact]
    public async Task ApnsSender_SendsAlertWithJwtAndTopicHeaders()
    {
        var keyPath = WriteTempP8Key();
        try
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var sender = CreateSender(handler, keyPath: keyPath);

            var outcome = await sender.SendPayloadAsync("device-tok-1",
                "{\"title\":\"Fokus fertig\",\"body\":\"25 Minuten geschafft\"}");

            Assert.Equal(ApnsSendOutcome.Delivered, outcome);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://apns.test/3/device/device-tok-1", request.Uri);
            Assert.Equal("app.studylife.mobile", request.Headers["apns-topic"]);
            Assert.Equal("alert", request.Headers["apns-push-type"]);
            Assert.StartsWith("bearer ", request.Headers["authorization"]);
            // JWT: three base64url segments (header.claims.signature)
            Assert.Equal(3, request.Headers["authorization"]["bearer ".Length..].Split('.').Length);
            Assert.Contains("Fokus fertig", request.Body);
            Assert.Contains("25 Minuten geschafft", request.Body);
            Assert.Contains("\"aps\"", request.Body);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task ApnsSender_ReusesCachedJwtAcrossSends()
    {
        var keyPath = WriteTempP8Key();
        try
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var sender = CreateSender(handler, keyPath: keyPath);

            await sender.SendPayloadAsync("tok", "{\"title\":\"a\",\"body\":\"b\"}");
            await sender.SendPayloadAsync("tok", "{\"title\":\"a\",\"body\":\"b\"}");

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(handler.Requests[0].Headers["authorization"], handler.Requests[1].Headers["authorization"]);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, "{\"reason\":\"Unregistered\"}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"reason\":\"BadDeviceToken\"}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"reason\":\"DeviceTokenNotForTopic\"}")]
    public async Task ApnsSender_TerminalTokenErrors_ReportExpiredToken(HttpStatusCode status, string body)
    {
        var keyPath = WriteTempP8Key();
        try
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
            var sender = CreateSender(handler, keyPath: keyPath);

            var outcome = await sender.SendPayloadAsync("tok", "{\"title\":\"a\",\"body\":\"b\"}");

            Assert.Equal(ApnsSendOutcome.ExpiredToken, outcome);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task ApnsSender_TransientServerError_ReportsFailedNotExpired()
    {
        var keyPath = WriteTempP8Key();
        try
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"reason\":\"InternalServerError\"}", Encoding.UTF8, "application/json"),
            });
            var sender = CreateSender(handler, keyPath: keyPath);

            Assert.Equal(ApnsSendOutcome.Failed, await sender.SendPayloadAsync("tok", "{\"title\":\"a\",\"body\":\"b\"}"));
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task ApnsSender_NonJsonPayload_FallsBackToRawBody()
    {
        var keyPath = WriteTempP8Key();
        try
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var sender = CreateSender(handler, keyPath: keyPath);

            await sender.SendPayloadAsync("tok", "kein json");

            Assert.Contains("kein json", Assert.Single(handler.Requests).Body);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    // ===== Live Activity push (step D) =====

    [Fact]
    public async Task ApnsSender_LiveActivityUpdate_UsesLiveActivityTopicAndPushType()
    {
        var keyPath = WriteTempP8Key();
        try
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var sender = CreateSender(handler, keyPath: keyPath);
            var endsAt = new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.Zero);

            var outcome = await sender.SendLiveActivityUpdateAsync("activity-tok-1", endsAt,
                isBreak: false, secondsLeft: 300, phaseTotalSeconds: 1500, round: 2, totalRounds: 4);

            Assert.Equal(ApnsSendOutcome.Delivered, outcome);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://apns.test/3/device/activity-tok-1", request.Uri);
            Assert.Equal("app.studylife.mobile.push-type.liveactivity", request.Headers["apns-topic"]);
            Assert.Equal("liveactivity", request.Headers["apns-push-type"]);
            Assert.Contains("\"event\":\"update\"", request.Body);
            Assert.Contains("\"content-state\"", request.Body);
            // Unix epoch seconds, not Swift's reference-date codec (see
            // TimerActivityAttributes.ContentState in the app repo) - exact value verifiable.
            Assert.Contains($"\"endsAt\":{endsAt.ToUnixTimeSeconds()}", request.Body);
            Assert.Contains("\"round\":2", request.Body);
            Assert.Contains("\"totalRounds\":4", request.Body);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task ApnsSender_LiveActivityEnd_SendsEndEventWithDismissalDate()
    {
        var keyPath = WriteTempP8Key();
        try
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var sender = CreateSender(handler, keyPath: keyPath);

            var outcome = await sender.SendLiveActivityEndAsync("activity-tok-2", DateTimeOffset.UtcNow,
                isBreak: true, secondsLeft: 0, phaseTotalSeconds: 0, round: 4, totalRounds: 4);

            Assert.Equal(ApnsSendOutcome.Delivered, outcome);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("app.studylife.mobile.push-type.liveactivity", request.Headers["apns-topic"]);
            Assert.Contains("\"event\":\"end\"", request.Body);
            Assert.Contains("\"dismissal-date\"", request.Body);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task ApnsSender_LiveActivityUpdate_TerminalTokenError_ReportsExpiredToken()
    {
        var keyPath = WriteTempP8Key();
        try
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone));
            var sender = CreateSender(handler, keyPath: keyPath);

            var outcome = await sender.SendLiveActivityUpdateAsync("activity-tok-3", DateTimeOffset.UtcNow,
                isBreak: false, secondsLeft: 1, phaseTotalSeconds: 1, round: 1, totalRounds: 1);

            Assert.Equal(ApnsSendOutcome.ExpiredToken, outcome);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    /// <summary>Records all requests (including headers/body) and returns predefined responses -
    /// a deliberately plainly-built stub instead of a mocking framework, matching the repo's test style.</summary>
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
