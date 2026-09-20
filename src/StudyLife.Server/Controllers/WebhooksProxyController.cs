using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Proxies webhook-registration requests from whichever caller to the studylife-webhooks
/// microservice, on behalf of whichever user actually authenticated the request.
///
/// Deliberately NOT session-only, unlike AiProxyController: the whole point of studylife-webhooks
/// is that OTHER programs/add-ons (not the StudyLife browser client itself) register their own
/// subscriptions, so this needs to be reachable with just an API key - specifically the
/// WebhooksApiKey slot (see ApiKeyScopes.Webhooks), generated once from the Setup page (like Ha's
/// key) and handed to whichever external tool the user wants to let manage webhooks. No explicit
/// [Authorize] here at all - falls through to the default ApiAccess fallback policy (session OR
/// any scoped API key + ApiKeyScopeAuthorizationHandler's per-slot enforcement), exactly like
/// TimerStateController/NotesController/etc. Per-user isolation is automatic, not something this
/// controller has to enforce itself: _currentUser.AuthUserId always resolves to whichever user
/// actually owns the credential that authenticated the request (session or API key alike), and
/// every call below is scoped to that id - there is no code path where one user's key can reach
/// another user's webhook registrations.
///
/// Pure reverse proxy for the response side (status/content-type/body pass through unparsed) -
/// this controller never needs to know studylife-webhooks' exact registration-record shape.
/// </summary>
[ApiController]
[Route("api/webhooks")]
public class WebhooksProxyController : ControllerBase
{
    private readonly WebhooksProxyClient _client;
    private readonly ICurrentUserAccessor _currentUser;

    public WebhooksProxyController(WebhooksProxyClient client, ICurrentUserAccessor currentUser)
    {
        _client = client;
        _currentUser = currentUser;
    }

    [HttpGet]
    public Task List(CancellationToken ct) =>
        ProxyAsync(() => _client.ListWebhooksAsync(_currentUser.AuthUserId, ct));

    [HttpPost]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(RateLimitPolicies.Expensive)]
    public async Task<IActionResult> Create([FromBody] CreateWebhookRequestDto dto, CancellationToken ct)
    {
        // The target is a URL studylife-webhooks later POSTs to unattended from inside the
        // cluster network - same blind-proxy hazard as PushService.SubscribeAsync's endpoint, see
        // OutboundUrlPolicy. Checked BEFORE the Enabled gate in ProxyAsync so an unconfigured
        // install answers a bad URL with 400 rather than a misleading 503.
        if (!OutboundUrlPolicy.IsAcceptableWebhookTarget(dto.TargetUrl))
            return BadRequest(new { error = "TargetUrl must be a public http(s) URL." });

        await ProxyAsync(() => _client.CreateWebhookAsync(_currentUser.AuthUserId, dto.TargetUrl, dto.Events, ct));
        return new EmptyResult();
    }

    [HttpDelete("{id}")]
    public Task Delete(string id, CancellationToken ct) =>
        ProxyAsync(() => _client.DeleteWebhookAsync(_currentUser.AuthUserId, id, ct));

    private async Task ProxyAsync(Func<Task<HttpResponseMessage>> call)
    {
        if (!_client.Enabled)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var upstream = await call();
        Response.StatusCode = (int)upstream.StatusCode;
        if (upstream.Content.Headers.ContentType is { } contentType)
            Response.ContentType = contentType.ToString();
        await upstream.Content.CopyToAsync(Response.Body);
    }
}
