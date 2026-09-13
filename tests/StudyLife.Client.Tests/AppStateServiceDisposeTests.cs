using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

/// <summary>
/// Lifetime contract of AppStateService.DisposeAsync. The service registers two callbacks on the
/// SessionTokenStore (which outlives it) and runs an open server change stream; leaving either
/// behind kept a disposed instance alive and started a second change stream on the next login.
/// </summary>
public class AppStateServiceDisposeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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

    private static async Task WithTimeoutAsync(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.Same(task, completed);
        await task;
    }

    [Fact]
    public async Task Constructor_StartsTheChangeStream_WhenATokenIsAlreadyPresent()
    {
        var (state, _, handler) = Build();
        await using (state)
        {
            await WithTimeoutAsync(handler.Started);
            Assert.Equal(1, handler.EventRequests);
        }
    }

    [Fact]
    public async Task DisposeAsync_CancelsTheOpenChangeStream()
    {
        var (state, _, handler) = Build();
        await WithTimeoutAsync(handler.Started);

        await state.DisposeAsync();

        await WithTimeoutAsync(handler.Cancelled);
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromOnTokenAvailable_SoNoSecondStreamIsStarted()
    {
        var (state, store, handler) = Build();
        await WithTimeoutAsync(handler.Started);

        await state.DisposeAsync();
        await WithTimeoutAsync(handler.Cancelled);

        // A relogin on the still-living store must not resurrect the disposed service's stream.
        await store.SetTokenAsync("second-token");
        await Task.Delay(100);

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
        await WithTimeoutAsync(handler.Started);

        await state.DisposeAsync();
        await state.DisposeAsync();

        Assert.Null(store.OnLoggedOutAsync);
        Assert.Equal(1, handler.EventRequests);
    }
}
