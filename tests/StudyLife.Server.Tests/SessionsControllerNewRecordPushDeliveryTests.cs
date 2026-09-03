using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

// ── Delivery paths of SessionsController.SendNewRecordPushAsync ──────────────────────────────
// The NewRecord*Tests classes cover WHEN a record push is triggered (dedup key in
// SentReminders); these classes cover HOW it is delivered: real WebPush send (success and
// 410-Gone cleanup) and the APNs branch with an enabled sender. Same rule as over there:
// record detection compares globally across ALL sessions, so every scenario needs its own
// factory/DB (own class).

/// <summary>Shared per-class helpers - each test class passes in its own factory.</summary>
internal static class NewRecordPushDelivery
{
    public static async Task EnableNewRecordAsync(HttpClient client)
    {
        var response = await client.PutAsJsonAsync("/api/settings",
            new UserSettingsDto { NewRecordNotificationsEnabled = true });
        response.EnsureSuccessStatusCode();
    }

    public static async Task<StudySessionDto> CreateCompletedAsync(HttpClient client, DateTime start, TimeSpan duration)
    {
        var response = await client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 1,
            CourseName = "Record Delivery Test Course",
            CourseColor = "#6C5CE7",
            StartTime = start,
            EndTime = start + duration,
            IsCompleted = true,
            TimerModeId = 1,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StudySessionDto>())!;
    }

    /// <summary>Baseline (1h) + record (5h): triggers exactly one record push attempt.</summary>
    public static async Task<StudySessionDto> TriggerRecordAsync(HttpClient client)
    {
        await CreateCompletedAsync(client, DateTime.Now.AddDays(-3), TimeSpan.FromHours(1));
        return await CreateCompletedAsync(client, DateTime.Now.AddDays(-1), TimeSpan.FromHours(5));
    }

    public static Task<List<PushSubscriptionEntity>> GetSubscriptionsAsync(CustomWebApplicationFactory factory, string endpoint)
        => factory.WithDbAsync(async db =>
            await db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == endpoint).ToListAsync());

    public static Task<int> CountRecordRemindersAsync(CustomWebApplicationFactory factory, int sessionId)
        => factory.WithDbAsync(db =>
            db.SentReminders.AsNoTracking().CountAsync(r => r.Key == $"newrecord:{sessionId}"));
}

// The 201-answering success endpoint lives as the shared CreatedEndpoint helper in
// BackgroundTaskServiceDispatchTests.cs (same namespace) - the delivery tests below use
// it directly, including its RequestCount to assert the push genuinely reached it.

public class SessionsControllerNewRecordWebPushSuccessTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionsControllerNewRecordWebPushSuccessTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RecordPush_AcceptedByPushService_KeepsSubscription()
    {
        await NewRecordPushDelivery.EnableNewRecordAsync(_client);
        using var pushService = new CreatedEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, pushService.Url, p256dh, auth);

        var record = await NewRecordPushDelivery.TriggerRecordAsync(_client);

        // The push actually went over the wire and was accepted ...
        Assert.Equal(1, await NewRecordPushDelivery.CountRecordRemindersAsync(_factory, record.Id));
        Assert.True(pushService.RequestCount >= 1, "no push request reached the local push service");
        // ... and a successfully delivered subscription must of course survive.
        Assert.Single(await NewRecordPushDelivery.GetSubscriptionsAsync(_factory, pushService.Url));
    }
}

public class SessionsControllerNewRecordWebPushGoneTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionsControllerNewRecordWebPushGoneTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RecordPush_To410Endpoint_RemovesExpiredSubscription_ButStillRecordsTheRecord()
    {
        await NewRecordPushDelivery.EnableNewRecordAsync(_client);
        using var gone = new GoneEndpoint();
        var (p256dh, auth) = FakePushKeys.Generate();
        await PushTestSubscriptions.InsertAsync(_factory, gone.Url, p256dh, auth);

        var record = await NewRecordPushDelivery.TriggerRecordAsync(_client);

        // Same 410 semantics as BackgroundTaskService.SendPushAsync: the browser revoked the
        // subscription, so the instant-feedback path prunes it from the DB too ...
        Assert.Empty(await NewRecordPushDelivery.GetSubscriptionsAsync(_factory, gone.Url));
        // ... while the record itself still counts as sent (dedup entry exists).
        Assert.Equal(1, await NewRecordPushDelivery.CountRecordRemindersAsync(_factory, record.Id));
    }
}

/// <summary>
/// APNs branch of the record push with a genuinely ENABLED sender: the DI-registered
/// ApnsSender in the test host has no Apns:* config (Enabled=false), so this factory swaps
/// it for one with a temp p8 key and a stub HTTP handler that answers "Unregistered" (410) -
/// the terminal token error, which must prune the APNs subscription exactly like a web push
/// 410 (same expired-list handling in SendNewRecordPushAsync).
/// </summary>
public class SessionsControllerNewRecordApnsExpiredTests
    : IClassFixture<SessionsControllerNewRecordApnsExpiredTests.ExpiredApnsFactory>
{
    /// <summary>Answers every APNs request with 410 {"reason":"Unregistered"}.</summary>
    private sealed class UnregisteredHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent("{\"reason\":\"Unregistered\"}", Encoding.UTF8, "application/json"),
            });
    }

    public class ExpiredApnsFactory : CustomWebApplicationFactory
    {
        private readonly string _keyPath;

        public ExpiredApnsFactory()
        {
            // Same technique as ApnsPushTests.WriteTempP8Key: a real ES256 key, so the
            // sender's JWT signing runs for real - only the HTTP hop is stubbed.
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _keyPath = Path.Combine(Path.GetTempPath(), $"studylife-apns-record-{Guid.NewGuid():N}.p8");
            File.WriteAllText(_keyPath, key.ExportPkcs8PrivateKeyPem());
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(ApnsSender));
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Apns:KeyPath"] = _keyPath,
                    ["Apns:KeyId"] = "TESTKEY123",
                    ["Apns:TeamId"] = "TEAM123456",
                    ["Apns:BundleId"] = "app.studylife.mobile",
                    ["Apns:Endpoint"] = "https://apns.test",
                }).Build();
                services.AddSingleton(new ApnsSender(configuration, NullLogger<ApnsSender>.Instance,
                    new HttpClient(new UnregisteredHandler())));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                try { File.Delete(_keyPath); } catch (IOException) { /* best effort */ }
        }
    }

    private readonly ExpiredApnsFactory _factory;
    private readonly HttpClient _client;

    public SessionsControllerNewRecordApnsExpiredTests(ExpiredApnsFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RecordPush_ApnsUnregisteredToken_RemovesApnsSubscription()
    {
        await NewRecordPushDelivery.EnableNewRecordAsync(_client);
        (await _client.PostAsJsonAsync("/api/push/subscribe-apns",
            new ApnsSubscribeRequest("record-expired-token", "Test iPhone"))).EnsureSuccessStatusCode();
        Assert.Single(await NewRecordPushDelivery.GetSubscriptionsAsync(_factory, "apns:record-expired-token"));

        var record = await NewRecordPushDelivery.TriggerRecordAsync(_client);

        // Terminal APNs token error ("Unregistered") -> subscription pruned, record recorded.
        Assert.Empty(await NewRecordPushDelivery.GetSubscriptionsAsync(_factory, "apns:record-expired-token"));
        Assert.Equal(1, await NewRecordPushDelivery.CountRecordRemindersAsync(_factory, record.Id));
    }
}
