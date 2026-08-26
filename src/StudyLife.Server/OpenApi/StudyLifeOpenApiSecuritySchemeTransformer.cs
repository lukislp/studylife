using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace StudyLife.Server.OpenApi;

/// <summary>
/// Audit finding D2: documents the two real credential kinds a caller can present
/// (StudyLifeAuthenticationHandler resolves session token / API key / ICS calendar token, in
/// that priority - see StudyLifeAuthorizationPolicies for the full policy design), so the
/// generated document is actually usable by a client-generator instead of showing every
/// endpoint as unauthenticated. Deliberately minimal: two apiKey-type header schemes, not a
/// full description of scope/slot semantics (ApiKeyScopes) or the session-vs-key distinction
/// enforced by the SessionOnly policy - that nuance doesn't fit OpenAPI's security-scheme model
/// well and isn't needed for a consumer to construct a working request. The ICS calendar token
/// (query-string based, GET /api/sessions/ics only) is deliberately NOT modeled here either -
/// it's a single, self-describing link handed out by the app itself, not a credential a
/// generated API client needs to authenticate with.
/// </summary>
public sealed class StudyLifeOpenApiSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["SessionToken"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Session-Token",
            Description = "Passkey session token issued by POST /api/auth/passkey/complete or the device-link/handoff flows - what the browser/native app clients use.",
        };
        document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Api-Key",
            Description = "Long-lived per-user API key (generated on the Setup page, one slot each for Home Assistant/AI/MCP/Capture - see ApiKeyScopes). Scoped per slot to a subset of endpoints; an out-of-scope request answers 403, not 401.",
        };

        return Task.CompletedTask;
    }
}
