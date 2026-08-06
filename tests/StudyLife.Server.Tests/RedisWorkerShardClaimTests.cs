using NSubstitute;
using StackExchange.Redis;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// RedisWorkerShardClaim only ever calls three IDatabase methods (StringGetAsync/KeyExpireAsync/
/// StringSetAsync) - IDatabase itself is far too large to hand-stub in this codebase's usual
/// style, so NSubstitute mocks it instead (see the csproj comment and RedisVersionCounterTests).
/// IWorkerReplicaCountProvider, in contrast, is a tiny one-method interface - hand-stubbed here
/// per house style, with a queue so a single test can simulate the replica count changing between
/// ticks (e.g. HPA scaling).
/// </summary>
public class RedisWorkerShardClaimTests
{
    /// <summary>Returns a fixed sequence of replica counts, one per call; the last value repeats
    /// once the queue is exhausted (mirrors a provider settling on its current reading).</summary>
    private sealed class FakeReplicaCountProvider(params int[] counts) : IWorkerReplicaCountProvider
    {
        private readonly Queue<int> _counts = new(counts);

        public Task<int> GetReplicaCountAsync(CancellationToken ct) =>
            Task.FromResult(_counts.Count > 1 ? _counts.Dequeue() : _counts.Peek());
    }

    private static (RedisWorkerShardClaim claim, IDatabase db) NewClaim(params int[] replicaCounts)
    {
        var db = Substitute.For<IDatabase>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        var claim = new RedisWorkerShardClaim(mux, new FakeReplicaCountProvider(replicaCounts));
        return (claim, db);
    }

    [Fact]
    public async Task ClaimOrRenewAsync_FreeShard_ClaimsOrdinalZeroFirst()
    {
        var (claim, db) = NewClaim(3);
        // Nothing is configured on StringSetAsync -> NSubstitute's default Task<bool> is false for
        // EVERY ordinal except the one we explicitly allow through, so this also proves ordinal 0
        // is tried BEFORE ordinal 1/2 (the fixed iteration order documented on ClaimOrRenewAsync).
        db.StringSetAsync((RedisKey)"worker:shard:0", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(true);

        var ordinal = await claim.ClaimOrRenewAsync(CancellationToken.None);

        Assert.Equal(0, ordinal);
        Assert.Equal(3, claim.LastReplicaCount);
    }

    [Fact]
    public async Task ClaimOrRenewAsync_FirstOrdinalTaken_FallsThroughToNextFreeOne()
    {
        var (claim, db) = NewClaim(3);
        // ordinal 0 is already occupied by someone else (When.NotExists -> false), ordinal 1 is free.
        db.StringSetAsync((RedisKey)"worker:shard:0", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(false);
        db.StringSetAsync((RedisKey)"worker:shard:1", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(true);

        var ordinal = await claim.ClaimOrRenewAsync(CancellationToken.None);

        Assert.Equal(1, ordinal);
    }

    [Fact]
    public async Task ClaimOrRenewAsync_AllShardsTaken_ReturnsNull()
    {
        var (claim, db) = NewClaim(2);
        // Default (unconfigured) StringSetAsync already returns false for every ordinal - this
        // test asserts that behavior explicitly rather than relying on it implicitly.
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(false);

        var ordinal = await claim.ClaimOrRenewAsync(CancellationToken.None);

        Assert.Null(ordinal);
        Assert.Equal(2, claim.LastReplicaCount);
    }

    [Fact]
    public async Task ClaimOrRenewAsync_SecondTick_RenewsTheOwnPreviouslyHeldSlot()
    {
        var (claim, db) = NewClaim(3, 3);
        RedisValue writtenInstanceId = default;
        db.StringSetAsync((RedisKey)"worker:shard:0", Arg.Do<RedisValue>(v => writtenInstanceId = v), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(true);

        var first = await claim.ClaimOrRenewAsync(CancellationToken.None);
        Assert.Equal(0, first);

        // Second tick: Redis is stubbed to hand back exactly what the first tick wrote as the
        // owner of slot 0 - simulating that this process really does still hold the lease.
        db.StringGetAsync((RedisKey)"worker:shard:0", Arg.Any<CommandFlags>()).Returns(writtenInstanceId);

        var second = await claim.ClaimOrRenewAsync(CancellationToken.None);

        Assert.Equal(0, second);
        // Renewal must refresh the TTL, not merely re-check ownership.
        await db.Received(1).KeyExpireAsync((RedisKey)"worker:shard:0", TimeSpan.FromSeconds(90), Arg.Any<ExpireWhen>(), Arg.Any<CommandFlags>());
        // A slot that's successfully renewed must NOT be re-claimed via StringSetAsync on the same tick.
        await db.Received(1).StringSetAsync((RedisKey)"worker:shard:0", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists);
    }

    [Fact]
    public async Task ClaimOrRenewAsync_OwnSlotStolenByAnotherInstance_FallsThroughToANewSlot()
    {
        var (claim, db) = NewClaim(3, 3);
        db.StringSetAsync((RedisKey)"worker:shard:0", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(true);

        var first = await claim.ClaimOrRenewAsync(CancellationToken.None);
        Assert.Equal(0, first);

        // Second tick: another process' lease expired ours out and grabbed slot 0 first - the
        // owner recorded in Redis is now a foreign instance id, so renewal must be refused.
        db.StringGetAsync((RedisKey)"worker:shard:0", Arg.Any<CommandFlags>()).Returns((RedisValue)"some-other-instance-id");
        // Slot 0 is (correctly) no longer claimable by us either way; slot 1 is free.
        db.StringSetAsync((RedisKey)"worker:shard:0", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(false);
        db.StringSetAsync((RedisKey)"worker:shard:1", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(true);

        var second = await claim.ClaimOrRenewAsync(CancellationToken.None);

        Assert.Equal(1, second);
        await db.DidNotReceive().KeyExpireAsync((RedisKey)"worker:shard:0", Arg.Any<TimeSpan?>(), Arg.Any<ExpireWhen>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task ClaimOrRenewAsync_ReplicaCountShrinksBelowHeldOrdinal_DoesNotRenewAndSearchesAnew()
    {
        // Held ordinal 2 was valid while replicaCount was 5; a scale-down to 2 (HPA) leaves only
        // ordinals 0/1 valid - per the class comment, slot 2 is deliberately NOT actively renewed
        // (it expires via lease TTL on its own) and the process must look for a NEW slot instead.
        var (claim, db) = NewClaim(5, 2);
        db.StringSetAsync((RedisKey)"worker:shard:2", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(true);

        var first = await claim.ClaimOrRenewAsync(CancellationToken.None);
        Assert.Equal(2, first);

        db.StringSetAsync((RedisKey)"worker:shard:0", Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(true);

        var second = await claim.ClaimOrRenewAsync(CancellationToken.None);

        Assert.Equal(0, second);
        Assert.Equal(2, claim.LastReplicaCount);
        // The old (now out-of-range) slot 2 must not even be looked at anymore this tick.
        await db.DidNotReceive().StringGetAsync((RedisKey)"worker:shard:2", Arg.Any<CommandFlags>());
    }
}
