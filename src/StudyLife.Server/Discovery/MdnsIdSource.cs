using System.Net.Http.Json;
using System.Text.Json.Serialization;
using StudyLife.Server.Services;

namespace StudyLife.Server.Discovery;

/// <summary>Where the announcement gets its instance id (the TXT record <c>id</c>) from. Returns
/// null when it is not available right now; implementations never throw for that reason.</summary>
public interface IMdnsIdSource
{
    Task<string?> TryGetIdAsync(CancellationToken cancellationToken);
}

/// <summary>Normal mode: this process owns the database and announces its own persisted id.</summary>
public sealed class ProviderMdnsIdSource(IInstanceIdProvider provider, ILogger<ProviderMdnsIdSource> logger) : IMdnsIdSource
{
    public async Task<string?> TryGetIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "mDNS: Instanz-ID konnte nicht gelesen werden; Ankündigung zunächst ohne ID.");
            return null;
        }
    }
}

/// <summary>Announcer-only mode with an explicit <c>Discovery:Mdns:Id</c>: no fetch.</summary>
public sealed class FixedMdnsIdSource(string id) : IMdnsIdSource
{
    public Task<string?> TryGetIdAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(id);
}

/// <summary>
/// Announcer-only mode: the pod has no database, so it asks the running server,
/// <c>GET {InstanceUrl}/api/instance</c>, with a short timeout. Any failure (unreachable, non-200,
/// malformed body) yields null; the hosted service retries later. The response body is never logged.
/// </summary>
public sealed class HttpMdnsIdSource(HttpClient http, Uri instanceUrl, ILogger<HttpMdnsIdSource> logger) : IMdnsIdSource
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private sealed record InstanceResponse([property: JsonPropertyName("id")] string? Id);

    public async Task<string?> TryGetIdAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var endpoint = new Uri(instanceUrl.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/api/instance");
            using var response = await http.GetAsync(endpoint, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("mDNS: GET {Url} lieferte HTTP {Status}.", endpoint, (int)response.StatusCode);
                return null;
            }
            var body = await response.Content.ReadFromJsonAsync<InstanceResponse>(timeout.Token);
            var id = body?.Id?.Trim().ToLowerInvariant();
            if (InstanceIdProvider.IsValidId(id)) return id;
            logger.LogDebug("mDNS: die Antwort von {Url} enthält keine gültige Instanz-ID.", endpoint);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            logger.LogDebug(ex, "mDNS: Instanz-ID von {Host} nicht abrufbar.", instanceUrl.Host);
            return null;
        }
    }
}
