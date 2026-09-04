using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// CacheHelper.GetOrSetAsync only needs a ControllerBase for its Request/Response/StatusCode
/// surface - no full ASP.NET Core host is required, so a bare ControllerBase with a
/// DefaultHttpContext is enough to exercise it directly.
///
/// Has run against IDistributedCache instead of IMemoryCache since the scalability rework (see
/// CacheHelper.cs) - <see cref="FakeDistributedCache"/> is a minimal, purely
/// dictionary-based test double implementation (no TTL expiry needed, all tests run
/// within the same TTL), so this test project doesn't need an extra package reference just for
/// an in-memory IDistributedCache implementation.
/// </summary>
public class GetOrSetAsyncTests
{
    private class TestController : ControllerBase { }

    private static TestController NewController(string? ifNoneMatch = null)
    {
        var httpContext = new DefaultHttpContext();
        if (ifNoneMatch != null)
            httpContext.Request.Headers["If-None-Match"] = ifNoneMatch;
        return new TestController { ControllerContext = new ControllerContext { HttpContext = httpContext } };
    }

    private static IDistributedCache NewCache() => new FakeDistributedCache();

    /// <summary>The ETag GetOrSetAsync sends for a value: a hash of its serialized JSON bytes -
    /// the same bytes it stores in the cache (see CacheHelper.ComputeEtag).</summary>
    private static string EtagOf<T>(T value) =>
        CacheHelper.ComputeEtag(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value));

    private class FakeDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new();

        public byte[]? Get(string key) => _store.TryGetValue(key, out var value) ? value : null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _store[key] = value;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _store.TryRemove(key, out _);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FirstCall_InvokesFactory_AndReturnsItsValue()
    {
        var cache = NewCache();
        var callCount = 0;

        var result = await cache.GetOrSetAsync(NewController(), "key1", TimeSpan.FromMinutes(1), () =>
        {
            callCount++;
            return Task.FromResult("value1");
        });

        Assert.Equal(1, callCount);
        Assert.Equal("value1", result.Value);
    }

    [Fact]
    public async Task SecondCall_SameKeyWithinTtl_ServesFromCache_DoesNotInvokeFactoryAgain()
    {
        var cache = NewCache();
        var callCount = 0;
        Task<string> Factory()
        {
            callCount++;
            return Task.FromResult("v");
        }

        await cache.GetOrSetAsync(NewController(), "key2", TimeSpan.FromMinutes(1), Factory);
        var second = await cache.GetOrSetAsync(NewController(), "key2", TimeSpan.FromMinutes(1), Factory);

        Assert.Equal(1, callCount);
        Assert.Equal("v", second.Value);
    }

    [Fact]
    public async Task DifferentKeys_EachInvokeTheFactoryIndependently()
    {
        var cache = NewCache();
        var callCount = 0;
        Task<string> Factory()
        {
            callCount++;
            return Task.FromResult("v");
        }

        await cache.GetOrSetAsync(NewController(), "keyA", TimeSpan.FromMinutes(1), Factory);
        await cache.GetOrSetAsync(NewController(), "keyB", TimeSpan.FromMinutes(1), Factory);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task MatchingIfNoneMatch_Returns304_OnceTheContentIsKnown()
    {
        // The ETag is derived from the cached CONTENT, not from the key (see CacheHelper.cs for
        // the Redis-counter incident behind that rule) - so on a cold cache the factory has to
        // run once to know the content, and only then can a matching If-None-Match be answered
        // with a bodyless 304.
        var controller = NewController(ifNoneMatch: EtagOf("value"));
        var cache = NewCache();
        var callCount = 0;

        var result = await cache.GetOrSetAsync(controller, "key3", TimeSpan.FromMinutes(1), () =>
        {
            callCount++;
            return Task.FromResult("value");
        });

        Assert.Equal(1, callCount);
        var statusResult = Assert.IsType<StatusCodeResult>(result.Result);
        Assert.Equal(StatusCodes.Status304NotModified, statusResult.StatusCode);
    }

    [Fact]
    public async Task MatchingIfNoneMatch_OnAWarmCache_Returns304_WithoutTheFactory()
    {
        var cache = NewCache();
        await cache.GetOrSetAsync(NewController(), "key3b", TimeSpan.FromMinutes(1), () => Task.FromResult("value"));

        var controller = NewController(ifNoneMatch: EtagOf("value"));
        var callCount = 0;
        var result = await cache.GetOrSetAsync(controller, "key3b", TimeSpan.FromMinutes(1), () =>
        {
            callCount++;
            return Task.FromResult("value");
        });

        Assert.Equal(0, callCount);
        Assert.IsType<StatusCodeResult>(result.Result);
    }

    [Fact]
    public async Task IfNoneMatch_WithMultipleCommaSeparatedValues_MatchesAnyOfThem()
    {
        var controller = NewController(ifNoneMatch: $"\"other\", {EtagOf("value")}");
        var cache = NewCache();

        var result = await cache.GetOrSetAsync(controller, "key4", TimeSpan.FromMinutes(1), () => Task.FromResult("value"));

        Assert.IsType<StatusCodeResult>(result.Result);
    }

    [Fact]
    public async Task NonMatchingIfNoneMatch_ProceedsNormally_InvokesFactory()
    {
        var controller = NewController(ifNoneMatch: "\"stale-key\"");
        var cache = NewCache();
        var callCount = 0;

        var result = await cache.GetOrSetAsync(controller, "key5", TimeSpan.FromMinutes(1), () =>
        {
            callCount++;
            return Task.FromResult("value5");
        });

        Assert.Equal(1, callCount);
        Assert.Equal("value5", result.Value);
    }

    [Fact]
    public async Task DefaultClientMaxAge_SetsNoCacheHeader_AndContentEtag()
    {
        var controller = NewController();
        var cache = NewCache();

        await cache.GetOrSetAsync(controller, "key6", TimeSpan.FromMinutes(1), () => Task.FromResult("v"));

        Assert.Equal("private, no-cache", controller.Response.Headers["Cache-Control"].ToString());
        Assert.Equal(EtagOf("v"), controller.Response.Headers["ETag"].ToString());
    }

    [Fact]
    public async Task ExplicitClientMaxAge_SetsMaxAgeHeader_InSeconds()
    {
        var controller = NewController();
        var cache = NewCache();

        await cache.GetOrSetAsync(controller, "key7", TimeSpan.FromMinutes(1), () => Task.FromResult("v"),
            clientMaxAge: TimeSpan.FromHours(1));

        Assert.Equal("private, max-age=3600", controller.Response.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public async Task Response304_StillSetsCacheControlAndETagHeaders()
    {
        var controller = NewController(ifNoneMatch: EtagOf("v"));
        var cache = NewCache();

        await cache.GetOrSetAsync(controller, "key8", TimeSpan.FromMinutes(1), () => Task.FromResult("v"));

        Assert.Equal(EtagOf("v"), controller.Response.Headers["ETag"].ToString());
        Assert.Equal("private, no-cache", controller.Response.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public async Task Etag_DependsOnContentNotOnKey()
    {
        // Regression for the Redis-counter reset: two different keys with the same body must
        // produce the same ETag, and the same key with a different body must not.
        var cache = NewCache();
        var c1 = NewController();
        await cache.GetOrSetAsync(c1, "settings:1:7", TimeSpan.FromMinutes(1), () => Task.FromResult("same"));
        var c2 = NewController();
        await cache.GetOrSetAsync(c2, "settings:1:8", TimeSpan.FromMinutes(1), () => Task.FromResult("same"));
        var c3 = NewController();
        await cache.GetOrSetAsync(c3, "settings:1:9", TimeSpan.FromMinutes(1), () => Task.FromResult("changed"));

        var etag1 = c1.Response.Headers["ETag"].ToString();
        Assert.Equal(etag1, c2.Response.Headers["ETag"].ToString());
        Assert.NotEqual(etag1, c3.Response.Headers["ETag"].ToString());
        Assert.NotEqual("\"settings:1:7\"", etag1);
    }

    [Fact]
    public async Task StaleIfNoneMatch_ForAKeyWhoseBodyChanged_GetsAFreshBodyNot304()
    {
        // The exact failure mode: the counter wrapped around to an already-seen value while the
        // client still sends the ETag it received for the OLD body under that key.
        var cache = NewCache();
        var first = NewController();
        await cache.GetOrSetAsync(first, "settings:1:3", TimeSpan.FromMinutes(1), () => Task.FromResult("old"));
        var oldEtag = first.Response.Headers["ETag"].ToString();

        var rebuiltCache = NewCache(); // same key, new content
        var second = NewController(oldEtag);
        var result = await rebuiltCache.GetOrSetAsync(second, "settings:1:3", TimeSpan.FromMinutes(1), () => Task.FromResult("new"));
        Assert.Equal("new", result.Value);
        Assert.NotEqual(StatusCodes.Status304NotModified, second.Response.StatusCode);
    }
}
