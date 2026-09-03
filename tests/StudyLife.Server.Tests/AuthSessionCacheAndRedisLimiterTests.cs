using System.Net;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using StackExchange.Redis;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

public class AuthSessionCacheTests
{
    [Fact]
    public void Hit_OnlyWhileTheSessionItselfIsStillValid()
    {
        var cache = new AuthSessionCache();
        var now = DateTime.UtcNow;
        cache.Set("hash", new AuthSessionCache.Entry(7, 1, now.AddHours(1), now.AddDays(1)));

        Assert.True(cache.TryGet("hash", now, out var entry));
        Assert.Equal(7, entry.SessionId);
        Assert.Equal(1, entry.AuthUserId);
        // The cached copy never outlives the session's own expiry columns.
        Assert.False(cache.TryGet("hash", now.AddHours(2), out _));
        Assert.False(cache.TryGet("other", now, out _));
    }

    [Fact]
    public void Remove_EvictsImmediately()
    {
        var cache = new AuthSessionCache();
        var now = DateTime.UtcNow;
        cache.Set("hash", new AuthSessionCache.Entry(7, 1, now.AddHours(1), now.AddDays(1)));

        cache.Remove("hash");

        Assert.False(cache.TryGet("hash", now, out _));
    }
}

/// <summary>
/// The handler-side integration of AuthSessionCache: a second request with the same token is
/// served without the session row (proven by deleting the row and still getting 200 within
/// the window), and an explicit logout evicts immediately regardless.
/// </summary>
public class AuthSessionCacheIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthSessionCacheIntegrationTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task SecondRequest_IsServedFromCache_EvenIfTheRowIsGone_UntilLogout()
    {
        var token = await SessionTestHelpersForCache.IssueSessionAsync(_factory);
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        client.DefaultRequestHeaders.Add(AuthSessionService.TokenHeaderName, token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/account-info")).StatusCode); // populates the cache

        var hash = AuthSessionService.HashToken(token);
        await _factory.WithDbAsync(db => db.AuthSessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync());

        // Documented staleness window: this pod still trusts the token for up to 30s.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/account-info")).StatusCode);

        // ...but an explicit logout evicts the caller's own entry right away.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/account-info")).StatusCode);
    }
}

internal static class SessionTestHelpersForCache
{
    public static Task<string> IssueSessionAsync(CustomWebApplicationFactory factory) =>
        factory.WithDbAsync(async db =>
        {
            var token = AuthSessionService.IssueSession(db, 1, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return token;
        });
}

public class RedisFixedWindowRateLimiterTests
{
    private static (RedisFixedWindowRateLimiter limiter, IDatabase db) NewLimiter(int permitLimit = 3)
    {
        var db = Substitute.For<IDatabase>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return (new RedisFixedWindowRateLimiter(mux, "api|1.2.3.4", permitLimit, TimeSpan.FromMinutes(1)), db);
    }

    [Fact]
    public async Task FirstHitOfAWindow_SetsTheExpiry_AndIsAcquired()
    {
        var (limiter, db) = NewLimiter();
        db.StringIncrementAsync(Arg.Any<RedisKey>(), 1L, Arg.Any<CommandFlags>()).Returns(1L);

        using var lease = await limiter.AcquireAsync();

        Assert.True(lease.IsAcquired);
        await db.Received(1).StringIncrementAsync((RedisKey)"ratelimit:api|1.2.3.4", 1L, Arg.Any<CommandFlags>());
        await db.Received(1).KeyExpireAsync((RedisKey)"ratelimit:api|1.2.3.4", TimeSpan.FromMinutes(1), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task LaterHitWithinLimit_DoesNotTouchTheExpiry()
    {
        var (limiter, db) = NewLimiter();
        db.StringIncrementAsync(Arg.Any<RedisKey>(), 1L, Arg.Any<CommandFlags>()).Returns(2L);

        using var lease = await limiter.AcquireAsync();

        Assert.True(lease.IsAcquired);
        await db.DidNotReceive().KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task OverTheLimit_IsRejected_WithRetryAfterFromTheKeyTtl()
    {
        var (limiter, db) = NewLimiter(permitLimit: 3);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), 1L, Arg.Any<CommandFlags>()).Returns(4L);
        db.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(TimeSpan.FromSeconds(42));

        using var lease = await limiter.AcquireAsync();

        Assert.False(lease.IsAcquired);
        Assert.True(lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(42), retryAfter);
    }

    [Fact]
    public async Task OverTheLimit_WithoutTtl_HealsTheExpiry()
    {
        var (limiter, db) = NewLimiter(permitLimit: 3);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), 1L, Arg.Any<CommandFlags>()).Returns(9L);
        db.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((TimeSpan?)null);

        using var lease = await limiter.AcquireAsync();

        Assert.False(lease.IsAcquired);
        await db.Received(1).KeyExpireAsync((RedisKey)"ratelimit:api|1.2.3.4", TimeSpan.FromMinutes(1), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task RedisFailure_FailsOpen()
    {
        var (limiter, db) = NewLimiter();
        db.StringIncrementAsync(Arg.Any<RedisKey>(), 1L, Arg.Any<CommandFlags>())
            .Returns<long>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        using var lease = await limiter.AcquireAsync();

        Assert.True(lease.IsAcquired);
    }
}
