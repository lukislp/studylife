using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Discovery;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// GET /api/instance - the stable installation id (docs/MDNS.md). Deliberately anonymous: it carries
/// nothing secret (a random id plus the version, which /api/system/version already exposes), and
/// Home Assistant needs it before it has any credential to recognise "the same StudyLife" under a
/// different URL. [AllowAnonymous] skips the ApiAccess policy, so - like the other anonymous
/// endpoints - this action needs no ApiKeyScopes entry; the normal rate limiter still applies.
/// </summary>
[ApiController]
[Route("api/instance")]
public class InstanceController(IInstanceIdProvider instanceId) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<InstanceInfoDto>> Get(CancellationToken cancellationToken)
    {
        var id = await instanceId.GetAsync(cancellationToken);
        // The value never changes, so a short private cache is safe.
        Response.Headers.CacheControl = "private, max-age=60";
        return new InstanceInfoDto { Id = id, Version = MdnsServiceDescription.CurrentVersion() };
    }
}
