using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Auth;

public class StudyLifeAuthenticationSchemeOptions : AuthenticationSchemeOptions;

/// <summary>
/// Single AuthenticationHandler replacing the two hand-rolled inline middleware lambdas that
/// used to live in Program.cs (audit finding A3). Resolves exactly the same three credential
/// kinds, in exactly the same priority order, as the former "API gate" middleware:
///
/// 1. ICS calendar feed (GET /api/sessions/ics exactly, ?calendarToken=) - EXCLUSIVE: this
///    route never falls through to session/API-key resolution, matching the former gate's ICS
///    branch which returned/continued without ever looking at X-Session-Token or X-Api-Key.
/// 2. X-Session-Token header - if present, it EXCLUSIVELY applies (an invalid/expired token
///    fails authentication even if a valid API key is ALSO present), extending the session on
///    a sliding basis via AuthSessionService.ValidateAndRefreshAsync exactly as before.
/// 3. X-Api-Key header ONLY, matched against the seven independent hash column slots on
///    AuthUserEntity (ApiKeyHash/AiApiKeyHash/McpApiKeyHash/CaptureApiKeyHash/
///    FocusGuardApiKeyHash/FocusTunesApiKeyHash/TrayApiKeyHash), falling back to the
///    WebhookApiKeyEntity table (Webhooks is the one slot that supports multiple named keys
///    per user instead of a single column - see that entity's own doc comment). The former
///    ?apiKey= query string fallback was removed (audit finding A12a): a credential in the URL ends up in
///    server access logs, browser history, and Referer headers of any outbound request the
///    page makes, none of which apply to a header. No known consumer ever used the query
///    form (see the removal commit for the cross-repo grep); the ICS feed's ?calendarToken=
///    above and the progress-share token in the URL PATH (ProgressController) are unrelated,
///    intentionally-URL-borne mechanisms and are untouched by this.
///
/// HttpContext.Items keeps being populated exactly like before (CurrentUserAccessor.
/// HttpContextItemKey, AuthSessionService.SessionItemKey, AuthSessionService.ApiKeySlotItemKey) -
/// that contract is load-bearing (EF global query filters, whoami, the session-required checks
/// across several controllers) and is NOT replaced by the ClaimsPrincipal, only complemented by
/// it (the principal is what makes [Authorize]/policies work; Items remains authoritative for
/// every existing consumer).
///
/// Which /api paths need no credential at all (open registration/login, the public progress
/// link, /api/system/version) is now expressed via [AllowAnonymous] / the PublicUnlessInvalidSession
/// policy on the individual controller actions, not via path-string checks here - this handler
/// only ever answers "who, if anyone, does this request's credential belong to", regardless of
/// whether the endpoint actually requires one.
/// </summary>
public class StudyLifeAuthenticationHandler : AuthenticationHandler<StudyLifeAuthenticationSchemeOptions>
{
    public const string SchemeName = "StudyLife";

    /// <summary>HttpContext.Items marker set ONLY when an X-Session-Token was present but
    /// failed validation - consumed exclusively by the PublicUnlessInvalidSessionRequirement
    /// (progress/shared, system/version), which must reject an invalid session token even
    /// though the endpoint is otherwise reachable without any credential at all. Mirrors the
    /// former resolution middleware's behavior precisely: it re-validated X-Session-Token even
    /// on exempt paths and 401'd on an invalid one there too (except on /api/auth, which
    /// tolerates it - those actions are plain AllowAnonymous and never consult this marker).</summary>
    public const string InvalidSessionTokenItemKey = "StudyLife.InvalidSessionTokenAttempted";

    /// <summary>Claim carrying which credential kind authenticated the request ("session",
    /// "apikey", or "calendarToken") - the SessionOnly policy requires "session" specifically,
    /// the same distinction AuthController.SessionAuthUserId/SettingsController.SessionUser
    /// used to make by checking AuthSessionService.SessionItemKey presence.</summary>
    public const string AuthTypeClaim = "auth_type";

    /// <summary>Claim carrying a ClientApiKeyEntity's GrantedScopes snapshot (see that entity's
    /// doc comment) - only present when api_key_slot starts with "client:". Read exclusively by
    /// ApiKeyScopeAuthorizationHandler, which parses it via ApiKeyScopes.Parse instead of looking
    /// up ApiKeyScopes.BySlot for this one dynamic case.</summary>
    public const string GrantedScopesClaim = "granted_scopes";

