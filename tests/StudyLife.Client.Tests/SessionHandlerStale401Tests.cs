// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

/// <summary>
/// First-start sign-in race (App Store screenshot run, 2026-11-18): the dashboard's first requests
/// went out without a session, the user signed in while they were still in flight, and their 401
/// responses arrived only after the new token was stored. The handler treated those late 401s as
/// "the session is dead", discarded the brand-new token and sent the user back to the sign-in page.
/// A 401 may only end the session the request was actually sent with.
/// </summary>
public class SessionHandlerStale401Tests
{
    private static readonly Uri BaseAddress = new("https://studylife.test/");

    [Fact]
    public async Task Late_401_for_a_request_sent_without_a_token_does_not_end_a_newer_sign_in()
    {
        var (store, server, client) = Create();
        var invalidated = 0;
        store.OnSessionInvalidated += () => invalidated++;

        var pending = client.GetAsync("api/settings");
        await server.Received.Task;
        await store.SetTokenAsync("fresh-token");
        server.Respond(HttpStatusCode.Unauthorized);
        using var response = await pending;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(server.SentToken);
        Assert.Equal("fresh-token", store.Token);
        Assert.Equal(0, invalidated);
    }

    [Fact]
    public async Task Late_401_for_a_request_sent_with_an_older_token_does_not_end_the_newer_session()
    {
        var (store, server, client) = Create();
        await store.SetTokenAsync("old-token");
        var invalidated = 0;
        store.OnSessionInvalidated += () => invalidated++;

        var pending = client.GetAsync("api/notes");
        await server.Received.Task;
        await store.SetTokenAsync("new-token");
        server.Respond(HttpStatusCode.Unauthorized);
        using var _ = await pending;

        Assert.Equal("old-token", server.SentToken);
        Assert.Equal("new-token", store.Token);
        Assert.Equal(0, invalidated);
    }

    [Fact]
    public async Task A_401_for_the_current_token_still_ends_the_session()
    {
        var (store, server, client) = Create();
        await store.SetTokenAsync("current-token");
        var invalidated = 0;
        store.OnSessionInvalidated += () => invalidated++;

        var pending = client.GetAsync("api/courses");
        await server.Received.Task;
        server.Respond(HttpStatusCode.Unauthorized);
        using var _ = await pending;

        Assert.Equal("current-token", server.SentToken);
        Assert.Null(store.Token);
        Assert.Equal(1, invalidated);
    }

    [Fact]
    public async Task A_401_without_any_session_still_sends_the_user_to_sign_in()
    {
        var (store, server, client) = Create();
        var invalidated = 0;
        store.OnSessionInvalidated += () => invalidated++;

        var pending = client.GetAsync("api/sessions");
        await server.Received.Task;
        server.Respond(HttpStatusCode.Unauthorized);
        using var _ = await pending;

        Assert.Equal(1, invalidated);
    }

    [Fact]
    public async Task A_401_on_an_auth_path_never_ends_the_session()
    {
        var (store, server, client) = Create();
        await store.SetTokenAsync("current-token");
        var invalidated = 0;
        store.OnSessionInvalidated += () => invalidated++;

        var pending = client.PostAsync("api/auth/login/finish", null);
        await server.Received.Task;
        server.Respond(HttpStatusCode.Unauthorized);
        using var _ = await pending;

        Assert.Equal("current-token", store.Token);
        Assert.Equal(0, invalidated);
    }

    private static (SessionTokenStore Store, GatedServer Server, HttpClient Client) Create()
    {
        var store = new SessionTokenStore(new StubJSRuntime());
        var server = new GatedServer();
        var handler = new SessionHandler(store, BaseAddress, new ServiceCollection().BuildServiceProvider())
        {
            InnerHandler = server,
        };
        return (store, server, new HttpClient(handler) { BaseAddress = BaseAddress });
    }

    /// <summary>Holds the one request until the test decides how (and when) it is answered, and
    /// records the session token it carried.</summary>
    private sealed class GatedServer : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpStatusCode> _status = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? SentToken { get; private set; }

        public void Respond(HttpStatusCode status) => _status.SetResult(status);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentToken = request.Headers.TryGetValues("X-Session-Token", out var values) ? values.Single() : null;
            Received.SetResult();
            return new HttpResponseMessage(await _status.Task) { RequestMessage = request };
        }
    }
}
