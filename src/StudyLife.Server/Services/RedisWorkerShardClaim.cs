using StackExchange.Redis;

namespace StudyLife.Server.Services;

/// <summary>
/// Multi-worker case (Worker:ReplicaCount &gt; 1): dynamic shard claim via Redis instead of
/// deriving it from the pod name - this works identically on Kubernetes, AWS ECS Fargate, or
/// multiple VPS instances, as long as Redis is reachable (already required for
/// Cache:Provider=Redis in multi-pod operation anyway). Each slot is its own key
/// ("worker:shard:{i}") with the value being this process's random instance id and a lease TTL -
/// deliberately ONE single key per Redis call (no multi-key Lua script), because Redis Cluster
/// otherwise rejects scripts spanning multiple keys (CROSSSLOT) unless they're forced onto the
/// same slot via a hash tag. Single-key operations are the established pattern in this app (see
/// RedisVersionCounter) and cluster-safe without any workaround.
///
/// The shard COUNT comes fresh from <see cref="IWorkerReplicaCountProvider"/> on EVERY CLAIM CALL
/// instead of from a value frozen at process start - a prerequisite for safe HPA autoscaling
/// (see the comment there). A already-held shard that falls outside the new valid range due to a
/// SHRINKING replica count is no longer renewed (it expires via the lease TTL on its own) instead
/// of being actively released - this keeps the logic simple and is harmless, since no one filters
/// on this ordinal anymore once the replica count has decreased.
/// </summary>
public sealed class RedisWorkerShardClaim : IWorkerShardClaim
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(90); // 3x tick interval (30s)

    private readonly IDatabase _db;
    private readonly IWorkerReplicaCountProvider _replicaCountProvider;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    // The last held slot is remembered so that a process first renews its OWN slot on every
    // tick, instead of starting the search at 0 again every tick - avoids unnecessary jumping
    // between slots as long as its own slot remains free/owned.
    private int? _heldOrdinal;

    public int LastReplicaCount { get; private set; } = 1;

    public RedisWorkerShardClaim(IConnectionMultiplexer connectionMultiplexer, IWorkerReplicaCountProvider replicaCountProvider)
    {
        _db = connectionMultiplexer.GetDatabase();
        _replicaCountProvider = replicaCountProvider;
    }

    public async Task<int?> ClaimOrRenewAsync(CancellationToken ct)
    {
        var replicaCount = await _replicaCountProvider.GetReplicaCountAsync(ct);
        LastReplicaCount = replicaCount;

        if (_heldOrdinal is int held && held < replicaCount && await TryRenewAsync(held)) return held;

        for (var i = 0; i < replicaCount; i++)
        {
            if (await TryClaimAsync(i))
            {
                _heldOrdinal = i;
                return i;
            }
        }

        _heldOrdinal = null;
        return null;
    }

    private async Task<bool> TryRenewAsync(int ordinal)
    {
        var key = ShardKey(ordinal);
        var owner = await _db.StringGetAsync(key);
        if (owner != _instanceId) return false;
        await _db.KeyExpireAsync(key, Lease);
        return true;
    }

    private async Task<bool> TryClaimAsync(int ordinal) =>
        await _db.StringSetAsync(ShardKey(ordinal), _instanceId, Lease, When.NotExists);

    private static string ShardKey(int ordinal) => $"worker:shard:{ordinal}";
}
