using System.Diagnostics;
using System.Net.Http.Json;

namespace StudyLife.Server.Services;

/// <summary>
/// Talks to the studylife-webhooks microservice on the logged-in user's behalf - both event
/// publishing (PublishEventAsync, called from wherever in this codebase something worth
/// notifying about happens - see WebhookEventTypes) and registration management (ListWebhooksAsync/
/// CreateWebhookAsync/DeleteWebhookAsync, proxied from WebhooksProxyController).
///
/// Unlike AiProxyClient's per-request signed proxy token, this uses one flat shared secret
/// (X-StudyLife-Shared-Secret) for every call, same as AiProxyClient's own /internal/* calls
/// (RegisterKeyAsync/RevokeKeyAsync) - there is no per-request user-impersonation concern here the
/// way there is for AiProxyController's /chat|/agent proxy (which forwards an interactive session
/// to an LLM agent); every call here already carries its own explicit user_id in the body/query,
/// and studylife-webhooks trusts the shared secret to mean "this really is StudyLife asking".
///
/// "Enabled" gate mirrors AiProxyClient/ApnsSender - an optional integration that silently stays
/// off (users simply can't register webhooks, and PublishEventAsync becomes a no-op) rather than
/// failing the app when StudyLifeWebhooks:* isn't configured.
/// </summary>
public sealed class WebhooksProxyClient
{
    private readonly ILogger<WebhooksProxyClient> _logger;
    private readonly HttpClient _http;
    private readonly string? _baseUrl;
    private readonly string? _sharedSecret;

    /// <summary>Upper bound on concurrently running PublishEventAsync calls per process. Callers
    /// fire-and-forget these (`_ = _webhooks.PublishEventAsync(...)` after every session/note/
    /// timer write), so without a bound a slow or hanging studylife-webhooks would accumulate
    /// unobserved tasks and sockets without limit (2026-09 audit L4). Beyond the bound a publish
    /// waits briefly for a slot and is then dropped with a warning - the events are best-effort
    /// notifications, never the source of truth.</summary>
    private const int MaxInFlight = 8;
    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _inFlight = new(MaxInFlight, MaxInFlight);

    public WebhooksProxyClient(IConfiguration configuration, ILogger<WebhooksProxyClient> logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _http = httpClient ?? new HttpClient { Timeout = RequestTimeout };
        _baseUrl = NullIfEmpty(configuration["StudyLifeWebhooks:BaseUrl"])?.TrimEnd('/');
        _sharedSecret = NullIfEmpty(configuration["StudyLifeWebhooks:SharedSecret"]);

        if (Enabled)
            _logger.LogInformation("studylife-webhooks integration active (BaseUrl {BaseUrl})", _baseUrl);
        else if (!string.IsNullOrEmpty(_baseUrl) || _sharedSecret is not null)
            _logger.LogWarning("studylife-webhooks configuration incomplete (BaseUrl and SharedSecret must both be set) - integration stays off");
    }

    public bool Enabled => !string.IsNullOrEmpty(_baseUrl) && _sharedSecret is not null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Never throws and silently no-ops when not Enabled - an install without
    /// StudyLifeWebhooks:* configured must behave exactly like one where the user has zero
    /// webhooks registered. Callers fire this without awaiting completion on a hot request path
    /// where that matters (see call sites) - a slow/unreachable studylife-webhooks must never add
    /// latency to the actual user-facing request that triggered the event.</summary>
    public async Task PublishEventAsync(int userId, string eventType, object payload, CancellationToken ct)
    {
        if (!Enabled) return;
        if (!await _inFlight.WaitAsync(SlotWait, ct))
        {
            StudyLifeMetrics.WebhookPublishes.Add(1, StudyLifeMetrics.Outcome("dropped"));
            _logger.LogWarning("studylife-webhooks event {EventType} dropped - {Max} publishes already in flight for {Wait}s", eventType, MaxInFlight, SlotWait.TotalSeconds);
            return;
        }
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/internal/events")
            {
                Content = JsonContent.Create(new
                {
                    user_id = userId,
                    event_type = eventType,
                    occurred_at = DateTime.UtcNow,
                    payload,
                }),
            };
            request.Headers.Add("X-StudyLife-Shared-Secret", _sharedSecret);
            var response = await _http.SendAsync(request, ct);
            outcome = response.IsSuccessStatusCode ? "ok" : "http_error";
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("studylife-webhooks /internal/events returned {Status} for event {EventType}", response.StatusCode, eventType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "studylife-webhooks /internal/events call failed for event {EventType}", eventType);
        }
        finally
        {
            _inFlight.Release();
            StudyLifeMetrics.WebhookPublishes.Add(1, StudyLifeMetrics.Outcome(outcome));
            StudyLifeMetrics.WebhookPublishDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    /// <summary>Caller (WebhooksProxyController) must check Enabled first and short-circuit with
    /// 503 - unlike PublishEventAsync, these three intentionally propagate failures/throw on a
    /// null BaseUrl, since they back an interactive user action (the Setup page) that should see
    /// a real error rather than a silently "successful" no-op.</summary>
    public Task<HttpResponseMessage> ListWebhooksAsync(int userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"/internal/webhooks?user_id={userId}", null, ct);

    public Task<HttpResponseMessage> CreateWebhookAsync(int userId, string targetUrl, IReadOnlyList<string> events, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/internal/webhooks", new { user_id = userId, target_url = targetUrl, events }, ct);

    public Task<HttpResponseMessage> DeleteWebhookAsync(int userId, string webhookId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"/internal/webhooks/{Uri.EscapeDataString(webhookId)}?user_id={userId}", null, ct);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-StudyLife-Shared-Secret", _sharedSecret);
        return await _http.SendAsync(request, ct);
    }
}
