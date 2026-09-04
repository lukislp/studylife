using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

namespace StudyLife.Server.Services;

/// <summary>
/// Shared GET-cache pattern for SessionsController/SettingsController/CoursesController:
/// each caches a value under a key that already changes exactly when the underlying data
/// does (version-counter bump for Sessions/Settings, hourly TTL for the static Courses
/// catalog), then stamps Cache-Control so time-based clients skip the round-trip entirely.
///
/// The same key doubles as an ETag with no extra hashing: since it only changes when the
/// data does, a client's cached ETag still being current means the data is still current,
/// so an If-None-Match match can short-circuit straight to 304 before even touching the
/// cache/DB. This also covers clients that don't do time-based caching at all - e.g. the
/// Home Assistant integration's aiohttp client (custom_components/studylife/api.py) issues
/// a plain GET every poll and never looks at Cache-Control, so ETag/304 is the only way for
/// those callers to ever avoid paying for the full response body.
///
/// Has run on IDistributedCache instead of IMemoryCache since the scalability rework (Program.cs
/// registers either AddDistributedMemoryCache - default, identical behavior to before, just
/// in-memory behind the same interface - or Redis for multi-pod operation, depending on
/// Cache:Provider). IDistributedCache only stores byte[], hence the JSON serialization here;
/// the ETag/If-None-Match mechanism itself is unaffected by this.
/// </summary>
public static class CacheHelper
{
    /// <param name="clientMaxAge">
    /// Optional browser-side max-age. Default null = "no-cache": the browser must revalidate
    /// every response via If-None-Match (unchanged => bodyless 304, bandwidth still saved). A
    /// real max-age would be wrong for user data: after a save, the browser would answer an
    /// immediate refetch from ITS OWN cache without asking the server - the version counter
    /// would never take effect, and the device that just made the edit wouldn't see its own
    /// change until the TTL expires (a read-your-own-writes violation). Only useful for data
    /// that's immutable at runtime (course catalog).
    /// </param>
    public static async Task<ActionResult<T>> GetOrSetAsync<T>(
        this IDistributedCache cache,
        ControllerBase controller,
        string key,
        TimeSpan ttl,
        Func<Task<T>> factory,
        TimeSpan? clientMaxAge = null)
    {
        // The ETag is derived from the CONTENT, never from the cache key. The key embeds a
        // per-user version counter that lives in Redis; when that counter is reset (Redis rebuilt
        // from scratch, 2026-09-04) it walks through values browsers had already seen, so a
        // key-derived ETag matched a stale If-None-Match and the client kept its old body from
        // the 304 - stale settings with a stale Version, every PUT answered 409, and the user's
        // toggles silently reverted on the next poll. A content hash cannot collide that way:
        // the counter still decides which cache entry is reachable, the hash decides whether the
        // client's copy is that entry.
        var cached = await cache.GetAsync(key);
        T? result;
        byte[] bytes;
        if (cached is not null)
        {
            StudyLifeMetrics.CacheRequests.Add(1, StudyLifeMetrics.Result("hit"));
            bytes = cached;
            result = JsonSerializer.Deserialize<T>(cached);
        }
        else
        {
            StudyLifeMetrics.CacheRequests.Add(1, StudyLifeMetrics.Result("miss"));
            result = await factory();
            bytes = JsonSerializer.SerializeToUtf8Bytes(result);
            await cache.SetAsync(key, bytes, new DistributedCacheEntryOptions().SetAbsoluteExpiration(ttl));
        }

        var etag = ComputeEtag(bytes);
        if (IfNoneMatchHits(controller.Request, etag))
        {
            StudyLifeMetrics.CacheRequests.Add(1, StudyLifeMetrics.Result("not_modified"));
            SetHeaders(controller.Response, clientMaxAge, etag);
            return controller.StatusCode(StatusCodes.Status304NotModified);
        }

        SetHeaders(controller.Response, clientMaxAge, etag);
        return result!;
    }

    /// <summary>Strong ETag over the cached JSON bytes: first 16 bytes of SHA-256, hex. Identical
    /// across pods because every pod serializes the same DTO the same way, so a 304 from any
    /// replica is valid for a body served by any other.</summary>
    internal static string ComputeEtag(byte[] bytes)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return $"\"{Convert.ToHexStringLower(hash.AsSpan(0, 16))}\"";
    }

    private static bool IfNoneMatchHits(HttpRequest request, string etag)
    {
        var header = request.Headers["If-None-Match"].ToString();
        if (string.IsNullOrEmpty(header)) return false;
        return header.Split(',').Select(v => v.Trim()).Any(v => v == etag);
    }

    private static void SetHeaders(HttpResponse response, TimeSpan? clientMaxAge, string etag)
    {
        response.Headers["Cache-Control"] = clientMaxAge.HasValue
            ? $"private, max-age={(int)clientMaxAge.Value.TotalSeconds}"
            : "private, no-cache";
        response.Headers["ETag"] = etag;
    }
}
