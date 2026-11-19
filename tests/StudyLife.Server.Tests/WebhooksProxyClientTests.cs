// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Configuration;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// WebhooksProxyClient's HTTP-facing behavior, verified against a stub handler that records
/// requests - same style as AiProxyClientTests' StubHttpHandler (deliberately plainly-built
/// instead of a mocking framework, matching the repo's test style).
/// </summary>
public class WebhooksProxyClientTests
{
    private static WebhooksProxyClient CreateClient(
        StubHttpHandler? handler = null,
        string? baseUrl = "https://webhooks.test",
        string? sharedSecret = "shared-secret",
        TimeProvider? timeProvider = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["StudyLifeWebhooks:BaseUrl"] = baseUrl,
            ["StudyLifeWebhooks:SharedSecret"] = sharedSecret,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var httpClient = handler != null ? new HttpClient(handler) : null;
        return new WebhooksProxyClient(TestOptions.For<StudyLifeWebhooksOptions>(configuration), NullLogger<WebhooksProxyClient>.Instance, httpClient, timeProvider);
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
        var client = CreateClient(baseUrl: "https://webhooks.test", sharedSecret: null);
        Assert.False(client.Enabled);
    }

    [Fact]
    public void WithFullConfiguration_IsEnabled()
    {
        var client = CreateClient();
        Assert.True(client.Enabled);
    }

    [Fact]
    public async Task PublishEventAsync_SendsSharedSecretAndEventPayload()
    {
        var handler = WithHookStub(hasWebhook: true);
        var client = CreateClient(handler);

        await client.PublishEventAsync(7, WebhookEventTypes.SessionCompleted, new { sessionId = 42 }, CancellationToken.None);

        var request = Assert.Single(handler.Requests, r => r.Uri.EndsWith("/internal/events"));
        Assert.Equal("https://webhooks.test/internal/events", request.Uri);
        Assert.Equal("shared-secret", request.Headers["x-studylife-shared-secret"]);
        Assert.Contains("\"user_id\":7", request.Body);
        Assert.Contains("\"event_type\":\"session.completed\"", request.Body);
        Assert.Contains("\"sessionId\":42", request.Body);
    }

