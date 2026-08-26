using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace StudyLife.Server.Auth;

/// <summary>
/// Policy names + the one-line registration helper for the whole authorization setup (audit
/// finding A3). Two policies cover everything that used to be a path-string check or a
/// copy-pasted "session-required" property across controllers:
///
/// - ApiAccess: any authenticated credential (session, or any of the four API-key slots, or
///   the ICS calendar token) - the DEFAULT for every endpoint via AuthorizationOptions.
///   FallbackPolicy, which (unlike chaining .RequireAuthorization(...) onto MapControllers())
///   applies ONLY to endpoints that carry NO authorization metadata of their own at all, and
///   steps aside cleanly for any action that already has [Authorize(Policy = ...)] or
///   [AllowAnonymous] - chaining .RequireAuthorization() as an endpoint convention instead
///   would have COMBINED with those (every endpoint would need BOTH policies to pass), which
///   is exactly wrong for PublicUnlessInvalidSession below. This project has no Razor Pages/
///   static assets that FallbackPolicy could incorrectly catch (MapRazorPages() maps zero
///   endpoints here - no .cshtml files exist), except the SPA host file and the Apple
///   site-association endpoint, both explicitly marked .AllowAnonymous() in Program.cs.
/// - SessionOnly: ApiAccess PLUS the credential must be a real passkey session, not a bare API
///   key - the policy-based replacement for the SessionAuthUserId/SessionUser properties that
///   used to be copy-pasted across AuthController/SettingsController/SystemController/
///   AiProxyController. A SessionOnly failure - whether "no credential at all" or "a valid but
///   non-session credential" - always resolves to a 401 Challenge, never a 403 Forbid (see
///   AlwaysChallengeAuthorizationMiddlewareResultHandler), matching every one of those
///   properties' original "return Unauthorized()" behavior exactly.
///
///   BackupController deliberately does NOT use this policy: its IsOwnerAsync check
///   additionally requires being the FIRST registered user and, for that specific rejection,
///   intentionally returns 403 (not 401) so a merely-unprivileged-but-genuinely-logged-in
///   second user isn't shown as "session expired" by the client (see the comment there) - a
///   distinction the generic policy pipeline cannot express, so it stays a manual check.
///
/// PublicUnlessInvalidSession: the narrow policy for the two GET endpoints (progress/shared,
/// system/version) that were reachable without ANY credential under the old gate, but which
/// the former resolution middleware would still 401 if an X-Session-Token WAS present and
/// invalid (while tolerating a missing one). See PublicUnlessInvalidSessionRequirement.
/// </summary>
public static class StudyLifeAuthorizationPolicies
{
    public const string ApiAccess = "ApiAccess";
    public const string SessionOnly = "SessionOnly";
    public const string PublicUnlessInvalidSession = "PublicUnlessInvalidSession";

    public static IServiceCollection AddStudyLifeAuthentication(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = StudyLifeAuthenticationHandler.SchemeName;
                options.DefaultAuthenticateScheme = StudyLifeAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = StudyLifeAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = StudyLifeAuthenticationHandler.SchemeName;
            })
            .AddScheme<StudyLifeAuthenticationSchemeOptions, StudyLifeAuthenticationHandler>(
                StudyLifeAuthenticationHandler.SchemeName, _ => { });

        services.AddSingleton<IAuthorizationHandler, PublicUnlessInvalidSessionHandler>();
        // Audit finding A6 round 2: per-slot (ha/ai/mcp/capture) endpoint scoping for API keys -
        // see ApiKeyScopes for the map and ApiKeyScopeAuthorizationHandler for the enforcement
        // (session/calendar-token credentials are untouched by it, only api-key auth is scoped).
        services.AddSingleton<IAuthorizationHandler, ApiKeyScopeAuthorizationHandler>();
        // Every automatic authorization failure (ApiAccess/SessionOnly/PublicUnlessInvalidSession)
        // challenges (401) instead of the ASP.NET Core default of forbidding (403) an already-
        // authenticated-but-not-permitted principal - see the handler's own comment for why:
        // an API-key credential IS "authenticated" in ASP.NET Core's sense, but every one of the
        // properties this replaces returned 401, not 403, for exactly that case. The ONE
        // exception is a scope-only failure from ApiKeyScopeAuthorizationHandler (audit finding
        // A6 round 2), which the handler below deliberately still answers with 403 - see its
        // own comment.
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AlwaysChallengeAuthorizationMiddlewareResultHandler>();

        services.AddAuthorization(options =>
        {
            var apiAccessPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                // Audit finding A6 round 2: narrows a bare API key to its slot's allowed
                // endpoints (ApiKeyScopes) - a no-op for session/calendar-token credentials,
                // which ApiKeyScopeAuthorizationHandler always succeeds unconditionally.
                .AddRequirements(new ApiKeyScopeRequirement())
                .Build();
            options.AddPolicy(ApiAccess, apiAccessPolicy);
            // The default for every endpoint that states no requirement of its own - see the
            // class comment for why this MUST be FallbackPolicy and not a chained
            // .RequireAuthorization() endpoint convention.
            options.FallbackPolicy = apiAccessPolicy;

            options.AddPolicy(SessionOnly, p => p
                .RequireAuthenticatedUser()
                .RequireClaim(StudyLifeAuthenticationHandler.AuthTypeClaim, "session"));
            options.AddPolicy(PublicUnlessInvalidSession, p => p
                .AddRequirements(new PublicUnlessInvalidSessionRequirement()));
        });

        return services;
    }
}

