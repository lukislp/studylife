// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Server.Services.Account;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// 2026-11-19 GDPR audit: a push registration (web push or APNs) is bound to the sign-in session
/// that registered it and is deleted by the server when that session ends - logout, "sign out
/// everywhere else", recovery login, expiry - instead of relying on the app's best-effort
/// unsubscribe call before it signs out. Plus the explicit deletion path for the Live Activity
/// push token.
/// </summary>
public class PushTokenLifecycleTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PushTokenLifecycleTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static string NewEndpoint() => $"https://push.example.com/{Guid.NewGuid():N}";

    private static string NewApnsToken() => Guid.NewGuid().ToString("N");

    private Task<List<PushSubscriptionEntity>> RowsAsync(params string[] endpoints) =>
        _factory.WithDbAsync(db => db.PushSubscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(p => endpoints.Contains(p.Endpoint)).ToListAsync());

    private Task<int> SessionIdAsync(string token) =>
        _factory.WithDbAsync(db =>
        {
            var hash = AuthSessionService.HashToken(token);
            return db.AuthSessions.Where(s => s.TokenHash == hash).Select(s => s.Id).SingleAsync();
        });

    private static async Task SubscribeWebAsync(HttpClient client, string endpoint) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest(endpoint, "p256dh-key-value", "auth-key-value"))).StatusCode);

    private static async Task SubscribeApnsAsync(HttpClient client, string token) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/push/subscribe-apns",
            new ApnsSubscribeRequest(token, "Test iPhone"))).StatusCode);

    private async Task<(HttpClient Client, string Token, int SessionId)> NewSessionAsync()
    {
        var token = await SessionTestHelpers.IssueSessionAsync(_factory);
        return (SessionTestHelpers.ClientWithSession(_factory, token), token, await SessionIdAsync(token));
    }

    [Fact]
    public async Task Subscribe_BindsTheRegistrationToTheCallingSession()
    {
        var (client, _, sessionId) = await NewSessionAsync();
        var web = NewEndpoint();
        var apnsToken = NewApnsToken();

        await SubscribeWebAsync(client, web);
        await SubscribeApnsAsync(client, apnsToken);

        var rows = await RowsAsync(web, $"apns:{apnsToken}");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(sessionId, r.AuthSessionId));
    }

    [Fact]
    public async Task Resubscribe_FromANewSession_RebindsTheSameDevice()
    {
        var (first, _, firstId) = await NewSessionAsync();
        var (second, _, secondId) = await NewSessionAsync();
        var endpoint = NewEndpoint();

        await SubscribeWebAsync(first, endpoint);
        await SubscribeWebAsync(second, endpoint);

        var row = Assert.Single(await RowsAsync(endpoint));
        Assert.NotEqual(firstId, row.AuthSessionId);
        Assert.Equal(secondId, row.AuthSessionId);
    }

    [Fact]
    public async Task Logout_DeletesTheSessionsRegistrations_AndLeavesOtherSessionsAlone()
    {
        var (a, _, _) = await NewSessionAsync();
        var (b, _, _) = await NewSessionAsync();
        var webA = NewEndpoint();
        var apnsA = NewApnsToken();
        var webB = NewEndpoint();
        await SubscribeWebAsync(a, webA);
        await SubscribeApnsAsync(a, apnsA);
        await SubscribeWebAsync(b, webB);

        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsync("/api/auth/logout", null)).StatusCode);

        Assert.Empty(await RowsAsync(webA, $"apns:{apnsA}"));
        Assert.Single(await RowsAsync(webB));
    }

    [Fact]
    public async Task Logout_KeepsRowsWithoutASessionBinding()
    {
        var (client, _, _) = await NewSessionAsync();
        var legacy = NewEndpoint();
        await _factory.WithDbAsync(async db =>
        {
            db.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                AuthUserId = 1,
                Endpoint = legacy,
                P256dh = "p",
                Auth = "a",
                CreatedAt = DateTime.UtcNow,
                AuthSessionId = null,
            });
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);

        Assert.Single(await RowsAsync(legacy));
    }

    [Fact]
    public async Task RevokeOthers_DeletesTheRevokedSessionsRegistrations_ButNotTheCallers()
    {
        var current = _factory.CreateClient(); // the factory's own seeded session
        var (other, _, _) = await NewSessionAsync();
        var mine = NewEndpoint();
        var theirs = NewEndpoint();
        var theirsApns = NewApnsToken();
        await SubscribeWebAsync(current, mine);
        await SubscribeWebAsync(other, theirs);
        await SubscribeApnsAsync(other, theirsApns);

        Assert.Equal(HttpStatusCode.NoContent, (await current.PostAsync("/api/auth/sessions/revoke-others", null)).StatusCode);

        Assert.Empty(await RowsAsync(theirs, $"apns:{theirsApns}"));
        Assert.Single(await RowsAsync(mine));
    }

    [Fact]
    public async Task RecoveryLogin_DeletesEveryPriorSessionsRegistrations()
    {
        var (other, _, otherId) = await NewSessionAsync();
        var endpoint = NewEndpoint();
        await SubscribeWebAsync(other, endpoint);

        // The session-ending step recovery login runs (AuthRecoveryService) shares this helper.
        var removed = await _factory.WithDbAsync(db => PushService.DeleteBoundToSessionsAsync(db, [otherId]));

        Assert.Equal(1, removed);
        Assert.Empty(await RowsAsync(endpoint));
    }

    [Fact]
    public async Task RetentionCleanup_DeletesRegistrationsOfExpiredSessions_ButNotUnboundOnes()
    {
        var now = DateTime.UtcNow;
        var bound = NewEndpoint();
        var unbound = NewEndpoint();
        var live = NewEndpoint();
        await _factory.WithDbAsync(async db =>
        {
            var expired = new AuthSessionEntity { AuthUserId = 1, TokenHash = $"push-exp-{Guid.NewGuid():N}", IssuedAt = now.AddDays(-100), ExpiresAt = now.AddDays(-1), HardExpiresAt = now.AddDays(80), LastUsedAt = now.AddDays(-91) };
            var alive = new AuthSessionEntity { AuthUserId = 1, TokenHash = $"push-live-{Guid.NewGuid():N}", IssuedAt = now.AddDays(-1), ExpiresAt = now.AddDays(80), HardExpiresAt = now.AddDays(170), LastUsedAt = now };
            db.AuthSessions.AddRange(expired, alive);
            await db.SaveChangesAsync();
            db.PushSubscriptions.AddRange(
                new PushSubscriptionEntity { AuthUserId = 1, Endpoint = bound, P256dh = "p", Auth = "a", CreatedAt = now, AuthSessionId = expired.Id },
                new PushSubscriptionEntity { AuthUserId = 1, Endpoint = unbound, P256dh = "p", Auth = "a", CreatedAt = now },
                new PushSubscriptionEntity { AuthUserId = 1, Endpoint = live, P256dh = "p", Auth = "a", CreatedAt = now, AuthSessionId = alive.Id });
            await db.SaveChangesAsync();
        });

        await _factory.WithDbAsync(db => new DataRetentionCleaner(new RetentionOptions()).RunAsync(db, now));

        Assert.Empty(await RowsAsync(bound));
        Assert.Single(await RowsAsync(unbound));
        Assert.Single(await RowsAsync(live));
    }
}

