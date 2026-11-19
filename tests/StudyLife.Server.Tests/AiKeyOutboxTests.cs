// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Server.Services.Account;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// AI key outbox (identity contract v1 §3, audit A7): RegisterKeyAsync/RevokeKeyAsync used to be
/// pure fire-and-forget, so a studylife-ai outage right at generation/revocation time lost the
/// plaintext (or the revoke intent) forever and left the two databases disagreeing. Two halves
/// tested here: (1) SettingsController's ai-api-key endpoints enqueue + attempt delivery, leaving
/// a row behind on failure (via WithWebHostBuilder to swap in an AiProxyClient that fails, same
/// override technique the framework itself supports - the DI-registered instance in the shared
/// factory has no StudyLifeAi:* config and is therefore a silent no-op, not a failure); (2)
/// BackgroundTaskService.RunAiKeyOutboxAsync drains it, same direct-construction pattern as
/// BackgroundTaskServiceCaptureEnrichmentTests.
/// </summary>
public class AiKeyOutboxHttpTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AiKeyOutboxHttpTests(CustomWebApplicationFactory factory) => _factory = factory;

    /// <summary>Swaps the DI-registered AiProxyClient singleton for one that is Enabled=true but
    /// whose every call fails (500) - simulates "studylife-ai is unreachable" for exactly one
    /// HTTP request through the real controller pipeline, on the SAME temp DB as the shared
    /// factory (DbPath/BackupContentRoot are instance fields read by ConfigureWebHost, which
    /// WithWebHostBuilder still calls on `this`).</summary>
    private HttpClient CreateClientWithFailingAiProxy(out AiKeyOutboxTestStubHandler handler)
    {
        var capturedHandler = new AiKeyOutboxTestStubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler = capturedHandler;
        var overriddenFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(AiProxyClient));
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["StudyLifeAi:BaseUrl"] = "https://ai-outbox-test.invalid",
                    ["StudyLifeAi:SharedSecret"] = "shared-secret",
                }).Build();
                services.AddSingleton(new AiProxyClient(TestOptions.For<StudyLifeAiOptions>(config), NullLogger<AiProxyClient>.Instance, new HttpClient(capturedHandler)));
            });
        });
        return overriddenFactory.CreateClient();
    }

    [Fact]
    public async Task GenerateAiApiKey_WhenAiProxyUnreachable_LeavesOutboxRowWithThePlaintext()
    {
        using var client = CreateClientWithFailingAiProxy(out var handler);
        // The failing add-on gives no provider description, i.e. an unknown provider, for which
        // key generation needs a consent (AiConsentService) - recorded here for exactly that.
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, AiConsentTestHost.UnknownFingerprint());

        var response = await client.PostAsync("/api/settings/ai-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // key generation itself never fails because of this
        var generated = await response.Content.ReadFromJsonAsync<AiApiKeyGenerateResponseDto>();
        Assert.NotNull(generated);
        Assert.NotEmpty(handler.Requests); // the immediate delivery attempt really was made (and failed)

        var row = await _factory.WithDbAsync(db => db.AiKeyOutbox.AsNoTracking().FirstOrDefaultAsync());
        Assert.NotNull(row);
        Assert.Equal(AiKeyOutboxEntity.ActionRegister, row!.Action);
        Assert.Equal(generated!.ApiKey, row.AiApiKeyPlaintext);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.LastAttemptAt);

        // Cleanup: leave the shared factory's DB as this test found it (other tests in this
        // fixture share the same temp DB/AuthUser 1).
        await _factory.WithDbAsync(async db =>
        {
            await db.AiKeyOutbox.IgnoreQueryFilters().ExecuteDeleteAsync();
            var user = await db.AuthUsers.FirstAsync(u => u.Id == 1);
            user.AiApiKeyHash = null;
            user.AiApiKeyCreatedAt = null;
            await db.SaveChangesAsync();
        });
        await AiConsentTestHost.SeedUserAsync(_factory, hasKey: false, consentFingerprint: null);
    }

    [Fact]
    public async Task RevokeAiApiKey_WhenAiProxyUnreachable_LeavesOutboxRowWithoutPlaintext()
    {
        // Needs an existing key to revoke - generate one first via the normal (unconfigured,
        // Enabled=false, silent no-op) shared client so no outbox row is left behind by this step.
        var normalClient = _factory.CreateClient();
        await normalClient.PostAsync("/api/settings/ai-api-key/generate", null);

        using var client = CreateClientWithFailingAiProxy(out var handler);
        var response = await client.PostAsync("/api/settings/ai-api-key/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); // revocation itself never fails because of this
        Assert.NotEmpty(handler.Requests);

        var row = await _factory.WithDbAsync(db => db.AiKeyOutbox.AsNoTracking().FirstOrDefaultAsync());
        Assert.NotNull(row);
        Assert.Equal(AiKeyOutboxEntity.ActionRevoke, row!.Action);
        Assert.Null(row.AiApiKeyPlaintext);
        Assert.Equal(1, row.Attempts);

        await _factory.WithDbAsync(db => db.AiKeyOutbox.IgnoreQueryFilters().ExecuteDeleteAsync());
    }
}

