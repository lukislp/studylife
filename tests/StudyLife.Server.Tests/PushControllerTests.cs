using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Subscribe/unsubscribe persistence is checked directly via the factory's DI container
/// against StudyLifeDb instead of via the (now existing) GET listing endpoint, so these
/// tests stay independent of its DTO shape. Device management (GET/DELETE
/// /api/push/subscriptions) has its own tests further below. Actual push delivery
/// (VAPID/WebPush roundtrip to a real browser) can't be simulated here and is therefore
/// deliberately not the subject of these tests.
/// </summary>
public class PushControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PushControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<List<PushSubscriptionEntity>> GetSubscriptionsFor(string endpoint)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
        return await db.PushSubscriptions.AsNoTracking()
            .Where(s => s.Endpoint == endpoint)
            .ToListAsync();
    }

    [Fact]
    public async Task GetPublicKey_ReturnsConfiguredVapidKey()
    {
        // VAPID keys are no longer fixed via appsettings.json config (see
        // VapidKeyProvider) - CustomWebApplicationFactory generates a fresh pair per test
        // run, so no exact value comparison here, just a match against
        // the key actually registered by this factory instance.
        var response = await _client.GetAsync("/api/push/publickey");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var publicKey = document.RootElement.GetProperty("publicKey").GetString();

        Assert.False(string.IsNullOrWhiteSpace(publicKey));
        var vapidKeys = _factory.Services.GetRequiredService<VapidKeysHolder>().Keys!;
        Assert.Equal(vapidKeys.PublicKey, publicKey);
    }

    [Fact]
    public async Task Subscribe_NewEndpoint_PersistsSubscription()
    {
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        var request = new PushSubscribeRequest(endpoint, "p256dh-key-value", "auth-key-value");

        var response = await _client.PostAsJsonAsync("/api/push/subscribe", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await GetSubscriptionsFor(endpoint);
        var entity = Assert.Single(stored);
        Assert.Equal("p256dh-key-value", entity.P256dh);
        Assert.Equal("auth-key-value", entity.Auth);
    }

    [Fact]
    public async Task Subscribe_SameEndpointTwice_UpsertsInsteadOfDuplicating()
    {
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        var initial = new PushSubscribeRequest(endpoint, "old-p256dh", "old-auth");
        var updated = new PushSubscribeRequest(endpoint, "new-p256dh", "new-auth");

        var firstResponse = await _client.PostAsJsonAsync("/api/push/subscribe", initial);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        var secondResponse = await _client.PostAsJsonAsync("/api/push/subscribe", updated);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        var stored = await GetSubscriptionsFor(endpoint);
        // PushSubscriptionEntity has a {AuthUserId, Endpoint} unique index (see
        // StudyLifeDb.OnModelCreating) - a second subscribe by the SAME user for the same
        // endpoint must therefore never create a second row (a DIFFERENT user with the same
        // physical endpoint, however, does - see PushControllerMultiUserTests).
        var entity = Assert.Single(stored);
        Assert.Equal("new-p256dh", entity.P256dh);
        Assert.Equal("new-auth", entity.Auth);
    }

    [Fact]
    public async Task Unsubscribe_ExistingEndpoint_RemovesSubscription()
    {
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        var subscribeResponse = await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest(endpoint, "p256dh-key-value", "auth-key-value"));
        Assert.Equal(HttpStatusCode.OK, subscribeResponse.StatusCode);
        Assert.Single(await GetSubscriptionsFor(endpoint));

        var unsubscribeResponse = await _client.PostAsJsonAsync("/api/push/unsubscribe",
            new PushSubscribeRequest(endpoint, "p256dh-key-value", "auth-key-value"));
        Assert.Equal(HttpStatusCode.OK, unsubscribeResponse.StatusCode);

        Assert.Empty(await GetSubscriptionsFor(endpoint));
    }

    [Fact]
    public async Task Unsubscribe_UnknownEndpoint_ReturnsOkAndIsNoop()
    {
        var neverSubscribedEndpoint = $"https://push.example.com/{Guid.NewGuid():N}";

        var response = await _client.PostAsJsonAsync("/api/push/unsubscribe",
            new PushSubscribeRequest(neverSubscribedEndpoint, "p256dh-key-value", "auth-key-value"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await GetSubscriptionsFor(neverSubscribedEndpoint));
    }

    [Fact]
    public async Task Subscribe_MissingRequiredFields_ReturnsBadRequest()
    {
        // Endpoint/P256dh/Auth are non-nullable record properties with nullable context enabled -
        // ASP.NET Core implicitly treats them as [Required] (ApiBehaviorOptions default), so a JSON body
        // without these fields must fail at [ApiController]'s automatic ModelState 400,
        // before any controller code even runs.
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/push/subscribe", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Subscribe_CapturesUserAgentAndCreatedAt()
    {
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        var before = DateTime.UtcNow;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/push/subscribe")
        {
            Content = JsonContent.Create(new PushSubscribeRequest(endpoint, "p256dh-key-value", "auth-key-value"))
        };
        request.Headers.UserAgent.ParseAdd("TestAgent/1.0");
        request.Headers.UserAgent.ParseAdd("(Windows NT 10.0)");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await GetSubscriptionsFor(endpoint);
        var entity = Assert.Single(stored);
        Assert.Contains("TestAgent/1.0", entity.UserAgent);
        Assert.NotNull(entity.CreatedAt);
        Assert.True(entity.CreatedAt >= before.AddSeconds(-1));
    }

    [Fact]
    public async Task GetSubscriptions_ReturnsListWithoutSensitiveFields()
    {
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        var subscribeResponse = await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest(endpoint, "p256dh-key-value", "auth-key-value"));
        Assert.Equal(HttpStatusCode.OK, subscribeResponse.StatusCode);

        var response = await _client.GetAsync("/api/push/subscriptions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Endpoint/P256dh/Auth are sensitive push credentials and must never leave the
        // device list - raw text check instead of just DTO shape, so accidentally too-broad
        // serializer behavior (e.g. [JsonInclude] on the entity) is actually caught.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(endpoint, raw);
        Assert.DoesNotContain("p256dh-key-value", raw);
        Assert.DoesNotContain("auth-key-value", raw);

        var items = JsonSerializer.Deserialize<List<PushSubscriptionListItemDto>>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(items);

        var stored = await GetSubscriptionsFor(endpoint);
        var entity = Assert.Single(stored);
        var item = Assert.Single(items!, i => i.Id == entity.Id);

        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
        Assert.Equal(expectedHash, item.EndpointHash);
        Assert.Equal(entity.UserAgent, item.UserAgent);
        Assert.Equal(entity.CreatedAt, item.CreatedAt);
    }

    [Fact]
    public async Task DeleteSubscription_ExistingId_RemovesRowAndSecondDeleteReturnsNotFound()
    {
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest(endpoint, "p256dh-key-value", "auth-key-value"));
        var stored = await GetSubscriptionsFor(endpoint);
        var entity = Assert.Single(stored);

        var deleteResponse = await _client.DeleteAsync($"/api/push/subscriptions/{entity.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Empty(await GetSubscriptionsFor(endpoint));

        var secondDeleteResponse = await _client.DeleteAsync($"/api/push/subscriptions/{entity.Id}");
        Assert.Equal(HttpStatusCode.NotFound, secondDeleteResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteSubscription_UnknownId_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/api/push/subscriptions/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// Regression test for a bug reported live: two people logging into the same browser with
/// their own accounts get the same endpoint back from the push API (it
/// belongs to the origin, not the logged-in user) - when the second user subscribes to push,
/// the former globally-unique index on PushSubscriptionEntity.Endpoint violated uniqueness
/// (SqliteException, "UNIQUE constraint failed: PushSubscriptions.Endpoint"), because the query
/// filter hides the first user's already-existing row from the second. Own factory
/// (fresh DB), because a real two-user situation is needed via the passkey registration flow.
/// </summary>
public class PushControllerMultiUserTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PushControllerMultiUserTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Subscribe_SamePhysicalEndpoint_ForTwoDifferentUsers_CreatesTwoSeparateRows()
    {
        using var firstKey = new FakePasskey();
        var alexToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);
        using var secondKey = new FakePasskey();
        var annaToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", secondKey);

        // The same physical browser/origin returns the same endpoint for both users.
        var sharedEndpoint = $"https://push.example.com/{Guid.NewGuid():N}";

        async Task<HttpStatusCode> SubscribeAsync(string token, string p256dh)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/push/subscribe")
            {
                Content = JsonContent.Create(new PushSubscribeRequest(sharedEndpoint, p256dh, "auth-key")),
            };
            request.Headers.Add("X-Session-Token", token);
            return (await _client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await SubscribeAsync(alexToken, "alex-p256dh"));
        // Before the fix: SqliteException here (global unique index on Endpoint alone).
        Assert.Equal(HttpStatusCode.OK, await SubscribeAsync(annaToken, "anna-p256dh"));

        var rows = await _factory.WithDbAsync(async db =>
            await db.PushSubscriptions.IgnoreQueryFilters()
                .Where(s => s.Endpoint == sharedEndpoint)
                .ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.P256dh == "alex-p256dh");
        Assert.Contains(rows, r => r.P256dh == "anna-p256dh");

        // Each user still only sees their own row in their own device list.
        using var alexListRequest = new HttpRequestMessage(HttpMethod.Get, "/api/push/subscriptions");
        alexListRequest.Headers.Add("X-Session-Token", alexToken);
        var alexList = await (await _client.SendAsync(alexListRequest))
            .Content.ReadFromJsonAsync<List<PushSubscriptionListItemDto>>();
        Assert.Single(alexList!);
    }
}
