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
/// 3. X-Api-Key header ONLY, matched against the four independent hash slots on AuthUserEntity
///    (ApiKeyHash/AiApiKeyHash/McpApiKeyHash/CaptureApiKeyHash). The former ?apiKey= query
///    string fallback was removed (audit finding A12a): a credential in the URL ends up in
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

        // Without a session token: per-user API key, matched against all eight independent
        // slots in one step (see AuthUserEntity.ApiKeyHash/AiApiKeyHash/McpApiKeyHash/
        // CaptureApiKeyHash/FocusGuardApiKeyHash/FocusTunesApiKeyHash/TrayApiKeyHash/
        // WebhooksApiKeyHash) - any one of them authenticates AND identifies the user. Header
        // only (audit finding A12a) - a ?apiKey= query string is deliberately NOT accepted.
        var providedKey = request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(providedKey))
        {
            var keyHash = AuthSessionService.HashToken(providedKey);
            var keyOwner = await _db.AuthUsers.AsNoTracking()
                .FirstOrDefaultAsync(u => u.ApiKeyHash == keyHash || u.AiApiKeyHash == keyHash
                    || u.McpApiKeyHash == keyHash || u.CaptureApiKeyHash == keyHash
                    || u.FocusGuardApiKeyHash == keyHash || u.FocusTunesApiKeyHash == keyHash
                    || u.TrayApiKeyHash == keyHash || u.WebhooksApiKeyHash == keyHash);
            if (keyOwner is null)
                return AuthenticateResult.Fail("Invalid API key.");

            var slot = keyOwner.ApiKeyHash == keyHash ? "ha"
                : keyOwner.AiApiKeyHash == keyHash ? "ai"
                : keyOwner.McpApiKeyHash == keyHash ? "mcp"
                : keyOwner.CaptureApiKeyHash == keyHash ? "capture"
                : keyOwner.FocusGuardApiKeyHash == keyHash ? "focusguard"
                : keyOwner.FocusTunesApiKeyHash == keyHash ? "focustunes"
                : keyOwner.TrayApiKeyHash == keyHash ? "tray"
                : "webhooks";

            Context.Items[CurrentUserAccessor.HttpContextItemKey] = keyOwner.Id;
            // Which slot matched (identity contract v1 §1) - only consumed by whoami today,
            // but cheap to set unconditionally here instead of re-deriving it per caller.
            Context.Items[AuthSessionService.ApiKeySlotItemKey] = slot;
            return AuthenticateResult.Success(BuildTicket(keyOwner.Id, AuthTypeApiKey, slot));
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
