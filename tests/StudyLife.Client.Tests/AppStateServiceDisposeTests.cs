using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

/// <summary>
/// Lifetime contract of AppStateService.DisposeAsync. The service registers two callbacks on the
/// SessionTokenStore (which outlives it) and runs an open server change stream; leaving either
/// behind kept a disposed instance alive and started a second change stream on the next login.
///
/// Every wait in here is an explicit signal from ChangeStreamSpyHandler or SessionTokenStore, not
/// a delay: the change stream is issued synchronously while the service is constructed, and
/// cancellation reaches the open request synchronously inside DisposeAsync, so all of these
/// awaits observe work that has already happened. SafetyNet only turns a regression into a
/// failure instead of a hung CI job - a passing run never spends time in it.
/// </summary>
public class AppStateServiceDisposeTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(30);

    private static (AppStateService State, SessionTokenStore Store, ChangeStreamSpyHandler Handler) Build()
    {
        var js = new StubJSRuntime();
        var store = new SessionTokenStore(js);
        store.SetTokenAsync("test-token").GetAwaiter().GetResult();

        var handler = new ChangeStreamSpyHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://studylife.test/") };
        var state = new AppStateService(http, js, NullLogger<AppStateService>.Instance, store);
        return (state, store, handler);
    }

    [Fact]
    public async Task Constructor_StartsTheChangeStream_WhenATokenIsAlreadyPresent()
    {
        var (state, _, handler) = Build();
        await using (state)
        {
            await handler.Started(1).WaitAsync(SafetyNet);
            Assert.Equal(1, handler.EventRequests);
        }
    }

    [Fact]
    public async Task DisposeAsync_CancelsTheOpenChangeStream()
    {
        var (state, _, handler) = Build();
        await handler.Started(1).WaitAsync(SafetyNet);

        await state.DisposeAsync();

        // Cancellation propagates through HttpClient's linked token while Cancel() runs, so the
        // request's own token is already cancelled by the time DisposeAsync returns.
        Assert.True(handler.LastRequestToken.IsCancellationRequested);
        await handler.Cancelled(1).WaitAsync(SafetyNet);
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromOnTokenAvailable_SoNoSecondStreamIsStarted()
    {
        var (state, store, handler) = Build();
        await handler.Started(1).WaitAsync(SafetyNet);

        await state.DisposeAsync();
        await handler.Cancelled(1).WaitAsync(SafetyNet);

        // OnTokenAvailable dispatches its subscribers synchronously and in subscription order, so
        // a leftover StartChangeStream would have run - and issued its request - strictly before
        // this probe, which subscribes last. Waiting for the probe therefore replaces waiting for
        // wall-clock time: once it has run, a second stream either exists or never will.
        var reloginDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnTokenAvailable += () => reloginDispatched.TrySetResult();
        await store.SetTokenAsync("second-token");
        await reloginDispatched.Task.WaitAsync(SafetyNet);

        Assert.False(handler.Started(2).IsCompleted);
        Assert.Equal(1, handler.EventRequests);
    }

    [Fact]
    public async Task DisposeAsync_ClearsTheLogoutPurgeHookItRegistered()
    {
        var (state, store, _) = Build();
        Assert.NotNull(store.OnLoggedOutAsync);

        await state.DisposeAsync();

        Assert.Null(store.OnLoggedOutAsync);
    }

    [Fact]
    public async Task DisposeAsync_KeepsAReplacementServicesLogoutHook()
    {
        var (state, store, _) = Build();
        Func<Task> replacement = () => Task.CompletedTask;
        store.OnLoggedOutAsync = replacement;

        await state.DisposeAsync();

        Assert.Same(replacement, store.OnLoggedOutAsync);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var (state, store, handler) = Build();
        await handler.Started(1).WaitAsync(SafetyNet);

        await state.DisposeAsync();
        await state.DisposeAsync();

        Assert.Null(store.OnLoggedOutAsync);
        Assert.Equal(1, handler.EventRequests);
    }
}
