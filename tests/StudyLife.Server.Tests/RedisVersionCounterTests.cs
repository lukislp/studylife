using NSubstitute;
using StackExchange.Redis;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// RedisVersionCounter only ever calls two IDatabase methods (StringGetAsync/StringIncrementAsync) -
/// IDatabase itself is far too large to hand-stub in this codebase's usual style (dozens of members),
/// so NSubstitute mocks it instead (see the csproj comment). IConnectionMultiplexer.GetDatabase() is
/// stubbed to return the substitute IDatabase, exactly mirroring how the constructor uses it.
/// Keys are per user since the 2026-09 audit: the constructor takes a prefix, every call a key.
/// </summary>
public class RedisVersionCounterTests
{
    private static (RedisVersionCounter counter, IDatabase db) NewCounter(string prefix = "version:test")
    {
        var db = Substitute.For<IDatabase>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        var counter = new RedisVersionCounter(mux, prefix);
        return (counter, db);
    }

    [Fact]
    public async Task GetValueAsync_MissingKey_ReturnsZero()
    {
        var (counter, db) = NewCounter();
        // Redis returns a null/"no value" RedisValue for a key that was never set - the default
        // struct value already models this (HasValue == false) without any explicit setup.
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);

        Assert.Equal(0, await counter.GetValueAsync("1"));
    }

    [Fact]
    public async Task GetValueAsync_ExistingKey_ReturnsStoredIntValue()
    {
        var (counter, db) = NewCounter();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)42);

        Assert.Equal(42, await counter.GetValueAsync("1"));
    }

    [Fact]
    public async Task GetValueAsync_UsesPrefixAndKey()
    {
        var (counter, db) = NewCounter("version:sessionhistory");
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)7);

        await counter.GetValueAsync("42");

        await db.Received(1).StringGetAsync((RedisKey)"version:sessionhistory:42", Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task IncrementAsync_ReturnsTheNewValueFromRedisIncr()
    {
        var (counter, db) = NewCounter();
        // StringIncrementAsync is Redis' INCR - the return value IS the new value after
        // incrementing (not the old one), which is exactly what the cast-through in
        // RedisVersionCounter.IncrementAsync relies on.
        db.StringIncrementAsync(Arg.Any<RedisKey>(), 1L, Arg.Any<CommandFlags>()).Returns(1L);

        Assert.Equal(1, await counter.IncrementAsync("1"));
    }

    [Fact]
    public async Task IncrementAsync_UsesPrefixAndKey()
    {
        var (counter, db) = NewCounter("version:settings");
        db.StringIncrementAsync(Arg.Any<RedisKey>(), 1L, Arg.Any<CommandFlags>()).Returns(5L);

        await counter.IncrementAsync("42");

        await db.Received(1).StringIncrementAsync((RedisKey)"version:settings:42", 1L, Arg.Any<CommandFlags>());
    }
}

public class InMemoryVersionCounterTests
{
    [Fact]
    public async Task Keys_AreIndependent()
    {
        var counter = new InMemoryVersionCounter();

        Assert.Equal(0, await counter.GetValueAsync("1"));
        Assert.Equal(1, await counter.IncrementAsync("1"));
        Assert.Equal(2, await counter.IncrementAsync("1"));
        Assert.Equal(2, await counter.GetValueAsync("1"));
        Assert.Equal(0, await counter.GetValueAsync("2")); // another user's counter is untouched
    }

    [Fact]
    public async Task CacheVersionFacades_BumpOnlyTheGivenUser()
    {
        var sessions = new SessionHistoryCacheVersion(new InMemoryVersionCounter());

        await sessions.BumpAsync(1);
        await sessions.BumpAsync(1);

        Assert.Equal(2, await sessions.GetAsync(1));
        Assert.Equal(0, await sessions.GetAsync(2));
    }
}

public class TtsAudioCacheTests
{
    [Fact]
    public void Set_StoresWithinBudget_AndRefusesASingleOversizedEntry()
    {
        var cache = new TtsAudioCache(sizeLimitBytes: 1000);

        cache.Set("small", new byte[400], TimeSpan.FromMinutes(1));
        Assert.True(cache.TryGet("small", out var small));
        Assert.Equal(400, small.Length);

        // Larger than half the budget: served once, never retained (would evict everything else).
        cache.Set("huge", new byte[600], TimeSpan.FromMinutes(1));
        Assert.False(cache.TryGet("huge", out _));
        Assert.True(cache.TryGet("small", out _));
    }
}