/// <summary>Marker requirement - see PublicUnlessInvalidSessionHandler for the actual check
/// and StudyLifeAuthorizationPolicies for the rationale.</summary>
public sealed class PublicUnlessInvalidSessionRequirement : IAuthorizationRequirement;

/// <summary>
/// Succeeds for every request EXCEPT one that carried an X-Session-Token which failed
/// validation (StudyLifeAuthenticationHandler.InvalidSessionTokenItemKey) - i.e. "reachable
/// anonymously, but a bad session token is still rejected", exactly reproducing the former
/// resolution middleware's behavior for progress/shared and system/version (the only two
/// exempt GET endpoints it applied this to; /api/auth tolerated an invalid token instead,
/// which is why those actions are plain [AllowAnonymous] and never use this policy).
/// </summary>
public sealed class PublicUnlessInvalidSessionHandler(IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<PublicUnlessInvalidSessionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PublicUnlessInvalidSessionRequirement requirement)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null || !httpContext.Items.ContainsKey(StudyLifeAuthenticationHandler.InvalidSessionTokenItemKey))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Overrides ASP.NET Core's default "authenticated-but-not-permitted -> Forbid (403),
/// unauthenticated -> Challenge (401)" split: here EVERY automatic policy failure challenges,
/// regardless of authentication state - WITH ONE EXCEPTION (audit finding A6 round 2, see
/// below). See StudyLifeAuthorizationPolicies for why the general case is 401 (this app's
/// former per-controller session checks always answered 401, never 403, and BackupController -
/// the one place that genuinely needs 403 - never goes through the automatic policy pipeline
/// for that decision, it calls Forbid() explicitly from its own manual owner check instead).
///
/// Exception: a VALID API key that is merely out of scope for the endpoint it hit
/// (ApiKeyScopeAuthorizationHandler/ApiKeyScopes) answers 403, not 401 - a scope rejection is
/// a genuine "authenticated, but not permitted to do THIS" the caller can react to (fix its own
/// key usage), unlike every other case this handler exists to flatten (a bare API key failing
/// SessionOnly, where the framework's default would be a MISLEADING "authenticated" 403 for what
/// every existing client actually treats as "not logged in"). Detected precisely: only when
/// EVERY failed requirement in the policy is ApiKeyScopeRequirement - i.e. RequireAuthenticatedUser's
/// own requirement already succeeded (a real credential was presented and matched) - does this
/// count as a scope-only failure; a request that fails for some other reason too (e.g. no
/// credential at all) keeps the blanket 401 below.
/// </summary>
public sealed class AlwaysChallengeAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            var failedRequirements = authorizeResult.AuthorizationFailure?.FailedRequirements.ToList();
            if (failedRequirements is { Count: > 0 } && failedRequirements.All(r => r is ApiKeyScopeRequirement))
            {
                await context.ForbidAsync();
                return;
            }

            await context.ChallengeAsync();
            return;
        }
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
