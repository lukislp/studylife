using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Proxies chat/agent requests from the Blazor client to the studylife-ai microservice on
/// behalf of the logged-in user - see AiProxyClient for why this hop exists (the user's real
/// AiApiKey can't be forwarded, only a short-lived signed identity token can). Session-only
/// auth ([Authorize(Policy = SessionOnly)], same requirement as SettingsController's ai-api-key
/// group): an API key alone must not be enough to drive the AI agent as some other user's
/// session.
///
/// Pure reverse proxy - request/response bodies pass through unparsed, including /chat's SSE
/// stream, so this controller never needs to know studylife-ai's request/response shapes.
/// </summary>
[ApiController]
[Route("api/ai")]
[Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(RateLimitPolicies.Expensive)]
public class AiProxyController : ControllerBase
{
    private readonly AiProxyClient _client;
    private readonly ICurrentUserAccessor _currentUser;

    public AiProxyController(AiProxyClient client, ICurrentUserAccessor currentUser)
    {
        _client = client;
        _currentUser = currentUser;
    }

    [HttpPost("chat")]
    public Task Chat(CancellationToken ct) => ProxyAsync("/chat", ct);

    [HttpPost("agent")]
    public Task Agent(CancellationToken ct) => ProxyAsync("/agent", ct);

    [HttpPost("agent/confirm")]
    public Task AgentConfirm(CancellationToken ct) => ProxyAsync("/agent/confirm", ct);

    private async Task ProxyAsync(string path, CancellationToken ct)
    {
        // No manual session check here anymore - [Authorize(Policy = SessionOnly)] on the
        // class already rejected anything that isn't a real session before this method runs.
        var userId = _currentUser.AuthUserId;
        if (!_client.Enabled)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var upstream = await _client.ProxyAsync(
            path, userId, Request.Body, Request.ContentType ?? "application/json", ct);
        Response.StatusCode = (int)upstream.StatusCode;
        if (upstream.Content.Headers.ContentType is { } contentType)
            Response.ContentType = contentType.ToString();
        await upstream.Content.CopyToAsync(Response.Body, ct);
    }
}
