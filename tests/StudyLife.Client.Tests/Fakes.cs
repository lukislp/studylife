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
/// </summary>
internal sealed class ChangeStreamSpyHandler : HttpMessageHandler
{
    private int _eventRequests;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Number of GET api/events requests seen so far.</summary>
    public int EventRequests => Volatile.Read(ref _eventRequests);

    /// <summary>Completes once the change stream has issued its first request.</summary>
    public Task Started => _started.Task;

    /// <summary>Completes once an open change-stream request was cancelled.</summary>
    public Task Cancelled => _cancelled.Task;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/api/events", StringComparison.Ordinal) == true)
        {
            Interlocked.Increment(ref _eventRequests);
            using var registration = cancellationToken.Register(() => _cancelled.TrySetResult());
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request };
    }
}
