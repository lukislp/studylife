using Microsoft.Extensions.Logging;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Only the off-cluster path is testable here: GetReplicaCountAsync reads ServiceAccount files
/// from a fixed absolute path (/var/run/secrets/kubernetes.io/serviceaccount) that simply doesn't
/// exist on a dev/CI machine, so every call falls into the catch branch (fail-safe: log a warning,
/// keep using the last known value). The in-cluster success path (real HTTP call against the
/// Kubernetes API with the mounted ServiceAccount token/CA) needs a real cluster and is NOT
/// covered here.
///
/// ILogger&lt;T&gt; is small enough to hand-stub per house style (three members) instead of
/// reaching for NSubstitute (reserved for the genuinely large Redis interfaces, see
/// RedisVersionCounterTests/RedisWorkerShardClaimTests).
/// </summary>
public class KubernetesWorkerReplicaCountProviderTests
{
    private sealed class FakeLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task GetReplicaCountAsync_NotRunningInAPod_ReturnsFallbackAndLogsWarning()
    {
        var logger = new FakeLogger<KubernetesWorkerReplicaCountProvider>();
        var provider = new KubernetesWorkerReplicaCountProvider("studylife-worker", fallbackReplicaCount: 3, logger);

        var count = await provider.GetReplicaCountAsync(CancellationToken.None);

        Assert.Equal(3, count);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("studylife-worker", warning.Message);
    }

    [Fact]
    public async Task GetReplicaCountAsync_FallbackBelowOne_IsClampedToOne()
    {
        var logger = new FakeLogger<KubernetesWorkerReplicaCountProvider>();
        var provider = new KubernetesWorkerReplicaCountProvider("studylife-worker", fallbackReplicaCount: 0, logger);

        Assert.Equal(1, await provider.GetReplicaCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetReplicaCountAsync_RepeatedCalls_KeepReturningTheSameLastKnownValue()
    {
        // Every call fails the same way off-cluster (no ServiceAccount files) - the fail-safe
        // design (see the class comment on KubernetesWorkerReplicaCountProvider) means it must
        // keep returning the last known value on EVERY subsequent call, not just the first.
        var logger = new FakeLogger<KubernetesWorkerReplicaCountProvider>();
        var provider = new KubernetesWorkerReplicaCountProvider("studylife-worker", fallbackReplicaCount: 4, logger);

        var first = await provider.GetReplicaCountAsync(CancellationToken.None);
        var second = await provider.GetReplicaCountAsync(CancellationToken.None);
        var third = await provider.GetReplicaCountAsync(CancellationToken.None);

        Assert.Equal(4, first);
        Assert.Equal(4, second);
        Assert.Equal(4, third);
        Assert.Equal(3, logger.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public void Dispose_BeforeAnyCall_DoesNotThrow_BecauseTheHttpClientIsBuiltLazily()
    {
        // The HttpClient is deliberately built lazily on the first GetReplicaCountAsync call (see
        // the class comment) precisely so the type can be instantiated/disposed in tests without a
        // ServiceAccount CA certificate present. Disposing without ever calling
        // GetReplicaCountAsync must be a no-op, not a NullReferenceException.
        var logger = new FakeLogger<KubernetesWorkerReplicaCountProvider>();
        var provider = new KubernetesWorkerReplicaCountProvider("studylife-worker", fallbackReplicaCount: 1, logger);

        provider.Dispose();
    }
}