/// <summary>Explicit deletion of the Live Activity push token: DELETE api/timerstate/liveactivity-token.</summary>
public class LiveActivityTokenDeletionTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string Path = "/api/timerstate/liveactivity-token";
    private readonly CustomWebApplicationFactory _factory;
    private readonly AccountHttp _http;

    public LiveActivityTokenDeletionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _http = new AccountHttp(factory);
    }

    private Task<string?> StoredTokenAsync(int userId) =>
        _factory.WithDbAsync(async db =>
            (await db.TimerState.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.AuthUserId == userId))?.LiveActivityPushToken);

    private static JsonContent Token(string token) => JsonContent.Create(new LiveActivityPushTokenDto { Token = token });

    private async Task<HttpResponseMessage> PutAsync(string sessionToken, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Path) { Content = Token(token) };
        request.Headers.Add(AuthSessionService.TokenHeaderName, sessionToken);
        return await _http.Client.SendAsync(request);
    }

    [Fact]
    public async Task Delete_RemovesOnlyTheCallersOwnToken()
    {
        var (aliceToken, _, aliceId) = await _http.NewUserAsync("LaAlice");
        var (bobToken, _, bobId) = await _http.NewUserAsync("LaBob");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(aliceToken, "alice-activity-token")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(bobToken, "bob-activity-token")).StatusCode);

        var response = await _http.SendAsync(HttpMethod.Delete, Path, aliceToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await StoredTokenAsync(aliceId));
        Assert.Equal("bob-activity-token", await StoredTokenAsync(bobId));
    }

    [Fact]
    public async Task Delete_IsIdempotent_AlsoWithoutAnyStoredRow()
    {
        var (token, _, userId) = await _http.NewUserAsync("LaNone");
        Assert.Null(await StoredTokenAsync(userId));

        Assert.Equal(HttpStatusCode.NoContent, (await _http.SendAsync(HttpMethod.Delete, Path, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.SendAsync(HttpMethod.Delete, Path, token)).StatusCode);
    }

    [Fact]
    public async Task Delete_RefusesAnApiKey_LikeTheOtherSessionOnlyEndpoints()
    {
        var (token, _, userId) = await _http.NewUserAsync("LaKey");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(token, "keep-me-activity-token")).StatusCode);
        const string apiKey = "la-delete-test-api-key";
        await _factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.SingleAsync(u => u.Id == userId);
            user.ApiKeyHash = AuthSessionService.HashToken(apiKey);
            await db.SaveChangesAsync();
        });
        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await keyClient.DeleteAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("keep-me-activity-token", await StoredTokenAsync(userId));
    }

    [Fact]
    public async Task Delete_WithoutACredential_IsUnauthorized()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync(Path)).StatusCode);
    }

    [Fact]
    public Task AccountDeletion_RemovesTheLiveActivityToken_WithTheTimerStateRow()
    {
        // TimerStateEntity has an AuthUserId column, so the generic per-user delete of the account
        // deletion (UserDataCatalog.PerUserTables) covers the token; pinned here so a refactor of
        // that catalog cannot silently leave it behind.
        using var scope = _factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<StudyLifeDb>().Model;
        Assert.Contains(UserDataCatalog.PerUserTables(model), t => t.ClrType == typeof(TimerStateEntity));
        Assert.Contains(UserDataCatalog.PerUserTables(model), t => t.ClrType == typeof(PushSubscriptionEntity));
        return Task.CompletedTask;
    }
}
