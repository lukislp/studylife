using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    public SystemController(StudyLifeDb db,
        Services.DatabaseBackupService? backupService = null,
        Services.DatabaseRestoreService? restoreService = null)
    {
        _db = db;
        // Same derivation as BackupController.IsRawBackupAvailable: both services are
        // only registered in SQLite mode (Program.cs) - on Postgres the external backup
        // path (e.g. R2) takes over, and the raw endpoints report 501.
        _rawBackupSupported = backupService is not null && restoreService is not null;
    }

    /// <summary>
    /// Server capabilities for the client UI: "which controls make sense here".
    /// Sits behind the session gate like almost everything under /api - the querying
    /// setup cards only exist while logged in anyway. SetupBackupCard/SetupRestoreCard
    /// hide their raw-backup parts when rawBackupSupported is false (Postgres operation,
    /// backup runs externally there).
    /// </summary>
    [HttpGet("capabilities")]
    public IActionResult GetCapabilities()
    {
        // no-store: this response must never end up in an HTTP cache - a client with
        // stale capability info would otherwise show/hide UI incorrectly (see the
        // /api fallback comment in Program.cs about NSURLCache poisoning).
        Response.Headers.CacheControl = "no-store";
        return Ok(new { rawBackupSupported = _rawBackupSupported });
    }

    /// <summary>
    /// Returns the setup page's permanent calendar token - lazily created on first fetch,
    /// so a user who never uses the feature doesn't have a token sitting in the DB either.
    /// </summary>
    [HttpGet("calendar-token")]
    public async Task<ActionResult<CalendarTokenResponseDto>> GetCalendarToken()
    {
        if (SessionAuthUserId is not int userId) return Unauthorized();
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        if (user.CalendarToken is null)
        {
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
    [HttpPost("regenerate-calendar-token")]
    public async Task<ActionResult<RegenerateCalendarTokenResponseDto>> RegenerateCalendarToken()
    {
        if (SessionAuthUserId is not int userId) return Unauthorized();
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
    /// padded to x.x.0.0.
    /// </summary>
    [HttpGet("version")]
    public ActionResult<VersionResponseDto> GetVersion() => new VersionResponseDto
    {
        Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
    };

    /// <summary>Same pattern as AuthController.SessionAuthUserId/SettingsController.SessionUser:
    /// AuthUserId only for a REAL validated session, API-key requests are rejected.</summary>
    private int? SessionAuthUserId =>
        HttpContext.Items.ContainsKey(AuthSessionService.SessionItemKey)
        && HttpContext.Items[CurrentUserAccessor.HttpContextItemKey] is int userId
            ? userId
            : null;
}
