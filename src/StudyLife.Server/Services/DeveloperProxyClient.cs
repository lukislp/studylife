using System.Net.Http.Json;

namespace StudyLife.Server.Services;

/// <summary>
/// Registers/revokes the owning user's DeveloperApiKeyHash plaintext with their paired
/// studylife-developers portal, the moment it exists (SettingsController's developer-api-key
/// group) - same "toggle, not reveal" shape as AiProxyClient.RegisterKeyAsync/RevokeKeyAsync, but
/// deliberately WITHOUT an outbox/background-retry mechanism (unlike AiKeyOutboxEntity): the
/// portal is a same-operator, same-network satellite (paired to exactly one instance, unlike
/// studylife-ai's arbitrary-uptime third-party dependency), so a failed delivery simply surfaces
/// to the user immediately (SettingsController returns the outcome), who can just retry the
/// toggle - the durability cost/benefit tradeoff that justifies AI's outbox doesn't apply here.
/// Same flat-shared-secret authentication as WebhooksProxyClient (X-StudyLife-Shared-Secret) -
/// there is no per-request user-impersonation concern, every call already carries its own
/// explicit user_id.
/// </summary>
public sealed class DeveloperProxyClient
{
    private readonly ILogger<DeveloperProxyClient> _logger;
    private readonly HttpClient _http;
    private readonly string? _baseUrl;
    private readonly string? _sharedSecret;

    public DeveloperProxyClient(IConfiguration configuration, ILogger<DeveloperProxyClient> logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _http = httpClient ?? new HttpClient();
        _baseUrl = NullIfEmpty(configuration["StudyLifeDevelopers:BaseUrl"])?.TrimEnd('/');
        _sharedSecret = NullIfEmpty(configuration["StudyLifeDevelopers:SharedSecret"]);

        if (Enabled)
            _logger.LogInformation("studylife-developers integration active (BaseUrl {BaseUrl})", _baseUrl);
        else if (!string.IsNullOrEmpty(_baseUrl) || _sharedSecret is not null)
            _logger.LogWarning("studylife-developers configuration incomplete (BaseUrl and SharedSecret must both be set) - integration stays off");
    }

    public bool Enabled => !string.IsNullOrEmpty(_baseUrl) && _sharedSecret is not null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Never throws - an unreachable/unconfigured portal must not fail key generation
    /// itself, same reasoning as AiProxyClient.RegisterKeyAsync. Returns true on confirmed
    /// delivery (including the "integration not configured" no-op) or false on a genuine
    /// failure the caller surfaces to the user (no outbox retry, see the class doc).</summary>
    public Task<bool> RegisterKeyAsync(int userId, string developerApiKey, CancellationToken ct) =>
        PostInternalAsync("/internal/register-key", new { user_id = userId, api_key = developerApiKey }, ct);

    /// <summary>Same never-throws/bool-success reasoning as RegisterKeyAsync.</summary>
    public Task<bool> RevokeKeyAsync(int userId, CancellationToken ct) =>
        PostInternalAsync("/internal/revoke-key", new { user_id = userId }, ct);

    private async Task<bool> PostInternalAsync(string path, object body, CancellationToken ct)
    {
        if (!Enabled) return true; // no-op success, matches AiProxyClient's "nothing to retry there"
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}") { Content = JsonContent.Create(body) };
            request.Headers.Add("X-StudyLife-Shared-Secret", _sharedSecret);
            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("studylife-developers {Path} returned {Status}", path, response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "studylife-developers {Path} call failed", path);
            return false;
        }
    }
}