    private const string AuthTypeSession = "session";
    private const string AuthTypeApiKey = "apikey";
    private const string AuthTypeCalendarToken = "calendarToken";

    private readonly StudyLifeDb _db;

    public StudyLifeAuthenticationHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<StudyLifeAuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        StudyLifeDb db)
        : base(options, logger, encoder)
    {
        _db = db;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var request = Context.Request;

        // ICS calendar feed: own permanent per-user token (AuthUserEntity.CalendarToken),
        // because subscribing calendar apps can neither set headers nor go through a login.
        // Deliberately limited to GET + exact path (like the former gate), so no other
        // /api/sessions/... endpoint is accidentally reachable via a stray ?calendarToken=
        // query parameter, and deliberately EXCLUSIVE (no fallback to session/API key below) -
        // identical scoping to the removed gate branch.
        if (HttpMethods.IsGet(request.Method)
            && request.Path.StartsWithSegments("/api/sessions/ics", out var icsRemainder)
            && string.IsNullOrEmpty(icsRemainder.Value))
        {
            var calendarToken = request.Query["calendarToken"].FirstOrDefault();
            if (string.IsNullOrEmpty(calendarToken))
                return AuthenticateResult.Fail("Missing calendar token.");

            var calendarOwner = await _db.AuthUsers.AsNoTracking()
                .FirstOrDefaultAsync(u => u.CalendarToken == calendarToken);
            if (calendarOwner is null)
                return AuthenticateResult.Fail("Invalid calendar token.");

            Context.Items[CurrentUserAccessor.HttpContextItemKey] = calendarOwner.Id;
            return AuthenticateResult.Success(BuildTicket(calendarOwner.Id, AuthTypeCalendarToken));
        }

        // Session token has highest priority - if one is present, it EXCLUSIVELY applies, and
        // an invalid/expired token fails authentication even alongside a valid API key.
        var sessionToken = request.Headers[AuthSessionService.TokenHeaderName].FirstOrDefault();
        if (!string.IsNullOrEmpty(sessionToken))
        {
            var session = await AuthSessionService.ValidateAndRefreshAsync(_db, sessionToken, DateTime.UtcNow);
            if (session is null)
            {
                // Consumed only by PublicUnlessInvalidSessionRequirement - see its comment.
                Context.Items[InvalidSessionTokenItemKey] = true;
                return AuthenticateResult.Fail("Invalid or expired session token.");
            }

            Context.Items[CurrentUserAccessor.HttpContextItemKey] = session.AuthUserId;
            Context.Items[AuthSessionService.SessionItemKey] = session.Id;
            return AuthenticateResult.Success(BuildTicket(session.AuthUserId, AuthTypeSession));
        }

        // Without a session token: per-user API key, matched against seven independent column
        // slots in one step (see AuthUserEntity.ApiKeyHash/AiApiKeyHash/McpApiKeyHash/
        // CaptureApiKeyHash/FocusGuardApiKeyHash/FocusTunesApiKeyHash/TrayApiKeyHash) - any one
        // of them authenticates AND identifies the user. Header only (audit finding A12a) - a
        // ?apiKey= query string is deliberately NOT accepted.
        var providedKey = request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(providedKey))
        {
            var keyHash = AuthSessionService.HashToken(providedKey);
            var keyOwner = await _db.AuthUsers.AsNoTracking()
                .FirstOrDefaultAsync(u => u.ApiKeyHash == keyHash || u.AiApiKeyHash == keyHash
                    || u.McpApiKeyHash == keyHash || u.CaptureApiKeyHash == keyHash
                    || u.FocusGuardApiKeyHash == keyHash || u.FocusTunesApiKeyHash == keyHash
                    || u.TrayApiKeyHash == keyHash);
            if (keyOwner is not null)
            {
                var slot = keyOwner.ApiKeyHash == keyHash ? "ha"
                    : keyOwner.AiApiKeyHash == keyHash ? "ai"
                    : keyOwner.McpApiKeyHash == keyHash ? "mcp"
                    : keyOwner.CaptureApiKeyHash == keyHash ? "capture"
                    : keyOwner.FocusGuardApiKeyHash == keyHash ? "focusguard"
                    : keyOwner.FocusTunesApiKeyHash == keyHash ? "focustunes"
                    : "tray";

                Context.Items[CurrentUserAccessor.HttpContextItemKey] = keyOwner.Id;
                // Which slot matched (identity contract v1 §1) - only consumed by whoami today,
                // but cheap to set unconditionally here instead of re-deriving it per caller.
                Context.Items[AuthSessionService.ApiKeySlotItemKey] = slot;
                return AuthenticateResult.Success(BuildTicket(keyOwner.Id, AuthTypeApiKey, slot));
            }

            // Unlike the seven slots above (one key per user, a column on AuthUserEntity),
            // Webhooks supports multiple NAMED keys per user (WebhookApiKeyEntity, see
            // SettingsController's webhooks-api-keys trio) - looked up by hash in its own table
            // instead of an equality check against a fixed column. No query filter on that
            // table (see StudyLifeDb's own comment on it) - this lookup is exactly the
            // before-any-user-is-known case that exempts it.
            var webhookKey = await _db.WebhookApiKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.KeyHash == keyHash);
            if (webhookKey is not null)
            {
                Context.Items[CurrentUserAccessor.HttpContextItemKey] = webhookKey.AuthUserId;
                Context.Items[AuthSessionService.ApiKeySlotItemKey] = "webhooks";
                return AuthenticateResult.Success(BuildTicket(webhookKey.AuthUserId, AuthTypeApiKey, "webhooks"));
            }

            // Dynamically registered clients (OAuthClientEntity, see its own doc comment and
            // ApiKeyScopes.PubliclyGrantable) - a THIRD kind of lookup, again by hash in its own
            // table, no query filter, same before-any-user-is-known reasoning. The slot is the
            // ClientId itself (prefixed, not one of the 8 fixed strings above) so Whoami usefully
            // shows WHICH app authenticated, not just "some dynamic client".
            var clientKey = await _db.ClientApiKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.KeyHash == keyHash);
            if (clientKey is not null)
            {
                var clientSlot = $"client:{clientKey.ClientId}";
                Context.Items[CurrentUserAccessor.HttpContextItemKey] = clientKey.AuthUserId;
                Context.Items[AuthSessionService.ApiKeySlotItemKey] = clientSlot;
                return AuthenticateResult.Success(BuildClientTicket(clientKey.AuthUserId, clientSlot, clientKey.GrantedScopes));
            }

            return AuthenticateResult.Fail("Invalid API key.");
        }

        // No credential attempted at all - NoResult (not Fail): endpoints that require ApiAccess
        // will Challenge (401) via the policy pipeline; AllowAnonymous endpoints simply run
        // unauthenticated, exactly like the former exemption branches did.
        return AuthenticateResult.NoResult();
    }

    private AuthenticationTicket BuildTicket(int userId, string authType, string? apiKeySlot = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
            new(AuthTypeClaim, authType),
        };
        if (apiKeySlot is not null) claims.Add(new Claim("api_key_slot", apiKeySlot));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationTicket(principal, Scheme.Name);
    }

    /// <summary>Same shape as BuildTicket, plus the GrantedScopesClaim a dynamic client's scope
    /// check needs - kept separate rather than adding an optional parameter to BuildTicket since
    /// only this one credential kind ever carries it.</summary>
    private AuthenticationTicket BuildClientTicket(int userId, string apiKeySlot, string grantedScopes)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
            new(AuthTypeClaim, AuthTypeApiKey),
            new("api_key_slot", apiKeySlot),
            new(GrantedScopesClaim, grantedScopes),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationTicket(principal, Scheme.Name);
    }

    /// <summary>
    /// Plain 401, no body, no WWW-Authenticate header, no redirect - byte-identical to the
    /// former gate's "context.Response.StatusCode = 401; return;" for every rejection case.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Plain 403, no body - only ever reached via an EXPLICIT Forbid() call in controller code
    /// (BackupController.IsOwnerAsync callers); automatic policy failures never reach this
    /// (see AlwaysChallengeAuthorizationMiddlewareResultHandler), so today's 401s stay 401s.
    /// </summary>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
