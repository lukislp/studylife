using System.ComponentModel.DataAnnotations;

namespace StudyLife.Server.Configuration;

/// <summary>
/// Background worker section (see <see cref="Services.BackgroundTaskService"/> and
/// docs/ARCHITECTURE.md "Background services"). The defaults are the single-container case:
/// worker on, exactly one replica, a static replica count.
/// </summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public const string StaticReplicaCountSource = "Static";
    public const string KubernetesReplicaCountSource = "Kubernetes";

    /// <summary>False turns a process into a pure web-only replica with no subtask running at
    /// all - what separates the stateless `server` pods from the `worker` pod.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Number of worker processes the user set is partitioned across. Validated at
    /// startup (ValidateDataAnnotations/ValidateOnStart in
    /// <see cref="StudyLifeOptionsRegistration"/>): 0 or a negative value could never have
    /// produced a working partition ("id % ReplicaCount") and is now refused with a readable
    /// message instead of surfacing much later inside the tick loop.</summary>
    [Range(1, int.MaxValue)]
    public int ReplicaCount { get; set; } = 1;

    /// <summary>"Static" (the frozen <see cref="ReplicaCount"/>) or "Kubernetes" (live query of
    /// the deployment's replica count, a prerequisite for safe HPA autoscaling).</summary>
    public string ReplicaCountSource { get; set; } = StaticReplicaCountSource;

    /// <summary>Deployment queried when <see cref="ReplicaCountSource"/> is "Kubernetes".</summary>
    public string DeploymentName { get; set; } = "studylife-worker";

    public bool UsesKubernetesReplicaCount =>
        string.Equals(ReplicaCountSource, KubernetesReplicaCountSource, StringComparison.OrdinalIgnoreCase);
}