/// <summary>Drain-side tests: same direct-construction pattern as
/// BackgroundTaskServiceCaptureEnrichmentTests (BackgroundTaskServiceTestFactory.Create with a
/// stub-backed AiProxyClient), own class so outbox rows seeded here never interact with
/// AiKeyOutboxHttpTests' real-HTTP-pipeline rows on the same shared temp DB.</summary>
public class AiKeyOutboxDrainTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AiKeyOutboxDrainTests(CustomWebApplicationFactory factory) => _factory = factory;

    private (BackgroundTaskService Service, AiKeyOutboxTestStubHandler Handler) CreateService(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StudyLifeAi:BaseUrl"] = "https://ai-outbox-drain-test.invalid",
            ["StudyLifeAi:SharedSecret"] = "shared-secret",
        }).Build();
        var handler = new AiKeyOutboxTestStubHandler(responder ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var aiProxyClient = new AiProxyClient(TestOptions.For<StudyLifeAiOptions>(config), NullLogger<AiProxyClient>.Instance, new HttpClient(handler));
        return (BackgroundTaskServiceTestFactory.Create(_factory, aiProxyClient: aiProxyClient), handler);
    }

    private async Task<int> SeedRowAsync(string action, string? plaintext, DateTime createdAt, int attempts = 0, DateTime? lastAttemptAt = null) =>
        await _factory.WithDbAsync(async db =>
        {
            var row = new AiKeyOutboxEntity
            {
                AuthUserId = 1,
                Action = action,
                AiApiKeyPlaintext = plaintext,
                CreatedAt = createdAt,
                Attempts = attempts,
                LastAttemptAt = lastAttemptAt,
            };
            db.AiKeyOutbox.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });

    private Task<List<AiKeyOutboxEntity>> LoadRowsAsync() =>
        _factory.WithDbAsync(db => db.AiKeyOutbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync());

    private Task ClearOutboxAsync() => _factory.WithDbAsync(db => db.AiKeyOutbox.ExecuteDeleteAsync());

    [Fact]
    public async Task RunAiKeyOutboxAsync_DeliversPendingRegisterRow_DeletesRowOnSuccess()
    {
        await ClearOutboxAsync();
        try
        {
            await SeedRowAsync(AiKeyOutboxEntity.ActionRegister, "plaintext-key", DateTime.UtcNow.AddMinutes(-1));
            var (service, handler) = CreateService();

            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));

            Assert.Empty(await LoadRowsAsync());
            var request = Assert.Single(handler.Requests);
            Assert.Contains("register-key", request.Uri);
            Assert.Contains("\"ai_api_key\":\"plaintext-key\"", request.Body);
        }
        finally
        {
            await ClearOutboxAsync();
        }
    }

    [Fact]
    public async Task RunAiKeyOutboxAsync_RegisterFollowingRevoke_DoesNotDeliverRegisterWhileRevokeStillFails()
    {
        // Ordering guarantee (identity contract v1 §3): a register enqueued after a revoke must
        // never be delivered while the revoke is still stuck, or studylife-ai would end up with
        // a key the user meant to revoke.
        await ClearOutboxAsync();
        try
        {
            var now = DateTime.UtcNow;
            await SeedRowAsync(AiKeyOutboxEntity.ActionRevoke, null, now.AddMinutes(-2));
            await SeedRowAsync(AiKeyOutboxEntity.ActionRegister, "plaintext-key", now.AddMinutes(-1));
            var (service, handler) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));

            var rows = await LoadRowsAsync();
            Assert.Equal(2, rows.Count); // both still pending - the register was never attempted
            Assert.Single(handler.Requests); // only the revoke (oldest row) was attempted this tick
            Assert.Contains("revoke-key", handler.Requests[0].Uri);
            Assert.Equal(1, rows.Single(r => r.Action == AiKeyOutboxEntity.ActionRevoke).Attempts);
            Assert.Equal(0, rows.Single(r => r.Action == AiKeyOutboxEntity.ActionRegister).Attempts);
        }
        finally
        {
            await ClearOutboxAsync();
        }
    }

    [Fact]
    public async Task RunAiKeyOutboxAsync_RecentlyFailedRow_NotRetriedBeforeBackoffElapses()
    {
        await ClearOutboxAsync();
        try
        {
            await SeedRowAsync(AiKeyOutboxEntity.ActionRegister, "plaintext-key", DateTime.UtcNow.AddMinutes(-5),
                attempts: 1, lastAttemptAt: DateTime.UtcNow.AddSeconds(-5)); // well within the 30s*2^1=60s backoff
            var (service, handler) = CreateService(); // would succeed if called

            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));

            Assert.Empty(handler.Requests);
            var row = Assert.Single(await LoadRowsAsync());
            Assert.Equal(1, row.Attempts);
        }
        finally
        {
            await ClearOutboxAsync();
        }
    }

    [Fact]
    public async Task RunAiKeyOutboxAsync_RetryAfterBackoffElapses_Succeeds()
    {
        await ClearOutboxAsync();
        try
        {
            await SeedRowAsync(AiKeyOutboxEntity.ActionRegister, "plaintext-key", DateTime.UtcNow.AddMinutes(-5),
                attempts: 1, lastAttemptAt: DateTime.UtcNow.AddMinutes(-2)); // well past the 60s backoff
            var (service, handler) = CreateService();

            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));

            Assert.Single(handler.Requests);
            Assert.Empty(await LoadRowsAsync());
        }
        finally
        {
            await ClearOutboxAsync();
        }
    }

    [Fact]
    public async Task RunAiKeyOutboxAsync_GivesUpAfterMaxAttempts_KeepsRowButStopsRetrying()
    {
        await ClearOutboxAsync();
        try
        {
            // 99 attempts already, long overdue (backoff caps at 1h) - this tick is attempt 100,
            // the give-up threshold.
            await SeedRowAsync(AiKeyOutboxEntity.ActionRegister, "plaintext-key", DateTime.UtcNow.AddDays(-1),
                attempts: 99, lastAttemptAt: DateTime.UtcNow.AddHours(-2));
            var (service, handler) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));

            Assert.Single(handler.Requests);
            var row = Assert.Single(await LoadRowsAsync());
            Assert.Equal(100, row.Attempts); // kept as evidence, not deleted
            Assert.Null(row.AiApiKeyPlaintext); // ... but not the credential (audit 2026-11-19 S6)

            // A second drain tick must not retry it again - it has given up.
            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));
            Assert.Single(handler.Requests); // still just the one request from the first tick
        }
        finally
        {
            await ClearOutboxAsync();
        }
    }

    [Fact]
    public async Task RunAiKeyOutboxAsync_ARowThatGaveUpEarlier_LosesItsPlaintextOnTheNextTick()
    {
        await ClearOutboxAsync();
        try
        {
            await SeedRowAsync(AiKeyOutboxEntity.ActionRegister, "legacy-plaintext-key", DateTime.UtcNow.AddDays(-30),
                attempts: 100, lastAttemptAt: DateTime.UtcNow.AddDays(-20));
            var (service, handler) = CreateService();

            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));

            Assert.Empty(handler.Requests); // still not retried
            var row = Assert.Single(await LoadRowsAsync());
            Assert.Null(row.AiApiKeyPlaintext);
        }
        finally
        {
            await ClearOutboxAsync();
        }
    }

    // ---- Revokes and purges never give up (audit F-15) ------------------------------------------

    private sealed class WarningLog : ILogger<BackgroundTaskService>
    {
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            if (logLevel == LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }

    /// <summary>A drain whose studylife-ai and studylife-mcp both answer 503.</summary>
    private (BackgroundTaskService Service, AiKeyOutboxTestStubHandler Ai, AiKeyOutboxTestStubHandler Mcp, WarningLog Log) CreateFailingPurgeService()
    {
        var ai = new AiKeyOutboxTestStubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var mcp = new AiKeyOutboxTestStubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var aiClient = new AiProxyClient(
            TestOptions.For<StudyLifeAiOptions>(("StudyLifeAi:BaseUrl", "https://ai-outbox-drain-test.invalid"), ("StudyLifeAi:SharedSecret", "s")),
            NullLogger<AiProxyClient>.Instance, new HttpClient(ai));
        var purges = new AddonPurgeClients([new AddonPurgeClient(AddonPurgeTarget.Mcp,
            new AddonPurgeOptions { BaseUrl = "https://mcp-outbox-drain-test.invalid", SharedSecret = "s" },
            NullLogger<AddonPurgeClient>.Instance, new HttpClient(mcp))]);
        var log = new WarningLog();
        var service = new BackgroundTaskService(
            new ServiceCollection().AddSingleton(purges).BuildServiceProvider(),
            _factory.Services.GetRequiredService<VapidKeysHolder>(), log,
            _factory.Services.GetRequiredService<ApnsSender>(), aiProxyClient: aiClient);
        return (service, ai, mcp, log);
    }

    private Task MakeDueAsync() => _factory.WithDbAsync(db =>
        db.AiKeyOutbox.ExecuteUpdateAsync(s => s.SetProperty(o => o.LastAttemptAt, DateTime.UtcNow.AddHours(-2))));

    [Fact]
    public async Task RunAiKeyOutboxAsync_RevokeAndPurgeRows_AreRetriedPastTheRegisterLimit()
    {
        await ClearOutboxAsync();
        try
        {
            var created = DateTime.UtcNow.AddDays(-10);
            await SeedRowAsync(AiKeyOutboxEntity.ActionRevoke, null, created, attempts: 150, lastAttemptAt: DateTime.UtcNow.AddHours(-2));
            await SeedRowAsync(AiKeyOutboxEntity.ActionPurgeMcp, null, created, attempts: 99, lastAttemptAt: DateTime.UtcNow.AddHours(-2));
            var (service, ai, mcp, log) = CreateFailingPurgeService();

            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));
            await MakeDueAsync();
            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));

            Assert.Equal(2, ai.Requests.Count);
            Assert.Equal(2, mcp.Requests.Count);
            var rows = await LoadRowsAsync();
            Assert.Equal(152, rows.Single(r => r.Action == AiKeyOutboxEntity.ActionRevoke).Attempts);
            Assert.Equal(101, rows.Single(r => r.Action == AiKeyOutboxEntity.ActionPurgeMcp).Attempts);
            Assert.Empty(log.Errors); // nothing "gave up"
        }
        finally
        {
            await ClearOutboxAsync();
        }
    }

    [Fact]
    public async Task RunAiKeyOutboxAsync_StuckPurge_WarnsAtTheThreshold_ThenAtMostOncePerDay()
    {
        await ClearOutboxAsync();
        try
        {
            var id = await SeedRowAsync(AiKeyOutboxEntity.ActionPurgeMcp, null, DateTime.UtcNow.AddDays(-1),
                attempts: AddonOutbox.WarnAfterAttempts - 2, lastAttemptAt: DateTime.UtcNow.AddHours(-2));
            var hash = AuthSessionService.HashToken("never-logged-key");
            await _factory.WithDbAsync(db => db.AiKeyOutbox.Where(o => o.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.KeyHashes, hash)));
            var (service, _, mcp, log) = CreateFailingPurgeService();

            // Attempt 9: below the threshold, no warning.
            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));
            Assert.Empty(log.Warnings);

            // Attempt 10: the threshold - one warning with ids and the count, never the hash.
            await MakeDueAsync();
            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));
            var warning = Assert.Single(log.Warnings);
            Assert.Contains($"row {id}", warning, StringComparison.Ordinal);
            Assert.Contains(AiKeyOutboxEntity.ActionPurgeMcp, warning, StringComparison.Ordinal);
            Assert.Contains($"after {AddonOutbox.WarnAfterAttempts} attempts", warning, StringComparison.Ordinal);
            Assert.DoesNotContain(hash, warning, StringComparison.Ordinal);
            Assert.NotNull((await LoadRowsAsync()).Single().FailureWarnedAt);

            // Attempt 11 on the same day: retried, but not reported again.
            await MakeDueAsync();
            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));
            Assert.Single(log.Warnings);

            // A day after the last warning: reported again.
            await MakeDueAsync();
            await _factory.WithDbAsync(db => db.AiKeyOutbox.ExecuteUpdateAsync(s =>
                s.SetProperty(o => o.FailureWarnedAt, DateTime.UtcNow.AddDays(-1).AddMinutes(-1))));
            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));
            Assert.Equal(2, log.Warnings.Count);

            Assert.Equal(4, mcp.Requests.Count);
            Assert.All(mcp.Requests, r => Assert.Contains(hash, r.Body, StringComparison.Ordinal));
        }
        finally
        {
            await ClearOutboxAsync();
        }
    }

    [Fact]
    public async Task RunAiKeyOutboxAsync_PurgeRowForAnAddonThatIsNotConfigured_CountsAsDelivered()
    {
        await ClearOutboxAsync();
        try
        {
            // The test host has no AddonPurge:* settings: studylife-telegram is not part of this
            // installation, so there is nothing to purge and the row goes without a request.
            await SeedRowAsync(AiKeyOutboxEntity.ActionPurgeTelegram, null, DateTime.UtcNow.AddMinutes(-5));
            var (service, handler) = CreateService();

            await _factory.WithDbAsync(db => service.RunAiKeyOutboxAsync(db));

            Assert.Empty(await LoadRowsAsync());
            Assert.Empty(handler.Requests);
        }
        finally
        {
            await ClearOutboxAsync();
        }
    }
}

/// <summary>Records all requests (including headers/body) and returns predefined responses -
/// same deliberately plainly-built stub as BackgroundTaskServiceCaptureEnrichmentTests/
/// AiProxyClientTests (no mocking library in this test project).</summary>
public sealed class AiKeyOutboxTestStubHandler : HttpMessageHandler
{
    public sealed record RecordedRequest(string Uri, string Body);

    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<RecordedRequest> Requests { get; } = [];

    public AiKeyOutboxTestStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
            Requests.Add(new RecordedRequest(request.RequestUri!.ToString(), body));
        return _responder(request);
    }
}
