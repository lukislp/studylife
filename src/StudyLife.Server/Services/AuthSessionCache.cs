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
/// only trade the DB round trip for a Redis round trip, saving DB load but no latency. The
/// price is a bounded staleness window: a session deleted on another pod (or directly in the
/// DB) can keep authenticating on THIS pod for up to <see cref="Ttl"/>. Logout removes the
/// caller's own entry immediately (AuthController.Logout via <see cref="Remove"/>), so the
/// common "I clicked sign out" path has no window at all. Only successful validations are
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

    public bool TryGet(string tokenHash, DateTime utcNow, out Entry entry)
    {
        if (_cache.TryGetValue(tokenHash, out Entry? cached) && cached is not null
            && cached.ExpiresAt > utcNow && cached.HardExpiresAt > utcNow)
        {
            entry = cached;
            return true;
        }
        entry = null!;
        return false;
    }

    public void Set(string tokenHash, Entry entry) =>
        _cache.Set(tokenHash, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Ttl });

    public void Remove(string tokenHash) => _cache.Remove(tokenHash);
}
