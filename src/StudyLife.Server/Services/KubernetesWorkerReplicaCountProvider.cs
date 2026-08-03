using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace StudyLife.Server.Services;

/// <summary>
/// Queries the current replica count of the studylife-worker deployment live via the Kubernetes
/// API (scale subresource, returns only {"spec":{"replicas":N}} - leaner than the full deployment
/// object) instead of relying on a configuration value frozen at pod start. Prerequisite for safe
/// HPA autoscaling of the worker (see the IWorkerReplicaCountProvider comment) - without this, a
/// scaled-up or scaled-down pod would keep computing with the OLD replica count and orphan user
/// partitions.
///
/// Deliberately uses a raw HttpClient + the ServiceAccount identity mounted in the pod instead of
/// a full Kubernetes client NuGet package - for a single GET query that would be a heavy
/// dependency for little benefit. The token is read fresh on EVERY call (K8s rotates it
/// periodically, one-time caching would run into a 401 after rotation).
///
/// On any error (API unreachable, RBAC missing, ServiceAccount files missing because not running
/// in a pod at all, parsing error) the last known value keeps being used (fail-safe instead of
/// fail-fast: a single API hiccup must not cause the worker to suddenly believe there are 0 or 1
/// replicas and incorrectly leave user partitions unprocessed) - only if a successful fetch has
/// NEVER succeeded does the supplied default (Worker:ReplicaCount, usually 1) apply. The
/// HttpClient is deliberately built LAZILY (only on the first call to GetReplicaCountAsync)
/// instead of in the constructor, so this class can also be instantiated outside a real pod
/// (e.g. in tests) without immediately failing due to the missing ServiceAccount CA certificate -
/// the fail-safe behavior then already applies on the very first call.
/// </summary>
public sealed class KubernetesWorkerReplicaCountProvider : IWorkerReplicaCountProvider, IDisposable
{
    private const string ServiceAccountDir = "/var/run/secrets/kubernetes.io/serviceaccount";

    private readonly string _deploymentName;
    private readonly ILogger<KubernetesWorkerReplicaCountProvider> _logger;
    private int _lastKnownReplicaCount;
    private HttpClient? _httpClient;

    public KubernetesWorkerReplicaCountProvider(
        string deploymentName,
        int fallbackReplicaCount,
        ILogger<KubernetesWorkerReplicaCountProvider> logger)
    {
        _deploymentName = deploymentName;
        _lastKnownReplicaCount = Math.Max(1, fallbackReplicaCount);
        _logger = logger;
    }

    public async Task<int> GetReplicaCountAsync(CancellationToken ct)
    {
        try
        {
            var httpClient = _httpClient ??= BuildHttpClient();

            var ns = (await File.ReadAllTextAsync(Path.Combine(ServiceAccountDir, "namespace"), ct)).Trim();
            var token = (await File.ReadAllTextAsync(Path.Combine(ServiceAccountDir, "token"), ct)).Trim();

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/apis/apps/v1/namespaces/{ns}/deployments/{_deploymentName}/scale");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var replicas = doc.RootElement.GetProperty("spec").GetProperty("replicas").GetInt32();

            _lastKnownReplicaCount = Math.Max(1, replicas);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not fetch the current replica count for deployment {DeploymentName} from the Kubernetes API - using last known value {LastKnown}",
                _deploymentName, _lastKnownReplicaCount);
        }

        return _lastKnownReplicaCount;
    }

    private static HttpClient BuildHttpClient()
    {
        var caCertPath = Path.Combine(ServiceAccountDir, "ca.crt");
        var caCert = X509CertificateLoader.LoadCertificateFromFile(caCertPath);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, chain, _) =>
            {
                if (cert is null || chain is null) return false;
                chain.ChainPolicy.ExtraStore.Add(caCert);
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(caCert);
                return chain.Build(cert);
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri("https://kubernetes.default.svc") };
    }

    public void Dispose() => _httpClient?.Dispose();
}
