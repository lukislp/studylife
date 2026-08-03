namespace StudyLife.Server.Services;

/// <summary>
/// Determines the current number of running worker replicas - the basis for the shard size in
/// <see cref="IWorkerShardClaim"/> (<c>AuthUserId % ReplicaCount == Ordinal</c>). Separate from
/// IWorkerShardClaim because the source of this number can vary independently of claim
/// coordination: a fixed configuration value (<see cref="StaticWorkerReplicaCountProvider"/>, VPS/
/// docker-compose or Kubernetes without HPA) or a live query of the current Kubernetes deployment
/// replica count (<see cref="KubernetesWorkerReplicaCountProvider"/>, a prerequisite for safe HPA
/// autoscaling of the worker - without it, scaling would compute with a stale value frozen at pod
/// start and orphan user partitions on every scaling event).
/// </summary>
public interface IWorkerReplicaCountProvider
{
    Task<int> GetReplicaCountAsync(CancellationToken ct);
}
