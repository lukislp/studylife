using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Proxies webhook-registration requests from the Blazor client to the studylife-webhooks
/// microservice on behalf of the logged-in user - same reasoning as AiProxyController (the user's
/// browser never talks to studylife-webhooks directly, and never needs its own API key/consent
/// flow for this the way Guard/Tune do, since every call already carries session auth here).
/// Session-only auth: an API key alone must not be enough to manage someone's webhook
/// registrations.
///
/// Pure reverse proxy for the response side (status/content-type/body pass through unparsed) -
/// this controller never needs to know studylife-webhooks' exact registration-record shape.
/// </summary>
[ApiController]
[Route("api/webhooks")]
[Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
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
    public Task Create([FromBody] CreateWebhookRequestDto dto, CancellationToken ct) =>
        ProxyAsync(() => _client.CreateWebhookAsync(_currentUser.AuthUserId, dto.TargetUrl, dto.Events, ct));

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
