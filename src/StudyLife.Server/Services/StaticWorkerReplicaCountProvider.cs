namespace StudyLife.Server.Services;

/// <summary>
/// Fixed value read from Worker:ReplicaCount at process start - default for VPS/docker-compose
/// or Kubernetes operation without HPA (must be kept manually in sync with spec.replicas there).
/// Identical behavior to the state before HPA was introduced.
/// </summary>
public sealed class StaticWorkerReplicaCountProvider : IWorkerReplicaCountProvider
{
    private readonly int _count;

    public StaticWorkerReplicaCountProvider(int count) => _count = Math.Max(1, count);

    public Task<int> GetReplicaCountAsync(CancellationToken ct) => Task.FromResult(_count);
}
