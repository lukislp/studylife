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
/// GET api/setup/overview - server-side bundle of the ~14 read-only GETs Setup.razor and its
/// cards fire on every page open (settings, capabilities, version, calendar token, study
/// programs, course goals, eight per-integration key statuses, webhook API keys, client keys,
/// invites, restore status), so a cold Setup page costs one round trip for all of them instead
/// of one each. See SetupOverviewDto for why GET api/webhooks is deliberately excluded (it
/// proxies to an external service - bundling it would ADD a network hop, not remove one).
///
/// GET-only, session-only, and free of side effects - unlike DashboardController's summary,
/// nothing here is shared computation, so every section is built by calling straight into the
/// same internal helper the corresponding controller's own action calls (SettingsController's
/// eight ToXxxApiKeyStatusDto/LoadWebhookApiKeysAsync, AuthController's LoadClientKeysAsync/
/// LoadInvitesAsync, BackupController's BuildRestoreStatus, SystemController's
/// BuildCapabilities, StudyProgramsController.LoadSummariesAsync, CourseGoalsController.ToDto) -
/// so a change to one of those endpoints' shape can't silently drift from what this bundle
/// reports. The AuthUsers row is read exactly ONCE and covers all eight key statuses plus the
/// calendar token plus (indirectly, via IOwnershipService) the owner flag, instead of the eight
/// separate SELECTs the individual endpoints would have run.
///
/// A section the individual endpoint would deny for this caller (owner-only invites/client-keys/
/// restore-status - no, client-keys is any session user, see below -, demo write-block on
/// /api/backup, no raw-backup support, no such user) is null here, never data the caller
/// couldn't already get - the corresponding card/page fetch falls back to its own request in
/// that case, so the error UX is unchanged. Deliberately uncached (unlike GET api/settings'
/// 10-minute cache): status data must be visibly fresh right after a key is generated/revoked on
/// the very same page, and every other section here is already uncached in its own endpoint.
/// </summary>
[ApiController]
[Route("api/setup")]
[Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
public class SetupController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly IConfiguration _config;
    private readonly IOwnershipService _ownership;
    private readonly DatabaseBackupService? _backupService;
    private readonly DatabaseRestoreService? _restoreService;

    public SetupController(StudyLifeDb db, IConfiguration config, IOwnershipService ownership,
        DatabaseBackupService? backupService = null, DatabaseRestoreService? restoreService = null)
    {
        _db = db;
        _config = config;
        _ownership = ownership;
        _backupService = backupService;
        _restoreService = restoreService;
    }

    [HttpGet("overview")]
    public async Task<ActionResult<SetupOverviewDto>> GetOverview()
    {
        // Never cached (see class summary) - a stale response here could show "no key" right
        // after generating one.
        Response.Headers.CacheControl = "no-store";

        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        var isOwner = await _ownership.IsOwnerAsync(userId);
        var isDemo = DemoModeGuard.IsEnabled(_config);
        // Same derivation as SystemController/DashboardController - both raw-backup services are
        // only registered in SQLite mode (Program.cs), and demo instances additionally report
        // false regardless of provider (see SystemController's own comment on this field).
        var rawBackupSupported = _backupService is not null && _restoreService is not null && !isDemo;

        var settingsEntity = await _db.Settings.AsNoTracking().FirstOrDefaultAsync() ?? new UserSettingsEntity();

        return new SetupOverviewDto
        {
            Settings = SettingsController.ToDto(settingsEntity),
            Capabilities = SystemController.BuildCapabilities(_config, rawBackupSupported),
            Version = new VersionResponseDto
            {
                Version = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
            },
            // Deliberately just a read of the existing column - GET api/system/calendar-token
            // GENERATES one when missing, which this bundle must never do (see class summary).
            CalendarToken = user.CalendarToken,
            StudyPrograms = await StudyProgramsController.LoadSummariesAsync(_db),
            CourseGoals = await _db.CourseGoals.AsNoTracking().Select(g => CourseGoalsController.ToDto(g)).ToListAsync(),

            HaApiKey = SettingsController.ToHaApiKeyStatusDto(user),
            AiApiKey = SettingsController.ToAiApiKeyStatusDto(user),
            McpApiKey = SettingsController.ToMcpApiKeyStatusDto(user),
            CaptureApiKey = SettingsController.ToCaptureApiKeyStatusDto(user),
            FocusGuardApiKey = SettingsController.ToFocusGuardApiKeyStatusDto(user),
            FocusTunesApiKey = SettingsController.ToFocusTunesApiKeyStatusDto(user),
            TrayApiKey = SettingsController.ToTrayApiKeyStatusDto(user),
            DeveloperApiKey = SettingsController.ToDeveloperApiKeyStatusDto(user),

            WebhookApiKeys = await SettingsController.LoadWebhookApiKeysAsync(_db, userId),
            // ListClientKeys has no owner restriction (any session user may see/revoke their own
            // issued add-on keys) - always populated, same as HaApiKey & co. above.
            ClientKeys = await AuthController.LoadClientKeysAsync(_db, userId),

            // Owner-only (AuthController.ListInvites Forbid()s everyone else) - null instead of
            // ever exposing invite rows to a non-owner session.
            Invites = isOwner ? await AuthController.LoadInvitesAsync(_db) : null,

            // Owner-only AND raw-backup-only; rawBackupSupported above already folds in the demo
            // check (Program.cs's /api/backup write-block middleware blocks GET api/backup/
            // restore/status entirely on a demo instance, for ANY method, before BackupController
            // is ever reached) - so a demo caller never gets this section either, matching the
            // 403 the real endpoint would give there.
            RestoreStatus = isOwner && rawBackupSupported
                ? BackupController.BuildRestoreStatus(_restoreService!)
                : null,

            IsOwner = isOwner,
            IsDemo = isDemo,
        };
    }
}
