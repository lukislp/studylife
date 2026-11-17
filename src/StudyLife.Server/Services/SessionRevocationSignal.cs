// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using StackExchange.Redis;

namespace StudyLife.Server.Services;

/// <summary>
/// "This session token is no longer valid" - tells every pod's <see cref="AuthSessionCache"/> to
/// drop its entry for a revoked session, so a sign-out, a "sign out other devices", a recovery
/// login or an account deletion takes effect cluster-wide at once instead of after the cache TTL
/// on the other pods (audit 2026-11-16 L9).
///
/// The message is the session's token HASH (the cache key), never the token: a hash cannot
/// authenticate anything, and it is the only identifier the evicting pod needs. Kept separate
/// from <see cref="IChangeSignal"/> on purpose: change kinds are forwarded to the user's SSE
/// stream, a revocation must not be.
/// </summary>
public interface ISessionRevocationSignal
{
    void Publish(string tokenHash);

    /// <summary>Called for every revocation, including the ones published by this pod.</summary>
    IDisposable Subscribe(Action<string> handler);
}

/// <summary>Shared subscriber bookkeeping; handlers are isolated from each other.</summary>
public abstract class SessionRevocationSignalBase : ISessionRevocationSignal
{
    private readonly ConcurrentDictionary<Guid, Action<string>> _handlers = new();

    public abstract void Publish(string tokenHash);

    public IDisposable Subscribe(Action<string> handler)
    {
        var id = Guid.NewGuid();
        _handlers[id] = handler;
        return new Subscription(() => _handlers.TryRemove(id, out _));
    }

    protected void Dispatch(string tokenHash)
    {
        foreach (var handler in _handlers.Values)
        {
            try { handler(tokenHash); } catch { /* one failing handler must not stop the others */ }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>Single process: a revocation only has to reach the local cache.</summary>
public sealed class InMemorySessionRevocationSignal : SessionRevocationSignalBase
{
    public override void Publish(string tokenHash) => Dispatch(tokenHash);
}

/// <summary>Multi-pod: one Redis pub/sub channel. The publishing pod receives its own message
/// too, so every pod evicts through the same path.</summary>
public sealed class RedisSessionRevocationSignal : SessionRevocationSignalBase
{
    public static readonly RedisChannel Channel = RedisChannel.Literal("studylife:session-revocations");

    private readonly ISubscriber _subscriber;

    public RedisSessionRevocationSignal(IConnectionMultiplexer connection)
    {
        _subscriber = connection.GetSubscriber();
        _subscriber.Subscribe(Channel, (_, message) => OnMessage(message));
    }

    // Fire and forget: the publishing pod has already evicted locally (AuthSessionCache.Remove),
    // and if Redis is briefly unreachable the other pods still drop the entry at the cache TTL.
    public override void Publish(string tokenHash) =>
        _subscriber.Publish(Channel, tokenHash, CommandFlags.FireAndForget);

    /// <summary>Exposed for tests (and for the subscriber callback above).</summary>
    public void OnMessage(string? message)
    {
        if (!string.IsNullOrEmpty(message)) Dispatch(message);
    }
}
