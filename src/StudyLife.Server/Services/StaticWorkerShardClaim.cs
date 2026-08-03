namespace StudyLife.Server.Services;

/// <summary>
/// Single-instance case (Worker:ReplicaCount &lt;= 1, default - Pi/docker-compose.yml or a single
/// worker in a scaled deployment): there is only one shard, always named "0", no coordination
/// needed, no Redis required. Identical to the behavior before partitioning was introduced.
/// </summary>
public sealed class StaticWorkerShardClaim : IWorkerShardClaim
{
    public int LastReplicaCount => 1;

    public Task<int?> ClaimOrRenewAsync(CancellationToken ct) => Task.FromResult<int?>(0);
}
