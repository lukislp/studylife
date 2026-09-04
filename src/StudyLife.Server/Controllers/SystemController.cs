using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

/// <summary>
/// Per-user token for the subscribable ICS calendar feed (AuthUserEntity.CalendarToken).
/// Replaces the former global CalendarTokenProvider (a single, process-wide token for
/// all users) - which, in multi-user operation, would have shown every caller the same
/// calendar (that of the first registered user), regardless of who is actually fetching
/// the token. Both endpoints require a REAL passkey session (SessionItemKey), not just
/// any gate authentication - same rationale as for the ha-api-key endpoints in
/// SettingsController: a leaked API key must not be able to issue or regenerate its own
/// calendar token.
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly bool _rawBackupSupported;
    private readonly bool _demoMode;

    public SystemController(StudyLifeDb db,
        IConfiguration config,
        Services.DatabaseBackupService? backupService = null,
        Services.DatabaseRestoreService? restoreService = null)
    {
        _db = db;
        // Same derivation as BackupController.IsRawBackupAvailable: both services are
        // only registered in SQLite mode (Program.cs) - on Postgres the external backup
        // path (e.g. R2) takes over, and the raw endpoints report 501.
        // Demo instances additionally report false regardless of provider: the demo
        // middleware 403s the whole /api/backup path (raw DB downloads would leak
        // SystemSecrets), and rawBackupSupported=false is exactly the existing signal the
        // setup page already uses to hide the backup/restore cards - no client change needed.
        // DemoModeGuard.IsEnabled (not a bare DEMO_MODE check) so this agrees with Program.cs
        // about whether the write-block middleware is actually registered.
        _rawBackupSupported = backupService is not null && restoreService is not null
            && !DemoModeGuard.IsEnabled(config);
        _demoMode = DemoModeGuard.IsEnabled(config);
    }

    /// <summary>
    /// Server capabilities for the client UI: "which controls make sense here".
    /// Sits behind the session gate like almost everything under /api - the querying
    /// setup cards only exist while logged in anyway. SetupBackupCard/SetupRestoreCard
    /// hide their raw-backup parts when rawBackupSupported is false (Postgres operation,
    /// backup runs externally there).
    /// </summary>
    [HttpGet("capabilities")]
    public ActionResult<SystemCapabilitiesResponseDto> GetCapabilities()
    {
        // no-store: this response must never end up in an HTTP cache - a client with
        // stale capability info would otherwise show/hide UI incorrectly (see the
        // /api fallback comment in Program.cs about NSURLCache poisoning).
        Response.Headers.CacheControl = "no-store";
        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        return Ok(BuildCapabilities(configuration, _rawBackupSupported));
    }

    // internal instead of private: reused by SetupController (bundle endpoint) so both call
    // sites compute the exact same DTO for the same rawBackupSupported input.
    internal static SystemCapabilitiesResponseDto BuildCapabilities(IConfiguration configuration, bool rawBackupSupported)
    {
        var sampleRatio = configuration.GetValue<double?>("Telemetry:ClientSampleRatio") ?? 0.10;
        return new SystemCapabilitiesResponseDto
        {
            RawBackupSupported = rawBackupSupported,
            TelemetryClientSampleRatio = Math.Clamp(sampleRatio, 0, 1),
        };
    }

    /// <summary>
    /// Returns the setup page's permanent calendar token - lazily created on first fetch,
    /// so a user who never uses the feature doesn't have a token sitting in the DB either.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("calendar-token")]
    public async Task<ActionResult<CalendarTokenResponseDto>> GetCalendarToken()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        if (user.CalendarToken is null)
        {
            // On a demo instance this branch is normally unreachable (DemoSeeder pre-seeds the
            // token), but this lazy create is a GET that PERSISTS - the write-block middleware
            // in Program.cs only covers non-GET methods, so without this check a seeder change
            // (e.g. dropping the pre-seeded token, or a second demo user) would silently turn
            // this endpoint into the demo's only visitor-reachable DB write.
            if (_demoMode)
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { error = "demo calendar token not seeded" });
            user.CalendarToken = AuthSessionService.GenerateToken();
            user.CalendarTokenCreatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return new CalendarTokenResponseDto { CalendarToken = user.CalendarToken };
    }

    /// <summary>
    /// Manual "regenerate now" for the permanent calendar token (e.g. on suspicion of a
    /// leak). Immediately breaks every existing calendar subscription of this user; the user
    /// must resubscribe to the ICS URL afterward.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("regenerate-calendar-token")]
    public async Task<ActionResult<RegenerateCalendarTokenResponseDto>> RegenerateCalendarToken()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        user.CalendarToken = AuthSessionService.GenerateToken();
        user.CalendarTokenCreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new RegenerateCalendarTokenResponseDto { CalendarToken = user.CalendarToken };
    }

    /// <summary>
    /// Deliberately unauthenticated (pure build metadata, no user relation) - shows on the
    /// setup page which version is actually running. InformationalVersion instead of the
    /// numeric AssemblyVersion, because "-p:Version=$NEXT_VERSION" (.gitlab-ci.yml) sets
    /// exactly this attribute (e.g. "1.16.0"), not the AssemblyVersion, which is usually
    /// padded to x.x.0.0. PublicUnlessInvalidSession (not plain [AllowAnonymous]): reachable
    /// without any credential, but an X-Session-Token that IS present and invalid is still
    /// rejected - matches the former resolution middleware's behavior for this exempt path.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.PublicUnlessInvalidSession)]
    [HttpGet("version")]
    public ActionResult<VersionResponseDto> GetVersion() => new VersionResponseDto
    {
        Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
    };
}
