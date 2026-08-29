using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace StudyLife.Client.Services;

/// <summary>
/// One published add-on listing, mirroring studylife-marketplace's own
/// schema/manifest.schema.json exactly (id/name/description/developer/repository/homepage?/
/// requestedScopes/redirectUriPattern). RedirectUriPattern is treated as one literal redirect
/// URI for v1, not a wildcard - see StudyLifeMarketplacePlan for the reasoning.
/// </summary>
public sealed class MarketplaceListingDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Developer { get; set; } = "";
    public string Repository { get; set; } = "";
    public string? Homepage { get; set; }
    public List<string> RequestedScopes { get; set; } = new();
    public string RedirectUriPattern { get; set; } = "";
}

/// <summary>GitHub contents-API entry shape, trimmed to the fields this client actually reads.</summary>
internal sealed class GitHubContentEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
}

/// <summary>Cache envelope persisted to localStorage - see MarketplaceClient.GetListingsAsync.</summary>
internal sealed class MarketplaceListingsCache
{
    public DateTime FetchedAtUtc { get; set; }
    public List<MarketplaceListingDto> Listings { get; set; } = new();
}

/// <summary>
/// Reads the public studylife-marketplace catalog directly from GitHub's REST API - no
/// StudyLife.Server involvement at all, since this is public, read-only, non-secret data (see
/// StudyLifeMarketplacePlan). Registered as a typed HttpClient in Program.cs with
/// BaseAddress = https://api.github.com/ and a User-Agent header (GitHub requires one on every
/// request, unauthenticated or not).
/// </summary>
public sealed class MarketplaceClient(HttpClient http, IJSRuntime js)
{
    private const string Owner = "lukislp";
    private const string Repo = "studylife-marketplace";

    // Add-ons don't get published often - a fresh fetch once a day is plenty, and keeps every
    // Setup-page visit from re-hitting GitHub's API (60 req/hour unauthenticated, shared across
    // every browser on the household's own IP). Persisted to localStorage (same pattern as
    // SessionTokenStore) so it survives across page reloads, not just this component's lifetime.
    private const string CacheKey = "studylife-marketplace-listings-cache";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<List<MarketplaceListingDto>> GetListingsAsync(CancellationToken ct = default)
    {
        var cached = await ReadCacheAsync();
        if (cached is not null && DateTime.UtcNow - cached.FetchedAtUtc < CacheDuration)
            return cached.Listings;

        var listings = await FetchListingsAsync(ct);
        await WriteCacheAsync(new MarketplaceListingsCache { FetchedAtUtc = DateTime.UtcNow, Listings = listings });
        return listings;
    }

    private async Task<List<MarketplaceListingDto>> FetchListingsAsync(CancellationToken ct)
    {
        var entries = await http.GetFromJsonAsync<List<GitHubContentEntry>>(
            $"repos/{Owner}/{Repo}/contents/listings", ct) ?? new();

        var listings = new List<MarketplaceListingDto>();
        foreach (var entry in entries)
        {
            if (!entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.DownloadUrl is not { Length: > 0 }) continue;

            var listing = await http.GetFromJsonAsync<MarketplaceListingDto>(entry.DownloadUrl, ct);
            if (listing is not null) listings.Add(listing);
        }

        return listings;
    }

    private async Task<MarketplaceListingsCache?> ReadCacheAsync()
    {
        try
        {
            var stored = await js.InvokeAsync<string?>("localStorage.getItem", CacheKey);
            return stored is { Length: > 0 } ? JsonSerializer.Deserialize<MarketplaceListingsCache>(stored, CacheJsonOptions) : null;
        }
        catch
        {
            // localStorage unavailable (private mode) or a corrupt/old-shape cache entry - just
            // refetch, same graceful degradation as SessionTokenStore's own localStorage reads.
            return null;
        }
    }

    private async Task WriteCacheAsync(MarketplaceListingsCache cache)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", CacheKey, JsonSerializer.Serialize(cache, CacheJsonOptions));
        }
        catch
        {
            // Best-effort only - a failed write just means the next open refetches instead of
            // reading a cache that was never there.
        }
    }
}
