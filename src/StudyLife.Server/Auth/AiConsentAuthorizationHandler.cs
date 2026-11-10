// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using StudyLife.Server.Services;

namespace StudyLife.Server.Auth;

/// <summary>Marker requirement added to the ApiAccess policy - see AiConsentAuthorizationHandler
/// for the check and AiConsentService for the rule it asks.</summary>
public sealed class AiConsentRequirement : IAuthorizationRequirement;

/// <summary>
/// Keeps studylife-ai from reading a user's data with that user's AI key while no AI consent
/// covers the current provider. studylife-ai's ingestion (<c>sync_user</c> and the periodic
/// <c>sync_all</c>) pulls notes, courses, sessions and goals through the REST API with the key
/// registered for the user, then embeds them at the AI provider - so gating only the proxy
/// endpoints and key generation would leave an existing user without a consent record (the
/// migration case), or anyone after a provider change, being indexed until they declined.
///
/// Decided here, where the credential's kind is already decided, and not in individual
/// controllers: it only applies to API-key requests of the "ai" slot (every other key slot,
/// sessions and the calendar token succeed unconditionally), and the rule itself is
/// <see cref="IAiConsentService.EnsureGrantedAsync"/> - the same one the proxy and key
/// generation ask. Like ApiKeyScopeAuthorizationHandler it never calls Fail(): the requirement is
/// left unsatisfied and AlwaysChallengeAuthorizationMiddlewareResultHandler turns exactly that
/// signal into the 403 <c>ai-consent-required</c> problem, which studylife-ai reads as "pause
/// this user quietly" (no purge, resumes by itself once the user agrees).
/// </summary>
public sealed class AiConsentAuthorizationHandler(IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<AiConsentRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AiConsentRequirement requirement)
    {
        var authType = context.User.FindFirst(StudyLifeAuthenticationHandler.AuthTypeClaim)?.Value;
        var slot = context.User.FindFirst("api_key_slot")?.Value;
        if (authType != "apikey" || slot != "ai")
        {
            context.Succeed(requirement);
            return;
        }

        var httpContext = httpContextAccessor.HttpContext;
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (httpContext is null || !int.TryParse(userIdClaim, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
            return; // fail closed: an AI-key request whose owner cannot be determined is not let through

        var consent = (IAiConsentService)httpContext.RequestServices.GetService(typeof(IAiConsentService))!;
        var gate = await consent.EnsureGrantedAsync(userId, httpContext.RequestAborted);
        if (gate.Outcome == ServiceOutcome.Success)
            context.Succeed(requirement);
        // else: deliberately unsatisfied - see the class comment.
    }
}
