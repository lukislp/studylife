// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;
using StudyLife.Server.Discovery;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// GET /api/instance - the stable installation id (docs/MDNS.md). Deliberately anonymous: it carries
/// nothing secret (a random id plus the version, which /api/system/version already exposes), and
/// Home Assistant needs it before it has any credential to recognise "the same StudyAtrium" under a
/// different URL. [AllowAnonymous] skips the ApiAccess policy, so - like the other anonymous
/// endpoints - this action needs no ApiKeyScopes entry; the normal rate limiter still applies.
/// It also names the pinned passkey domain (Fido2:ServerDomain / Fido2:Origins), the same values every
/// sign-in ceremony publishes, so the native app can tell a user on an internal address where passkeys work.
/// Likewise the operator's own privacy-policy and imprint addresses (Legal:*), which the client links
/// from the sign-in pages and About instead of the publisher's.
/// </summary>
[ApiController]
[Route("api/instance")]
public class InstanceController(IInstanceIdProvider instanceId, IOptionsMonitor<Fido2Options> fido2,
    IOptionsMonitor<LegalOptions> legal) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<InstanceInfoDto>> Get(CancellationToken cancellationToken)
    {
        var id = await instanceId.GetAsync(cancellationToken);
        // The value never changes, so a short private cache is safe.
        Response.Headers.CacheControl = "private, max-age=60";
        var passkeys = fido2.CurrentValue;
        var legalLinks = legal.CurrentValue;
        return new InstanceInfoDto
        {
            Id = id,
            Version = MdnsServiceDescription.CurrentVersion(),
            PasskeyRpId = string.IsNullOrWhiteSpace(passkeys.ServerDomain) ? null : passkeys.ServerDomain.Trim(),
            PasskeyOrigin = passkeys.Origins?.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o))?.Trim(),
            Legal = new LegalLinksDto
            {
                PrivacyUrl = LegalOptions.Normalize(legalLinks.PrivacyUrl),
                ImprintUrl = LegalOptions.Normalize(legalLinks.ImprintUrl),
            },
        };
    }
}
