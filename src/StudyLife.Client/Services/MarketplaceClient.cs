using System.Net.Http.Json;
using System.Text.Json.Serialization;

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

/// <summary>
/// Reads the public studylife-marketplace catalog directly from GitHub's REST API - no
/// StudyLife.Server involvement at all, since this is public, read-only, non-secret data (see
/// StudyLifeMarketplacePlan). Registered as a typed HttpClient in Program.cs with
/// BaseAddress = https://api.github.com/ and a User-Agent header (GitHub requires one on every
/// request, unauthenticated or not).
/// </summary>
public sealed class MarketplaceClient(HttpClient http)
{
    private const string Owner = "lukislp";
    private const string Repo = "studylife-marketplace";

    /// <summary>
    /// Lists every published add-on. Unauthenticated GitHub API calls are capped at 60/hour per
    /// source IP - more than enough at the personal/household scale every StudyLife instance
    /// runs at, so no caching or auth token is needed here.
    /// </summary>
    public async Task<List<MarketplaceListingDto>> GetListingsAsync(CancellationToken ct = default)
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
}
