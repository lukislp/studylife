// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// The instance owner's review of OAuth client registrations made by other users (2026-11-16
/// audit M5), used by the Setup page's add-on approvals card. Session-only: deciding who may
/// receive users' keys is for the owner in person, never for an API key. The owner check itself
/// lives in <see cref="OAuthClientReviewService"/> (403 with problem code <c>owner-required</c>).
/// </summary>
[ApiController]
[Route("api/oauth-client-reviews")]
[Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
public class OAuthClientReviewController(IOAuthClientReviewService reviews) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<OAuthClientReviewDto>>> List() =>
        (await reviews.ListAsync()).ToActionResult(this);

    [HttpPost("{clientId}/approve")]
    public async Task<ActionResult<OAuthClientReviewDto>> Approve(string clientId) =>
        (await reviews.ApproveAsync(clientId)).ToActionResult(this);

    [HttpPost("{clientId}/reject")]
    public async Task<ActionResult<OAuthClientReviewDto>> Reject(string clientId) =>
        (await reviews.RejectAsync(clientId)).ToActionResult(this);
}
