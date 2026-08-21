using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyLife.Server.Services;

/// <summary>Result of studylife-ai's POST /internal/enrich-capture (see AiProxyClient.
/// EnrichCaptureAsync) - null CourseId/Confidence means no course matched confidently enough,
/// an empty Tags/null Summary means the LLM step produced none. All fields independently
/// optional - see studylife-ai's rag/enrichment.py, each sub-step degrades on its own.</summary>
public sealed record CaptureEnrichmentResult(int? CourseId, double? CourseConfidence, List<string> Tags, string? Summary);

/// <summary>
/// Talks to the studylife-ai microservice on the logged-in user's behalf - both the live
/// /chat, /agent, /agent/confirm proxy (AiProxyController) and the key-registration
/// callbacks (SettingsController's ai-api-key group). "Enabled" gate mirrors ApnsSender: as
/// long as StudyLifeAi:BaseUrl/SharedSecret aren't both configured, the AI integration stays
/// off rather than failing the whole app - it's an optional integration, same as Home
/// Assistant/APNs.
///
/// See studylife-ai's docs/decisions.md "M4.5 Multi-user support" for the design this
/// implements: a short-lived signed proxy token (AiProxyTokenService) proves identity for
/// /chat|/agent|/agent/confirm, since the user's real AiApiKey can't be forwarded (only a
/// hash of it is ever stored here - see the ai-api-key group in SettingsController). The
/// registration callbacks separately hand studylife-ai the plaintext AiApiKey at the one
/// moment it exists (generation), so it can build a real StudyLifeClient for /agent's tool
/// calls without this backend ever forwarding it per request.
/// </summary>
public sealed class AiProxyClient
{
    private readonly ILogger<AiProxyClient> _logger;
    private readonly HttpClient _http;
    private readonly string? _baseUrl;
    private readonly string? _sharedSecret;

    public AiProxyClient(IConfiguration configuration, ILogger<AiProxyClient> logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _http = httpClient ?? new HttpClient();
        _baseUrl = configuration["StudyLifeAi:BaseUrl"]?.TrimEnd('/');
        _sharedSecret = configuration["StudyLifeAi:SharedSecret"];

        if (Enabled)
            _logger.LogInformation("studylife-ai integration active (BaseUrl {BaseUrl})", _baseUrl);
        else if (!string.IsNullOrEmpty(_baseUrl) || !string.IsNullOrEmpty(_sharedSecret))
            _logger.LogWarning("studylife-ai configuration incomplete (BaseUrl/SharedSecret must both be set) - integration stays off");
    }

    public bool Enabled => !string.IsNullOrEmpty(_baseUrl) && !string.IsNullOrEmpty(_sharedSecret);

    /// <summary>
    /// Pure reverse proxy: forwards the caller's raw request body to studylife-ai's `path`
    /// with a freshly-minted proxy token identifying `userId`, and returns the raw upstream
    /// response for the controller to relay back unchanged (status, content-type, body -
    /// including an SSE stream for /chat, via HttpCompletionOption.ResponseHeadersRead so the
    /// response starts relaying as soon as headers arrive, not after the whole body).
    /// Caller must dispose the returned response.
    /// </summary>
    public async Task<HttpResponseMessage> ProxyAsync(
        string path, int userId, Stream requestBody, string contentType, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}")
        {
            Content = new StreamContent(requestBody),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Headers.Add("X-StudyLife-Proxy-Token", AiProxyTokenService.Mint(userId, _sharedSecret!, DateTime.UtcNow));
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Registers `userId`'s real AiApiKey with studylife-ai, called the moment the
    /// plaintext exists (SettingsController.GenerateAiApiKey). Never throws - a studylife-ai
    /// outage must not fail key generation itself; the user just can't use /agent or get
    /// their notes ingested until this succeeds (logged as a warning, not surfaced to them -
    /// see docs/decisions.md "Deferred frontend UX notes" for the UI-side follow-up).</summary>
    public Task RegisterKeyAsync(int userId, string aiApiKey, CancellationToken ct) =>
        PostInternalAsync("/internal/register-key",
            new Dictionary<string, string> { ["user_id"] = userId.ToString(), ["ai_api_key"] = aiApiKey }, ct);

    /// <summary>Revokes `userId`'s key from studylife-ai's registry, called on
    /// SettingsController.RevokeAiApiKey. Same never-throws reasoning as RegisterKeyAsync.</summary>
    public Task RevokeKeyAsync(int userId, CancellationToken ct) =>
        PostInternalAsync("/internal/revoke-key",
            new Dictionary<string, string> { ["user_id"] = userId.ToString() }, ct);

    /// <summary>Requests course-match + tags/summary enrichment for one just-created capture
    /// note from studylife-ai (see BackgroundTaskService.CaptureEnrichment.cs, the only caller).
    /// Returns null on ANY failure (not Enabled, network error, non-success status, unparseable
    /// body) - same never-throws contract as RegisterKeyAsync/RevokeKeyAsync; the caller marks
    /// the note as attempted regardless (see EnrichedAt's doc comment - single attempt, no
    /// retry storm), so a null result here just means the note stays unenriched, not that
    /// anything crashes.</summary>
    public async Task<CaptureEnrichmentResult?> EnrichCaptureAsync(
        int userId, int noteId, string title, string content, string? sourceUrl, CancellationToken ct)
    {
        if (!Enabled) return null;
        try
        {
            var requestBody = new EnrichCaptureRequestJson
            {
                UserId = userId.ToString(),
                NoteId = noteId,
                Title = title,
                Content = content,
                SourceUrl = sourceUrl,
            };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/internal/enrich-capture")
            {
                Content = JsonContent.Create(requestBody),
            };
            request.Headers.Add("X-StudyLife-Shared-Secret", _sharedSecret);
            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("studylife-ai /internal/enrich-capture returned {Status} for note {NoteId}", response.StatusCode, noteId);
                return null;
            }
            var json = await response.Content.ReadFromJsonAsync<EnrichCaptureResponseJson>(cancellationToken: ct);
            if (json is null) return null;
            return new CaptureEnrichmentResult(json.CourseId, json.CourseConfidence, json.Tags ?? new List<string>(), json.Summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "studylife-ai /internal/enrich-capture call failed for note {NoteId}", noteId);
            return null;
        }
    }

    // Field names match studylife-ai's schemas/internal.py EnrichCaptureRequest/Response
    // exactly (snake_case) - same manual-matching convention as PostInternalAsync's
    // Dictionary<string,string> bodies above, not a naming-policy-driven serializer, since this
    // internal contract has never used camelCase (unlike StudyLifeNote's alias_generator on the
    // Python side, which exists specifically to match calls INTO StudyLife's own API).
    private sealed class EnrichCaptureRequestJson
    {
        [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
        [JsonPropertyName("note_id")] public int NoteId { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
        [JsonPropertyName("source_url")] public string? SourceUrl { get; set; }
    }

    private sealed class EnrichCaptureResponseJson
    {
        [JsonPropertyName("course_id")] public int? CourseId { get; set; }
        [JsonPropertyName("course_confidence")] public double? CourseConfidence { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
    }

    private async Task PostInternalAsync(string path, Dictionary<string, string> body, CancellationToken ct)
    {
        if (!Enabled) return;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-StudyLife-Shared-Secret", _sharedSecret);
            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("studylife-ai {Path} returned {Status}", path, response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "studylife-ai {Path} call failed", path);
        }
    }
}
