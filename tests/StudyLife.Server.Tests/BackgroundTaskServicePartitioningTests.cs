using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// User partitioning in BackgroundTaskService.ExecuteAsync ("AuthUserId % ReplicaCount ==
/// Shard"). The shard comes dynamically from IWorkerShardClaim (StaticWorkerShardClaim for the
/// single-instance case, RedisWorkerShardClaim for the multi-worker case - see
/// IWorkerShardClaim.cs, deliberately no more reference to pod names/hostnames, so the same
/// partitioning works on any platform, not just Kubernetes StatefulSets), the
/// underlying replica count from IWorkerReplicaCountProvider (static, or live via the
/// Kubernetes API for HPA autoscaling, see IWorkerReplicaCountProvider.cs).
/// RedisWorkerShardClaim itself, like RedisVersionCounter, is not unit-testable against
/// real Redis (no Redis test container in this suite) - instead verified empirically against
/// the real Redis cluster (see docs/SCALING.md). Here: the pure partitioning math
/// (scales to any number of replicas), StaticWorkerShardClaim, and both
/// IWorkerReplicaCountProvider implementations.
/// </summary>
public class BackgroundTaskServicePartitioningTests
{
    [Fact]
    public async Task StaticWorkerShardClaim_AlwaysClaimsShardZero()
    {
        var claim = new StaticWorkerShardClaim();
        Assert.Equal(0, await claim.ClaimOrRenewAsync(CancellationToken.None));
        Assert.Equal(0, await claim.ClaimOrRenewAsync(CancellationToken.None)); // repeated call, same result
        Assert.Equal(1, claim.LastReplicaCount);
    }

    [Fact]
    public async Task StaticWorkerReplicaCountProvider_ReturnsConfiguredValue()
    {
        var provider = new StaticWorkerReplicaCountProvider(5);
        Assert.Equal(5, await provider.GetReplicaCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StaticWorkerReplicaCountProvider_ClampsToAtLeastOne()
    {
        var provider = new StaticWorkerReplicaCountProvider(0);
        Assert.Equal(1, await provider.GetReplicaCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task KubernetesWorkerReplicaCountProvider_FallsBackWhenApiUnreachable()
    {
        // Doesn't run in a real pod here - the ServiceAccount files
        // (/var/run/secrets/kubernetes.io/serviceaccount/...) don't exist, so the call must
        // fall back to the given fallback value instead of throwing (fail-safe, see the
        // KubernetesWorkerReplicaCountProvider comment) - exactly the behavior that prevents
        // a single API hiccup in the real cluster from orphaning user
        // partitions.
        var provider = new KubernetesWorkerReplicaCountProvider(
            "studylife-worker", fallbackReplicaCount: 3, NullLogger<KubernetesWorkerReplicaCountProvider>.Instance);

        var result = await provider.GetReplicaCountAsync(CancellationToken.None);

        Assert.Equal(3, result);
    }

    [Fact]
    public void PartitionFilter_ScalesToArbitraryReplicaCount()
    {
        var authUserIds = Enumerable.Range(1, 100).ToList();

        // Simulates 7 worker replicas (arbitrary number, no special case) - every AuthUserId
        // must be claimed by EXACTLY one partition, none twice, none forgotten.
        const int replicaCount = 7;
        var partitions = Enumerable.Range(0, replicaCount)
            .Select(shard => authUserIds.Where(id => id % replicaCount == shard).ToList())
            .ToList();

        Assert.Equal(authUserIds.Count, partitions.Sum(p => p.Count));
        var union = partitions.SelectMany(p => p).ToHashSet();
        Assert.Equal(authUserIds.Count, union.Count);
    }
}
