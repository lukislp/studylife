using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace StudyLife.Server.Auth;

/// <summary>Marker requirement added to the ApiAccess policy (audit finding A6 round 2) -
/// see ApiKeyScopeAuthorizationHandler for the actual check and ApiKeyScopes for the map it
/// checks against.</summary>
public sealed class ApiKeyScopeRequirement : IAuthorizationRequirement;

/// <summary>
/// Enforces ApiKeyScopes for API-key-authenticated requests only; session and calendar-token
/// credentials always succeed unconditionally (see ApiKeyScopes' class comment for why neither
/// needs an entry in the map at all).
///
/// Deliberately does NOT call context.Fail() when denying: leaving the requirement merely
/// unsatisfied (pending) means AuthorizationFailure.FailedRequirements ends up containing
/// EXACTLY this requirement (RequireAuthenticatedUser's DenyAnonymousAuthorizationRequirement
/// already succeeded by the time this runs, since the credential itself authenticated fine) -
/// AlwaysChallengeAuthorizationMiddlewareResultHandler uses that precise signal to answer 403
/// (insufficient scope) instead of its usual blanket 401, without this handler having to know
/// anything about HTTP status codes itself.
/// </summary>
public sealed class ApiKeyScopeAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration,
    ILogger<ApiKeyScopeAuthorizationHandler> logger)
    : AuthorizationHandler<ApiKeyScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ApiKeyScopeRequirement requirement)
    {
        // Only an api-key-authenticated request is scoped at all - a real passkey session (the
        // browser client) and the ICS calendar token (exclusive to GET /api/sessions/ics by
        // construction, see StudyLifeAuthenticationHandler) both keep full/unconditional access,
        // exactly as before this change.
        var authType = context.User.FindFirst(StudyLifeAuthenticationHandler.AuthTypeClaim)?.Value;
        if (authType != "apikey")
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var slot = context.User.FindFirst("api_key_slot")?.Value;
        var httpContext = httpContextAccessor.HttpContext;
        var descriptor = httpContext?.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();

        // Dynamically registered clients (OAuthClientEntity) carry their own granted-scopes
        // snapshot as a claim instead of a fixed entry in ApiKeyScopes.BySlot - see
        // ClientApiKeyEntity.GrantedScopes and StudyLifeAuthenticationHandler.BuildClientTicket.
        // Whoami is unioned in unconditionally, same as every hardcoded slot below already does -
        // a developer never has to explicitly request identity-contract-v1-§1 access.
        IReadOnlySet<ApiKeyScopes.Endpoint>? allowedEndpoints = slot is not null && slot.StartsWith("client:", StringComparison.Ordinal)
            ? new HashSet<ApiKeyScopes.Endpoint>(ApiKeyScopes.Parse(context.User.FindFirst(StudyLifeAuthenticationHandler.GrantedScopesClaim)?.Value)) { ApiKeyScopes.Whoami }
            : slot is not null && ApiKeyScopes.BySlot.TryGetValue(slot, out var bySlot) ? bySlot : null;

        var allowed = descriptor is not null && allowedEndpoints is not null
            && allowedEndpoints.Contains(new ApiKeyScopes.Endpoint(descriptor.ControllerName, descriptor.ActionName));

        if (allowed)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Rollout escape hatch (audit finding A6 round 2 phase B step 2): default true. While
        // false, every would-be-denied request is logged but still let through - lets an
        // operator confirm the matrix against real traffic before it starts actually rejecting
        // anything. See appsettings.json / docs/ARCHITECTURE.md "Security" for the setting.
        var enforce = configuration.GetValue("Security:EnforceKeyScopes", true);
        var endpointLabel = descriptor is not null
            ? $"{descriptor.ControllerName}.{descriptor.ActionName}"
            : httpContext?.Request.Path.Value ?? "(unknown endpoint)";

        if (enforce)
        {
            logger.LogWarning(
                "API key slot {Slot} denied access to {Endpoint} - not in ApiKeyScopes (Security:EnforceKeyScopes=true)",
                slot, endpointLabel);
            // Deliberately no context.Fail()/context.Succeed() - see the class comment.
        }
        else
        {
            logger.LogWarning(
                "API key slot {Slot} would be DENIED access to {Endpoint} - not in ApiKeyScopes, " +
                "but Security:EnforceKeyScopes=false (log-only mode) lets it through",
                slot, endpointLabel);
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
