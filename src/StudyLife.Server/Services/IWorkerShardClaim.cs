namespace StudyLife.Server.Services;

/// <summary>
/// Determines which user partition (0..Worker:ReplicaCount-1) this worker instance is currently
/// allowed to process - the basis for partitioning in <see cref="BackgroundTaskService"/>
/// (<c>AuthUserId % ReplicaCount == Ordinal</c>). Deliberately has NO relation to pod names/
/// hostnames (that would be Kubernetes <c>StatefulSet</c>-specific and unavailable on e.g. AWS
/// ECS Fargate, since task IDs aren't sequential there) - instead a dynamic claim that works
/// identically on every platform, as long as Redis is reachable (see
/// <see cref="RedisWorkerShardClaim"/>/<see cref="StaticWorkerShardClaim"/>, analogous to the
/// <see cref="IVersionCounter"/> pattern).
/// </summary>
public interface IWorkerShardClaim
{
    /// <summary>
    /// Attempts to claim a free shard or renew the one already held.
    /// <c>null</c> only in the rare case that all shards are currently taken (e.g. briefly
    /// during a rolling update/HPA scaling event with temporarily more running processes than
    /// the current replica count) - the caller then processes no one this tick and retries on
    /// the next tick.
    /// </summary>
    Task<int?> ClaimOrRenewAsync(CancellationToken ct);

    /// <summary>
    /// The replica count that the ordinal last determined via <see cref="ClaimOrRenewAsync"/>
    /// was based on - the caller (<see cref="BackgroundTaskService"/>) needs the same value for
    /// the modulo filter (<c>AuthUserId % ReplicaCount == Ordinal</c>). Both values deliberately
    /// come from the same claim call (not from two separate queries), so they are guaranteed to
    /// be consistent within one tick, even if the replica count changes mid-tick (e.g. due to HPA).
    /// </summary>
    int LastReplicaCount { get; }
}
