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
        var etag = $"\"{key}\"";

        if (IfNoneMatchHits(controller.Request, etag))
        {
            SetHeaders(controller.Response, clientMaxAge, etag);
            return controller.StatusCode(StatusCodes.Status304NotModified);
        }

        var cached = await cache.GetAsync(key);
        T? result;
        if (cached is not null)
        {
            result = JsonSerializer.Deserialize<T>(cached);
        }
        else
        {
            result = await factory();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
            await cache.SetAsync(key, bytes, new DistributedCacheEntryOptions().SetAbsoluteExpiration(ttl));
        }

        SetHeaders(controller.Response, clientMaxAge, etag);
        return result!;
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