    private static StubHttpHandler WithHookStub(bool hasWebhook) =>
        new(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(r.RequestUri!.AbsolutePath == "/internal/webhooks" && r.Method == HttpMethod.Get
                ? (hasWebhook ? "[{\"id\":\"w1\"}]" : "[]")
                : "{}"),
        });

    [Fact]
    public async Task PublishEventAsync_UserWithoutWebhooks_SendsNothing()
    {
        var handler = WithHookStub(hasWebhook: false);
        var client = CreateClient(handler);

        await client.PublishEventAsync(7, WebhookEventTypes.NoteCreated, new { title = "secret" }, CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, r => r.Uri.EndsWith("/internal/events"));
    }

    [Fact]
    public async Task PublishEventAsync_LookupIsCachedPerUser()
    {
        var handler = WithHookStub(hasWebhook: true);
        var client = CreateClient(handler);

        await client.PublishEventAsync(7, WebhookEventTypes.NoteCreated, new { }, CancellationToken.None);
        await client.PublishEventAsync(7, WebhookEventTypes.NoteUpdated, new { }, CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Method == "GET");
        Assert.Equal(2, handler.Requests.Count(r => r.Uri.EndsWith("/internal/events")));
    }

    [Fact]
    public async Task PublishEventAsync_RightAfterCreatingAWebhook_IsSentEvenIfNoneWasCachedBefore()
    {
        var hasWebhook = false;
        var handler = new StubHttpHandler(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(r.Method == HttpMethod.Get ? (hasWebhook ? "[{}]" : "[]") : "{}"),
        });
        var client = CreateClient(handler);

        await client.PublishEventAsync(7, WebhookEventTypes.NoteCreated, new { }, CancellationToken.None);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.EndsWith("/internal/events"));

        hasWebhook = true;
        await client.CreateWebhookAsync(7, "https://example.com/hook", new[] { WebhookEventTypes.NoteCreated }, CancellationToken.None);
        await client.PublishEventAsync(7, WebhookEventTypes.NoteCreated, new { }, CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Uri.EndsWith("/internal/events"));
    }

    [Fact]
    public async Task PublishEventAsync_NegativeAnswerExpiresQuickly_SoWebhooksCreatedElsewhereAreSeen()
    {
        var hasWebhook = false;
        var handler = new StubHttpHandler(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(r.Method == HttpMethod.Get ? (hasWebhook ? "[{}]" : "[]") : "{}"),
        });
        var time = new ManualTimeProvider();
        var client = CreateClient(handler, timeProvider: time);

        await client.PublishEventAsync(7, WebhookEventTypes.NoteCreated, new { }, CancellationToken.None);
        hasWebhook = true;
        time.Advance(WebhooksProxyClient.NegativeTtl + TimeSpan.FromSeconds(1));
        await client.PublishEventAsync(7, WebhookEventTypes.NoteCreated, new { }, CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Uri.EndsWith("/internal/events"));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("network")]
    [InlineData("null-body")]
    public async Task PublishEventAsync_WhenTheLookupFails_DoesNotSendTheEvent(string failure)
    {
        var handler = new StubHttpHandler(r =>
        {
            if (r.Method != HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK);
            return failure switch
            {
                "status" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                "network" => throw new HttpRequestException("connection refused"),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null") },
            };
        });
        var client = CreateClient(handler);

        await client.PublishEventAsync(7, WebhookEventTypes.NoteCreated, new { }, CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, r => r.Uri.EndsWith("/internal/events"));
    }

    [Fact]
    public async Task PublishEventAsync_ALookupFailureIsNotCached_SoTheNextEventAsksAgain()
    {
        var lookupFails = true;
        var handler = new StubHttpHandler(r =>
        {
            if (r.Method != HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK);
            return lookupFails
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{}]") };
        });
        var client = CreateClient(handler);

        await client.PublishEventAsync(7, WebhookEventTypes.NoteCreated, new { }, CancellationToken.None);
        lookupFails = false;
        await client.PublishEventAsync(7, WebhookEventTypes.NoteCreated, new { }, CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Uri.EndsWith("/internal/events"));
    }

    [Fact]
    public async Task PublishEventAsync_WhenDisabled_NeverCallsOut()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler, baseUrl: null, sharedSecret: null);

        await client.PublishEventAsync(7, WebhookEventTypes.TimerStarted, new { }, CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PublishEventAsync_UpstreamFailure_DoesNotThrow()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        // Must not throw - an unreachable studylife-webhooks must never fail the request that
        // triggered the event.
        await client.PublishEventAsync(7, WebhookEventTypes.TimerStarted, new { }, CancellationToken.None);
    }

    [Fact]
    public async Task PurgeUserAsync_DeletesTheUsersDataWithTheSharedSecret()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);

        Assert.True(await client.PurgeUserAsync(7, CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://webhooks.test/internal/users/7", request.Uri);
        Assert.Equal("shared-secret", request.Headers["x-studylife-shared-secret"]);
    }

    [Fact]
    public async Task PurgeUserAsync_WhenDisabled_IsANoOpSuccess()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler, baseUrl: null, sharedSecret: null);

        Assert.True(await client.PurgeUserAsync(7, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)] // an older studylife-webhooks without the route
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PurgeUserAsync_WhenNotConfirmed_ReturnsFalseSoTheCallerRetries(HttpStatusCode status)
    {
        var client = CreateClient(new StubHttpHandler(_ => new HttpResponseMessage(status)));

        Assert.False(await client.PurgeUserAsync(7, CancellationToken.None));
    }

    [Fact]
    public async Task PurgeUserAsync_NetworkFailure_ReturnsFalseWithoutThrowing()
    {
        var client = CreateClient(new StubHttpHandler(_ => throw new HttpRequestException("connection refused")));

        Assert.False(await client.PurgeUserAsync(7, CancellationToken.None));
    }

    [Fact]
    public async Task PublishEventAsync_NetworkFailure_DoesNotThrow()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        await client.PublishEventAsync(7, WebhookEventTypes.TimerStarted, new { }, CancellationToken.None);
    }

    [Fact]
    public async Task ListWebhooksAsync_SendsSharedSecretAndUserIdQuery()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var client = CreateClient(handler);

        await client.ListWebhooksAsync(7, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://webhooks.test/internal/webhooks?user_id=7", request.Uri);
        Assert.Equal("shared-secret", request.Headers["x-studylife-shared-secret"]);
    }

    [Fact]
    public async Task CreateWebhookAsync_SendsUserIdTargetUrlAndEvents()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);

        await client.CreateWebhookAsync(7, "https://example.com/hook", new[] { WebhookEventTypes.SessionCompleted }, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://webhooks.test/internal/webhooks", request.Uri);
        Assert.Contains("\"user_id\":7", request.Body);
        Assert.Contains("\"target_url\":\"https://example.com/hook\"", request.Body);
        Assert.Contains("\"events\":[\"session.completed\"]", request.Body);
    }

    [Fact]
    public async Task DeleteWebhookAsync_SendsUserIdQueryAndEscapesId()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);

        await client.DeleteWebhookAsync(7, "hook id/with-slash", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        // Uri.ToString() (what the stub records) decodes %20 back to a literal space for display,
        // while keeping %2F encoded (decoding it would change the path's meaning) - the real wire
        // request (Uri.AbsoluteUri, not exercised by this in-process stub) keeps both encoded.
        // The behavior under test is "the '/' inside the id can't be mistaken for a path
        // separator" - confirmed by %2F surviving here regardless of how ToString() renders it.
        Assert.Equal("https://webhooks.test/internal/webhooks/hook id%2Fwith-slash?user_id=7", request.Uri);
    }

    /// <summary>Records all requests (including headers/body) and returns predefined responses -
    /// same deliberately plainly-built stub as AiProxyClientTests.StubHttpHandler.</summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public sealed record RecordedRequest(string Method, string Uri, Dictionary<string, string> Headers, string Body);

        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<RecordedRequest> Requests { get; } = [];

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers
                .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!.ToString(), headers, body));
            return _responder(request);
        }
    }
}
