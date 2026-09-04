using System.Collections.Concurrent;
using StackExchange.Redis;

namespace StudyLife.Server.Services;

/// <summary>Kinds published through <see cref="IChangeSignal"/> - the two things the client
/// polls for (AppStateService.PollAsync) and the two version counters that exist.</summary>
public static class ChangeKinds
{
    public const string Sessions = "sessions";
    public const string Settings = "settings";
    // Every other kind is derived by ChangeBroadcastFilter from the route of a successful write
    // (the first segment after /api: "notes", "coursegoals", "studyprograms", "sessiontemplates",
    // "courseresources", "timerstate", "backup", "system", "webhooks", ...). The client treats
    // any kind it does not know as "refetch what you show", so new controllers are covered
    // without touching the client.
}

/// <summary>
/// "Something of user X changed" - the push counterpart to the per-user version counters. The
/// counters already ARE the change signal (they bump on every write), but they were pull-only:
/// the client polled twice every 30 seconds and every pod re-read the counter from Redis per
/// request. Publishing the bump lets (a) the SSE endpoint (EventsController) tell connected
/// clients to refetch right away instead of on the next poll, and (b) every pod drop its local
/// copy of the counter (SignalInvalidatedVersionCounter) instead of asking Redis on every GET.
///
/// Two implementations: process-local for the single-instance/demo host, Redis pub/sub for
/// multi-pod operation (a PUBLISH reaches all replicas including the publishing one). Payloads
/// carry no data - only "user id + kind" - so nothing sensitive crosses the channel and the
/// receiver always refetches through the normal, authenticated API.
/// </summary>
public interface IChangeSignal
{
    Task PublishAsync(int authUserId, string kind);

    /// <summary>Per-user subscription (the SSE stream of one connected client).</summary>
    IDisposable Subscribe(int authUserId, Action<string> handler);

    /// <summary>Cross-user subscription (local version-counter invalidation on every pod).</summary>
    IDisposable SubscribeAll(Action<int, string> handler);
}

/// <summary>Shared subscriber bookkeeping for both implementations.</summary>
public abstract class ChangeSignalBase : IChangeSignal
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, Action<string>>> _perUser = new();
    private readonly ConcurrentDictionary<Guid, Action<int, string>> _all = new();

    public abstract Task PublishAsync(int authUserId, string kind);

    public IDisposable Subscribe(int authUserId, Action<string> handler)
    {
        var id = Guid.NewGuid();
        _perUser.GetOrAdd(authUserId, _ => new ConcurrentDictionary<Guid, Action<string>>())[id] = handler;
        return new Subscription(() =>
        {
            if (_perUser.TryGetValue(authUserId, out var handlers))
            {
                handlers.TryRemove(id, out _);
                if (handlers.IsEmpty) _perUser.TryRemove(authUserId, out _);
            }
        });
    }

    public IDisposable SubscribeAll(Action<int, string> handler)
    {
        var id = Guid.NewGuid();
        _all[id] = handler;
        return new Subscription(() => _all.TryRemove(id, out _));
    }

    /// <summary>Fan a change out to the local subscribers. Handlers are isolated from each other:
    /// a throwing SSE writer must never stop the other listeners (or the publisher) from seeing
    /// the change.</summary>
    protected void Dispatch(int authUserId, string kind)
    {
        foreach (var handler in _all.Values)
        {
            try { handler(authUserId, kind); } catch { /* isolated, see summary */ }
        }
        if (_perUser.TryGetValue(authUserId, out var handlers))
        {
            foreach (var handler in handlers.Values)
            {
                try { handler(kind); } catch { /* isolated, see summary */ }
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>Single-process implementation: publish is a synchronous local fan-out.</summary>
public sealed class InMemoryChangeSignal : ChangeSignalBase
{
    public override Task PublishAsync(int authUserId, string kind)
    {
        Dispatch(authUserId, kind);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Multi-pod implementation over one Redis pub/sub channel. Message format "userId:kind".
/// PUBLISH in cluster mode is broadcast to every node, so a subscriber connected to any node
/// receives it; the publishing pod receives its own message too and dispatches locally through
/// the same path as everyone else (no special-casing "self", one code path).
/// </summary>
public sealed class RedisChangeSignal : ChangeSignalBase
{
    public static readonly RedisChannel Channel = RedisChannel.Literal("studylife:changes");

    private readonly ISubscriber _subscriber;

    public RedisChangeSignal(IConnectionMultiplexer connection)
    {
        _subscriber = connection.GetSubscriber();
        _subscriber.Subscribe(Channel, (_, message) => OnMessage(message));
    }

    public override Task PublishAsync(int authUserId, string kind) =>
        _subscriber.PublishAsync(Channel, $"{authUserId}:{kind}");

    /// <summary>Exposed for tests (and for the subscriber callback above).</summary>
    public void OnMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var separator = message.IndexOf(':');
        if (separator <= 0 || !int.TryParse(message.AsSpan(0, separator), out var userId)) return;
        Dispatch(userId, message[(separator + 1)..]);
    }
}
