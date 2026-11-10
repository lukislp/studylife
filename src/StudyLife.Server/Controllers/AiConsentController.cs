// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// The user's consent to send their notes and study data to an AI provider (Apple App Review
/// guideline 5.1.2(i)). Pure HTTP mapping: the rule - when consent is required, what a consent
/// covers, what withdrawing does - is <see cref="AiConsentService"/>'s. Session-only, like the
/// ai-api-key group and the AI proxy: an API key must not be able to grant a consent the user
/// never gave. The POSTs are mutating, so the demo write-block rejects them on a demo instance
/// like every other write; the status read reports the AI as unavailable there.
/// </summary>
[ApiController]
[Route("api/ai/consent")]
[Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
public class AiConsentController : ControllerBase
{
    private readonly IAiConsentService _consent;

    public AiConsentController(IAiConsentService consent) => _consent = consent;

    private int SessionUserId => HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]

    /// <summary>What the client needs to decide whether and what to ask: whether consent is
    /// required, whether the stored one covers the current provider, who the provider is.</summary>
    [HttpGet]
    public async Task<ActionResult<AiConsentStatusDto>> GetStatus(CancellationToken ct)
    {
        // Same reasoning as GET api/system/capabilities: a cached answer would show or hide the
        // dialog wrongly.
        Response.Headers.CacheControl = "no-store";
        return await _consent.GetStatusAsync(SessionUserId, ct);
    }

    /// <summary>Records the consent for the provider description the user was shown. 400 for a
    /// malformed fingerprint, 409 (with the current status) when the description changed since.</summary>
    [HttpPost("grant")]
    public async Task<ActionResult<AiConsentStatusDto>> Grant([FromBody] AiConsentGrantRequestDto request, CancellationToken ct) =>
        (await _consent.GrantAsync(SessionUserId, request.Fingerprint, ct)).ToActionResult(this);

    /// <summary>Withdraws the consent and revokes the AI key, which purges the user's data in
    /// studylife-ai. Also the answer to "not now" for an existing AI user asked at app start.</summary>
    [HttpPost("withdraw")]
    public async Task<IActionResult> Withdraw(CancellationToken ct) =>
        (await _consent.WithdrawAsync(SessionUserId, ct)).ToNoContentResult(this);
}
