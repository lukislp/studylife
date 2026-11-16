// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Caching.Memory;

namespace StudyLife.Server.Services;

/// <summary>
/// Per-pod short-lived cache of VALID session-token lookups for StudyLifeAuthenticationHandler.
/// Every authenticated request used to resolve X-Session-Token with a DB round trip (a unique-
/// index lookup, cheap, but one more network hop on every single call - ~11 on a client cold
/// start, two per 30s poll, even for requests that end in a 304; 2026-09 audit P6). A hit here
/// answers from memory instead.
///
/// Deliberately process-local (not IDistributedCache): in Redis mode a distributed lookup would
/// only trade the DB round trip for a Redis round trip, saving DB load but no latency.
/// Revocations are not left to the TTL: <see cref="Remove"/> evicts locally AND publishes the
/// token hash through <see cref="ISessionRevocationSignal"/>, so every pod drops the entry at
/// once (audit 2026-11-16 L9). What remains is only a session deleted directly in the DB
/// without going through <see cref="Remove"/> (expiry is re-checked on every hit anyway), or a
/// revocation published while Redis is unreachable: those stay valid on other pods for up to
/// <see cref="Ttl"/>. Only successful validations are
/// cached - a flood of invalid tokens can't fill it - and the entry count is capped anyway.
/// </summary>
public sealed class AuthSessionCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>What the handler needs from AuthSessionEntity to authenticate a request without
    /// the row. ExpiresAt/HardExpiresAt are re-checked on every hit, so an entry cached seconds
    /// before the session's own expiry can never outlive it.</summary>
    public sealed record Entry(int SessionId, int AuthUserId, DateTime ExpiresAt, DateTime HardExpiresAt);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly ISessionRevocationSignal _revocations;

    public AuthSessionCache(ISessionRevocationSignal revocations)
    {
        _revocations = revocations;
        // Singleton for the process lifetime, so the subscription is never disposed.
        _revocations.Subscribe(tokenHash => _cache.Remove(tokenHash));
    }

    public bool TryGet(string tokenHash, DateTime utcNow, out Entry entry)
    {
        if (_cache.TryGetValue(tokenHash, out Entry? cached) && cached is not null
            && cached.ExpiresAt > utcNow && cached.HardExpiresAt > utcNow)
        {
            StudyLifeMetrics.AuthSessionCacheLookups.Add(1, StudyLifeMetrics.Result("hit"));
            entry = cached;
            return true;
        }
        StudyLifeMetrics.AuthSessionCacheLookups.Add(1, StudyLifeMetrics.Result("miss"));
        entry = null!;
        return false;
    }

    public void Set(string tokenHash, Entry entry) =>
        _cache.Set(tokenHash, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Ttl });

    /// <summary>The session behind <paramref name="tokenHash"/> was revoked: evict it here right
    /// away and tell every other pod to do the same.</summary>
    public void Remove(string tokenHash)
    {
        _cache.Remove(tokenHash);
        _revocations.Publish(tokenHash);
    }
}
