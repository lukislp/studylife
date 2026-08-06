using NSubstitute;
using StackExchange.Redis;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// RedisVersionCounter only ever calls two IDatabase methods (StringGetAsync/StringIncrementAsync) -
/// IDatabase itself is far too large to hand-stub in this codebase's usual style (dozens of members),
/// so NSubstitute mocks it instead (see the csproj comment). IConnectionMultiplexer.GetDatabase() is
/// stubbed to return the substitute IDatabase, exactly mirroring how the constructor uses it.
/// </summary>
public class RedisVersionCounterTests
{
    private static (RedisVersionCounter counter, IDatabase db) NewCounter(string key = "version:test")
    {
        var db = Substitute.For<IDatabase>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        var counter = new RedisVersionCounter(mux, key);
        return (counter, db);
    }

    [Fact]
    public async Task GetValueAsync_MissingKey_ReturnsZero()
    {
        var (counter, db) = NewCounter();
        // Redis returns a null/"no value" RedisValue for a key that was never set - the default
        // struct value already models this (HasValue == false) without any explicit setup.
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);

        Assert.Equal(0, await counter.GetValueAsync());
    }

    [Fact]
    public async Task GetValueAsync_ExistingKey_ReturnsStoredIntValue()
    {
        var (counter, db) = NewCounter();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)42);

        Assert.Equal(42, await counter.GetValueAsync());
    }

    [Fact]
    public async Task GetValueAsync_UsesTheKeyPassedToTheConstructor()
    {
        var (counter, db) = NewCounter("version:sessionhistory");
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)7);

        await counter.GetValueAsync();

        await db.Received(1).StringGetAsync((RedisKey)"version:sessionhistory", Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task IncrementAsync_ReturnsTheNewValueFromRedisIncr()
    {
        var (counter, db) = NewCounter();
        // StringIncrementAsync is Redis' INCR - the return value IS the new value after
        // incrementing (not the old one), which is exactly what the cast-through in
        // RedisVersionCounter.IncrementAsync relies on.
        db.StringIncrementAsync(Arg.Any<RedisKey>(), 1L, Arg.Any<CommandFlags>()).Returns(1L);

        Assert.Equal(1, await counter.IncrementAsync());
    }

    [Fact]
    public async Task IncrementAsync_UsesTheKeyPassedToTheConstructor()
    {
        var (counter, db) = NewCounter("version:settings");
        db.StringIncrementAsync(Arg.Any<RedisKey>(), 1L, Arg.Any<CommandFlags>()).Returns(5L);

        await counter.IncrementAsync();

        await db.Received(1).StringIncrementAsync((RedisKey)"version:settings", 1L, Arg.Any<CommandFlags>());
    }
}
