using Microsoft.Extensions.Caching.Memory;

namespace StudyLife.Server.Services;

/// <summary>
/// Bounded, process-local cache for synthesized "read note aloud" WAV audio (TtsController).
///
/// This used to live in the shared IDistributedCache with a 24h TTL. A minute of speech is
/// several megabytes of uncompressed PCM, and in multi-pod mode that cache is a 96 MB
/// allkeys-lru Redis shared with the WebAuthn challenges, consent assertions, DataProtection
/// key ring and worker shard leases - a handful of TTS calls could evict live login challenges
/// (2026-09 audit). On the single-instance/demo host the distributed cache is an unbounded
/// in-process MemoryCache, so visitors could grow the process until the next restart. A
/// dedicated MemoryCache with a hard SizeLimit (Tts:CacheSizeMb, default 32) fixes both: audio
/// never competes with coordination state, and the worst case is a re-synthesis. Per pod rather
/// than shared is the right trade-off here - synthesis is CPU work local to the pod anyway, and
/// a cross-pod hit rate for the same note in the same language is negligible.
/// </summary>
public sealed class TtsAudioCache
{
    private readonly MemoryCache _cache;
    private readonly long _sizeLimitBytes;

    public TtsAudioCache(long sizeLimitBytes)
    {
        _sizeLimitBytes = sizeLimitBytes;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = sizeLimitBytes });
    }

    public bool TryGet(string key, out byte[] wav) => _cache.TryGetValue(key, out wav!);

    /// <summary>Stores the audio unless it alone would exceed the whole budget (then it is simply
    /// served once and not retained - a single enormous note must not flush everything else).</summary>
    public void Set(string key, byte[] wav, TimeSpan ttl)
    {
        if (wav.Length > _sizeLimitBytes / 2) return;
        _cache.Set(key, wav, new MemoryCacheEntryOptions { Size = wav.Length, AbsoluteExpirationRelativeToNow = ttl });
    }
}
