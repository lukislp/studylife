using System.Net;
using Microsoft.JSInterop;

namespace StudyLife.Client.Tests;

/// <summary>
/// Minimal IJSRuntime that answers every call with default(TValue). SessionTokenStore and
/// AppStateService only ever use JS interop for localStorage and module imports, and neither
/// needs a real answer for the lifetime behaviour these tests cover.
/// </summary>
internal sealed class StubJSRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => ValueTask.FromResult<TValue>(default!);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => ValueTask.FromResult<TValue>(default!);
}

/// <summary>
/// Stands in for the server's SSE endpoint: GET api/events never completes on its own (like a
/// real open change stream), so the only way out of it is the CancellationToken AppStateService
/// passes in. That makes both "the stream was started" and "the stream was stopped" observable
/// from outside the service, without exposing any test-only member on it.
///
/// Every attempt is signalled through its own TaskCompletionSource pair (<see cref="Started"/> /
/// <see cref="Cancelled"/>), created on demand so a test can await attempt n either before or
/// after it happens - no polling, no sleeping, and a per-attempt signal instead of one shared
/// flag that cannot tell a first request from a second one.
///
/// The cancellation signal is raised from the catch block rather than from a
/// CancellationToken.Register callback on purpose. Cancellation callbacks run LIFO, so the
/// registration Task.Delay creates internally runs BEFORE one registered here; completing that
/// delay can resume this method inline on the cancelling thread, which would leave the using
/// scope and dispose the registration while CancellationTokenSource.Cancel is still walking the
/// list - the callback then never runs at all. Whether that inlining happens depends on the
/// scheduler, which is exactly the kind of platform-dependent flake this handler must not have.
/// </summary>
internal sealed class ChangeStreamSpyHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource> _started = new();
    private readonly Dictionary<int, TaskCompletionSource> _cancelled = new();
    private int _eventRequests;

    /// <summary>Number of GET api/events requests seen so far.</summary>
    public int EventRequests => Volatile.Read(ref _eventRequests);

    /// <summary>The CancellationToken of the most recent change-stream request. AppStateService's
    /// cancellation reaches it synchronously (through HttpClient's linked token), so a test can
    /// assert on this right after DisposeAsync returns without waiting for anything.</summary>
    public CancellationToken LastRequestToken { get; private set; }

    /// <summary>Completes once change-stream attempt <paramref name="ordinal"/> (1-based) has
    /// been issued. Safe to call before or after that happens.</summary>
    public Task Started(int ordinal) => Signal(_started, ordinal).Task;

    /// <summary>Completes once change-stream attempt <paramref name="ordinal"/> (1-based) was
    /// cancelled. Safe to call before or after that happens.</summary>
    public Task Cancelled(int ordinal) => Signal(_cancelled, ordinal).Task;

    private TaskCompletionSource Signal(Dictionary<int, TaskCompletionSource> map, int ordinal)
    {
        lock (_gate)
        {
            if (!map.TryGetValue(ordinal, out var tcs))
                map[ordinal] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return tcs;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/api/events", StringComparison.Ordinal) == true)
        {
            var ordinal = Interlocked.Increment(ref _eventRequests);
            LastRequestToken = cancellationToken;
            Signal(_started, ordinal).TrySetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Signal(_cancelled, ordinal).TrySetResult();
                throw;
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request };
    }
}
