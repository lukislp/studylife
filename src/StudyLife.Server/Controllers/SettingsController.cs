using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using StudyLife.Server.Auth;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly StudyLifeDb _db;
    private readonly IDistributedCache _cache;
    private readonly SettingsCacheVersion _settingsCacheVersion;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly AiProxyClient _aiProxyClient;

    public SettingsController(StudyLifeDb db, IDistributedCache cache, SettingsCacheVersion settingsCacheVersion,
        ICurrentUserAccessor currentUser, AiProxyClient aiProxyClient)
    {
        _db = db;
        _cache = cache;
        _settingsCacheVersion = settingsCacheVersion;
        _currentUser = currentUser;
        _aiProxyClient = aiProxyClient;
    }

    [HttpGet]
    public async Task<ActionResult<UserSettingsDto>> Get()
    {
        var cacheKey = $"settings:{_currentUser.AuthUserId}:{_settingsCacheVersion.Value}";
        // 15s TTL - half the 30s client poll interval, so near-simultaneous polls from
        // multiple open clients collapse onto one query while real changes still show
        // up within about one poll cycle.
        var result = await _cache.GetOrSetAsync(this, cacheKey, TimeSpan.FromSeconds(15), async () =>
        {
            var entity = await _db.Settings.AsNoTracking().FirstOrDefaultAsync()
                ?? new UserSettingsEntity();
            return ToDto(entity);
        });

        // Audit finding A12b: ProgressShareToken is a bearer credential (it alone grants read
        // access to GET /api/progress/shared/{token}) and must only ever reach the browser's
        // own real passkey session, never an API-key caller (this endpoint is in the "ha" slot's
        // ApiKeyScopes, so Home Assistant CAN reach GET /api/settings for its own poll). Masking
        // is applied HERE, after the cache lookup above, rather than inside the cached factory:
        // the cache entry is keyed only by user+version (shared across auth types for the SAME
        // user - see CacheHelper), so a session request's cache miss would otherwise populate the
        // 15s-TTL cache with the real token, and a same-user API-key request landing within that
        // window would receive it straight from cache without ever re-running ToDto. Checking
        // result.Value (not the request's own success/failure) means a 304 short-circuit (no
        // body at all) is untouched - there's nothing to mask on an empty response.
        if (result.Value is not null && HttpContext.Items.ContainsKey(AuthSessionService.ApiKeySlotItemKey))
            result.Value.ProgressShareToken = null;

        return result;
    }

    [HttpPut]
    public async Task<ActionResult<UserSettingsDto>> Save(UserSettingsDto dto)
    {
        if (dto.StudyWindowStartHour is < 0 or > 23 || dto.StudyWindowEndHour is < 0 or > 23 || dto.StudyWindowEndHour <= dto.StudyWindowStartHour)
            return BadRequest("StudyWindowStartHour/StudyWindowEndHour must be 0-23, end after start.");

        // Only a length guard for the singleton row - the client parses the content tolerantly.
        if (dto.CustomTimerModes is { Length: > 4000 })
            return BadRequest("CustomTimerModes must be at most 4000 characters long.");

        if (dto.WeeklyGoalMinHours is < 1 or > 100 || dto.WeeklyGoalMaxHours is < 1 or > 100 || dto.WeeklyGoalMaxHours <= dto.WeeklyGoalMinHours)
            return BadRequest("WeeklyGoalMinHours/WeeklyGoalMaxHours must be 1-100, max greater than min.");

        if (dto.MonthlyGoalMinHours is < 1 or > 400 || dto.MonthlyGoalMaxHours is < 1 or > 400 || dto.MonthlyGoalMaxHours <= dto.MonthlyGoalMinHours)
            return BadRequest("MonthlyGoalMinHours/MonthlyGoalMaxHours must be 1-400, max greater than min.");

        // Null = built-in study program, otherwise the id must point to an existing program.
        if (dto.ActiveStudyProgramId.HasValue
            && !await _db.StudyPrograms.AsNoTracking().AnyAsync(p => p.Id == dto.ActiveStudyProgramId.Value))
            return BadRequest("ActiveStudyProgramId does not reference an existing study program.");

        // AsNoTracking probe first: get-or-create below already saves a freshly created row, which
        // would otherwise commit before the TargetGraduationDate check below can still reject it.
        var existingTargetGraduationDate = await _db.Settings.AsNoTracking()
            .Select(s => (DateTime?)s.TargetGraduationDate).FirstOrDefaultAsync();
        // Only reject *newly set* past dates: an already stored date may continue to be
        // stored after it has elapsed (the client always sends the complete settings object
        // on PUT - otherwise, e.g., a theme change would suddenly fail).
        if (dto.TargetGraduationDate.HasValue
            && dto.TargetGraduationDate.Value.Date < DateTime.Today
            && dto.TargetGraduationDate != existingTargetGraduationDate)
            return BadRequest("TargetGraduationDate must not be in the past.");

        var entity = await _db.Settings.GetOrCreateAsync(_db);

        // Optimistic concurrency (audit S4/S5): only enforced when the caller actually sends a
        // Version - see UserSettingsDto.Version for why null must mean "no precondition" rather
        // than "version 0". This check has a narrow, non-atomic window against
        // entity.Version++/SaveChangesAsync below within THIS request (not closed via an EF
        // IsConcurrencyToken/ExecuteUpdate WHERE clause) - deliberately accepted for this app's
        // actual threat model (a couple of personal devices racing across the up-to-30s
        // poll/cache window, not sub-millisecond concurrent writers); closing that residual
        // window is out of scope for this fix.
        if (dto.Version.HasValue && dto.Version.Value != entity.Version)
            return Conflict(ToDto(entity));

        entity.SelectedCourseIds = string.Join(",", dto.SelectedCourseIds);
        entity.CompletedCourseIds = string.Join(",", dto.CompletedCourseIds);
        entity.Theme = dto.Theme;
        entity.AccentColor = dto.AccentColor;
        entity.AutoSwitchFocus = dto.AutoSwitchFocus;
        entity.AutoSwitchMinutesBefore = dto.AutoSwitchMinutesBefore;
        entity.MotivationalStyle = dto.MotivationalStyle;
        entity.SessionReminderMinutes = dto.SessionReminderMinutes;
        entity.CourseGoalReminderDays = dto.CourseGoalReminderDays;
        entity.InactivityThresholdDays = dto.InactivityThresholdDays;
        entity.StudyWindowStartHour = dto.StudyWindowStartHour;
        entity.StudyWindowEndHour = dto.StudyWindowEndHour;
        entity.StudyDays = dto.StudyDays;
        entity.TargetGraduationDate = dto.TargetGraduationDate;
        entity.CustomTimerModes = dto.CustomTimerModes;
        entity.WeeklyGoalMinHours = dto.WeeklyGoalMinHours;
        entity.WeeklyGoalMaxHours = dto.WeeklyGoalMaxHours;
        entity.MonthlyGoalMinHours = dto.MonthlyGoalMinHours;
        entity.MonthlyGoalMaxHours = dto.MonthlyGoalMaxHours;
        entity.SessionRemindersEnabled = dto.SessionRemindersEnabled;
        entity.CourseGoalRemindersEnabled = dto.CourseGoalRemindersEnabled;
        entity.InactivityRemindersEnabled = dto.InactivityRemindersEnabled;
        entity.AchievementNotificationsEnabled = dto.AchievementNotificationsEnabled;
        entity.WeeklyReportEnabled = dto.WeeklyReportEnabled;
        entity.DailyMotivationEnabled = dto.DailyMotivationEnabled;
        entity.PerCourseInactivityRemindersEnabled = dto.PerCourseInactivityRemindersEnabled;
        entity.StreakRiskRemindersEnabled = dto.StreakRiskRemindersEnabled;
        entity.WeeklyGoalNudgeEnabled = dto.WeeklyGoalNudgeEnabled;
        entity.CourseAlmostDoneRemindersEnabled = dto.CourseAlmostDoneRemindersEnabled;
        entity.BestStudyTimeRemindersEnabled = dto.BestStudyTimeRemindersEnabled;
        entity.ComebackNudgeEnabled = dto.ComebackNudgeEnabled;
        entity.NewRecordNotificationsEnabled = dto.NewRecordNotificationsEnabled;
        entity.MonthlyReportEnabled = dto.MonthlyReportEnabled;
        entity.ActiveStudyProgramId = dto.ActiveStudyProgramId;
        // LastBackupDownloadAt deliberately NOT set here (audit F1): it is documented on
        // UserSettingsEntity as "set directly in BackupController, not via the normal settings
        // PUT" - but until this fix, this full-row-replace endpoint quietly overwrote it from
        // whatever the client's DTO happened to carry anyway (typically its own last-fetched
        // copy, but a stale/offline client could just as easily send an old or null value and
        // silently revert the backup-reminder state, or forge it outright). Same rationale as
        // the ProgressShareEnabled/ProgressShareToken exclusion below - a field with its own
        // dedicated write path must not also be reachable through the generic PUT.
        // ProgressShareEnabled/ProgressShareToken deliberately NOT set here - they have their
        // own write path via the three endpoints below (same rationale as LastBackupDownloadAt:
        // "set directly, not via the normal settings PUT"). Reason: Enable must atomically
        // generate a cryptographically strong token if none exists yet - a client roundtrip via
        // the generic PUT could otherwise persist a "half-activated" state (Enabled=true,
        // Token=null).
        entity.Version++;
        await _db.SaveChangesAsync();
        _settingsCacheVersion.Value++;
        return ToDto(entity);
    }

    /// <summary>
    /// Activates the read-only progress link (ProgressController.GetShared) and always
    /// generates a new token while doing so - Disable now also deletes the token (see there),
    /// so there's never an "old" token left that could be reused.
    /// </summary>
    // SessionOnly (audit finding A3 cleanup): a leaked API key must not be able to activate a
    // public read-only link to the account's data on its own, same rationale as the ha-api-key
    // group above. Disable/Regenerate below are intentionally left at the plain ApiAccess level
    // they already had - narrowing only the specific gap this refactor's audit called out
    // (activating a NEW public link) without changing the two endpoints nobody flagged.
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("progress-share/enable")]
    public async Task<ActionResult<UserSettingsDto>> EnableProgressShare()
    {
        var entity = await _db.Settings.GetOrCreateAsync(_db);
        entity.ProgressShareEnabled = true;
        entity.ProgressShareToken = GenerateShareToken();
        await _db.SaveChangesAsync();
        _settingsCacheVersion.Value++;
        return ToDto(entity);
    }

    /// <summary>
    /// Disables the link AND deletes the token (GET /api/progress/shared/{token} would
    /// respond with 404 afterward anyway, but the cleared token ensures that an already
    /// shared/leaked link doesn't become valid again just by re-enabling - anyone who has
    /// actually passed on the link and wants to protect against that should be able to rely
    /// on "Disable" alone, without necessarily having to know about the separate
    /// "Regenerate" button).
    /// </summary>
    [HttpPost("progress-share/disable")]
    public async Task<ActionResult<UserSettingsDto>> DisableProgressShare()
    {
        var entity = await _db.Settings.FirstOrDefaultAsync();
        if (entity == null) return NotFound();
        entity.ProgressShareEnabled = false;
        entity.ProgressShareToken = null;
        await _db.SaveChangesAsync();
        _settingsCacheVersion.Value++;
        return ToDto(entity);
    }

    /// <summary>
    /// Manual "regenerate now" (e.g. on suspicion of a leak, or to specifically invalidate a
    /// previously shared link) - immediately breaks any existing link, analogous to
    /// CalendarTokenProvider.Regenerate. Also enables the feature in the process
    /// (regenerating implies "I want a valid link again").
    /// </summary>
    [HttpPost("progress-share/regenerate")]
    public async Task<ActionResult<UserSettingsDto>> RegenerateProgressShareToken()
    {
        var entity = await _db.Settings.FirstOrDefaultAsync();
        if (entity == null) return NotFound();
        entity.ProgressShareToken = GenerateShareToken();
        entity.ProgressShareEnabled = true;
        await _db.SaveChangesAsync();
        _settingsCacheVersion.Value++;
        return ToDto(entity);
    }

    // ── Per-user API key for Home Assistant (phase 3) ──────────────────────
    // Same endpoint pattern as progress-share/enable|disable|regenerate above (dedicated
    // POST write paths instead of the generic settings PUT), but with two peculiarities:
    // (1) The key lives on AuthUserEntity instead of UserSettingsEntity - it identifies the
    //     USER at the /api gate, long before settings are even resolved.
    // (2) All three endpoints require a REAL passkey session (SessionItemKey), not just
    //     any gate authentication: otherwise a leaked API key could reissue itself or
    //     revoke a user's key.

    /// <summary>Status for the setup card: does a key exist, and since when? Deliberately NO
    /// plaintext access - the key, like a password, is only visible once at generation.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("ha-api-key")]
    public async Task<ActionResult<HaApiKeyStatusDto>> GetHaApiKeyStatus()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();
        return new HaApiKeyStatusDto { HasKey = user.ApiKeyHash != null, CreatedAt = user.ApiKeyCreatedAt };
    }

    /// <summary>
    /// Generates a new long-lived per-user API key (immediately replaces any existing one -
    /// the old hash is overwritten, the old key gets 401 from now on). The PLAINTEXT is
    /// returned exactly once in this response; only the SHA-256 hash is stored (same pattern
    /// as AuthSessionService.IssueSession). No rotation, no expiry - an explicit user
    /// decision ("long lived"), because Home Assistant has no live session that could react
    /// to a rotation.
    /// </summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("ha-api-key/generate")]
    public async Task<ActionResult<HaApiKeyGenerateResponseDto>> GenerateHaApiKey()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        var key = AuthSessionService.GenerateToken();
        user.ApiKeyHash = AuthSessionService.HashToken(key);
        user.ApiKeyCreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new HaApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = user.ApiKeyCreatedAt.Value };
    }

    /// <summary>Permanently revokes the per-user API key (hash is deleted) - Home Assistant
    /// gets 401 from the next request onward and shows its reauth flow.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("ha-api-key/revoke")]
    public async Task<IActionResult> RevokeHaApiKey()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        user.ApiKeyHash = null;
        user.ApiKeyCreatedAt = null;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Same three-endpoint shape as the ha-api-key group above, for the separate studylife-ai
    // key slot (AuthUserEntity.AiApiKeyHash) - deliberately duplicated rather than
    // parameterized into one generic "integration key" mechanism: a real per-integration state
    // machine (open/rotate/revoke on a shared code path) would be more indirection than the
    // current need justifies. Still true with the third slot added below for studylife-mcp
    // (McpApiKeyHash) - three near-identical endpoint trios, on purpose.

    /// <summary>Status for the setup card: does an AI-integration key exist, and since when?
    /// Same "never the plaintext" rule as GetHaApiKeyStatus.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("ai-api-key")]
    public async Task<ActionResult<AiApiKeyStatusDto>> GetAiApiKeyStatus()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();
        return new AiApiKeyStatusDto { HasKey = user.AiApiKeyHash != null, CreatedAt = user.AiApiKeyCreatedAt };
    }

    /// <summary>Generates a new long-lived per-user API key for studylife-ai (immediately
    /// replaces any existing one in this slot only - ApiKeyHash/Home Assistant is untouched).
    /// Same one-time-plaintext shape as GenerateHaApiKey. Also registers the plaintext with
    /// studylife-ai (AiProxyClient.RegisterKeyAsync) at this one moment it exists - see
    /// docs/decisions.md "M4.5 Multi-user support" in the studylife-ai repo,
    /// "Registration-on-generate": studylife-ai cannot retrieve it later, only the hash is
    /// ever stored here. AI key outbox (audit A7): the intent is durably enqueued BEFORE the
    /// immediate delivery attempt, so a studylife-ai outage right now doesn't lose the plaintext
    /// forever - on confirmed delivery the row is deleted immediately (fast path, identical
    /// behavior to before the outbox existed); otherwise BackgroundTaskService.RunAiKeyOutboxAsync
    /// retries it with backoff. Either way key generation itself never fails because of this.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("ai-api-key/generate")]
    public async Task<ActionResult<AiApiKeyGenerateResponseDto>> GenerateAiApiKey(CancellationToken ct)
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        var key = AuthSessionService.GenerateToken();
        user.AiApiKeyHash = AuthSessionService.HashToken(key);
        user.AiApiKeyCreatedAt = DateTime.UtcNow;
        var outboxRow = new AiKeyOutboxEntity
        {
            AuthUserId = userId,
            Action = AiKeyOutboxEntity.ActionRegister,
            AiApiKeyPlaintext = key,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiKeyOutbox.Add(outboxRow);
        await _db.SaveChangesAsync();

        if (await _aiProxyClient.RegisterKeyAsync(userId, key, ct))
        {
            _db.AiKeyOutbox.Remove(outboxRow);
        }
        else
        {
            // Record this as the row's first attempt (same bookkeeping RunAiKeyOutboxAsync does
            // for every retry), so the background drain's backoff starts counting from here
            // instead of treating the fast-path attempt as if it never happened.
            outboxRow.Attempts = 1;
            outboxRow.LastAttemptAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return new AiApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = user.AiApiKeyCreatedAt.Value };
    }

    /// <summary>Permanently revokes the studylife-ai API key (hash is deleted) - studylife-ai
    /// gets 401 from the next request onward. Home Assistant's key is untouched. Also tells
    /// studylife-ai to forget its registered copy (AiProxyClient.RevokeKeyAsync) - without
    /// this, a revoked-here key would keep working there indefinitely. Same outbox-first pattern
    /// as GenerateAiApiKey above, so an unreachable studylife-ai doesn't leave the two databases
    /// disagreeing forever (audit A7).</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("ai-api-key/revoke")]
    public async Task<IActionResult> RevokeAiApiKey(CancellationToken ct)
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        user.AiApiKeyHash = null;
        user.AiApiKeyCreatedAt = null;
        var outboxRow = new AiKeyOutboxEntity
        {
            AuthUserId = userId,
            Action = AiKeyOutboxEntity.ActionRevoke,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiKeyOutbox.Add(outboxRow);
        await _db.SaveChangesAsync();

        if (await _aiProxyClient.RevokeKeyAsync(userId, ct))
        {
            _db.AiKeyOutbox.Remove(outboxRow);
        }
        else
        {
            outboxRow.Attempts = 1;
            outboxRow.LastAttemptAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Same three-endpoint shape again, for the separate studylife-mcp key slot
    // (AuthUserEntity.McpApiKeyHash). Unlike the ai-api-key group above, there is no
    // server-to-server registration call here: studylife-mcp is a locally-run MCP server (like
    // Home Assistant, not like the hosted studylife-ai microservice) that the user configures
    // with the plaintext key themselves - this backend never needs to hand it to anyone.

    /// <summary>Status for the setup card: does an MCP-integration key exist, and since when?
    /// Same "never the plaintext" rule as GetHaApiKeyStatus.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("mcp-api-key")]
    public async Task<ActionResult<McpApiKeyStatusDto>> GetMcpApiKeyStatus()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();
        return new McpApiKeyStatusDto { HasKey = user.McpApiKeyHash != null, CreatedAt = user.McpApiKeyCreatedAt };
    }

    /// <summary>Generates a new long-lived per-user API key for studylife-mcp (immediately
    /// replaces any existing one in this slot only - ApiKeyHash/AiApiKeyHash are untouched).
    /// Same one-time-plaintext shape as GenerateHaApiKey.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("mcp-api-key/generate")]
    public async Task<ActionResult<McpApiKeyGenerateResponseDto>> GenerateMcpApiKey()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        var key = RotateMcpKey(user, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        return new McpApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = user.McpApiKeyCreatedAt!.Value };
    }

    /// <summary>Core of GenerateMcpApiKey above - also reused by AuthController.McpConnect (the
    /// MCP OAuth connect flow, identity contract v1 §2 step 3) so the two code paths that rotate
    /// the same key slot can't drift apart. Caller must SaveChanges.</summary>
    internal static string RotateMcpKey(AuthUserEntity user, DateTime now)
    {
        var key = AuthSessionService.GenerateToken();
        user.McpApiKeyHash = AuthSessionService.HashToken(key);
        user.McpApiKeyCreatedAt = now;
        return key;
    }

    /// <summary>Permanently revokes the studylife-mcp API key (hash is deleted) - studylife-mcp
    /// gets 401 from the next request onward. The other two slots are untouched.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("mcp-api-key/revoke")]
    public async Task<IActionResult> RevokeMcpApiKey()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        user.McpApiKeyHash = null;
        user.McpApiKeyCreatedAt = null;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Same three-endpoint shape again, for the separate studylife-capture browser-extension key
    // slot (AuthUserEntity.CaptureApiKeyHash). Like the mcp-api-key group (and unlike ai-api-key),
    // there is no server-to-server registration call here - the extension holds the plaintext
    // key itself (pasted into its own settings popup) and sends it directly as X-Api-Key on
    // every request, resolved by the same gate middleware every other key type goes through.

    /// <summary>Status for the setup card: does a Capture-extension key exist, and since when?
    /// Same "never the plaintext" rule as GetHaApiKeyStatus.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("capture-api-key")]
    public async Task<ActionResult<CaptureApiKeyStatusDto>> GetCaptureApiKeyStatus()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();
        return new CaptureApiKeyStatusDto { HasKey = user.CaptureApiKeyHash != null, CreatedAt = user.CaptureApiKeyCreatedAt };
    }

    /// <summary>Generates a new long-lived per-user API key for studylife-capture (immediately
    /// replaces any existing one in this slot only - the other three slots are untouched).
    /// Same one-time-plaintext shape as GenerateHaApiKey.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("capture-api-key/generate")]
    public async Task<ActionResult<CaptureApiKeyGenerateResponseDto>> GenerateCaptureApiKey()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        var key = RotateCaptureKey(user, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        return new CaptureApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = user.CaptureApiKeyCreatedAt!.Value };
    }

    /// <summary>Core of GenerateCaptureApiKey above - also reused by AuthController.CaptureConnect
    /// (the capture browser-consent connect flow, identity contract v1 §2 generalized to a second
    /// audience) so the two code paths that rotate the same key slot can't drift apart. Same
    /// pattern as RotateMcpKey. Caller must SaveChanges.</summary>
    internal static string RotateCaptureKey(AuthUserEntity user, DateTime now)
    {
        var key = AuthSessionService.GenerateToken();
        user.CaptureApiKeyHash = AuthSessionService.HashToken(key);
        user.CaptureApiKeyCreatedAt = now;
        return key;
    }

    /// <summary>Permanently revokes the studylife-capture API key (hash is deleted) - the
    /// extension gets 401 from the next request onward. The other three slots are untouched.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("capture-api-key/revoke")]
    public async Task<IActionResult> RevokeCaptureApiKey()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        user.CaptureApiKeyHash = null;
        user.CaptureApiKeyCreatedAt = null;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Same two-endpoint shape again (status + revoke, no generate), for the separate
    // studylife-focusguard browser-extension key slot (AuthUserEntity.FocusGuardApiKeyHash).
    // Like capture/mcp, provisioning happens exclusively through the consent flow
    // (AuthController.FocusGuardConnect), never a plaintext-paste here.

    /// <summary>Status for the setup card: does a FocusGuard key exist, and since when? Same
    /// "never the plaintext" rule as GetCaptureApiKeyStatus.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpGet("focusguard-api-key")]
    public async Task<ActionResult<FocusGuardApiKeyStatusDto>> GetFocusGuardApiKeyStatus()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();
        return new FocusGuardApiKeyStatusDto { HasKey = user.FocusGuardApiKeyHash != null, CreatedAt = user.FocusGuardApiKeyCreatedAt };
    }

    /// <summary>Generates a new long-lived per-user API key for studylife-focusguard (immediately
    /// replaces any existing one in this slot only). Not the extension's actual path (it uses the
    /// consent flow, AuthController.FocusGuardConnect) - kept for the same uniform admin/test
    /// surface every other slot has (see GenerateCaptureApiKey).</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("focusguard-api-key/generate")]
    public async Task<ActionResult<FocusGuardApiKeyGenerateResponseDto>> GenerateFocusGuardApiKey()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        var key = RotateFocusGuardKey(user, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        return new FocusGuardApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = user.FocusGuardApiKeyCreatedAt!.Value };
    }

    /// <summary>Core of AuthController.FocusGuardConnect (the focusguard browser-consent connect
    /// flow, identity contract v1 §2 generalized to a third audience). Same pattern as
    /// RotateCaptureKey/RotateMcpKey. Caller must SaveChanges.</summary>
    internal static string RotateFocusGuardKey(AuthUserEntity user, DateTime now)
    {
        var key = AuthSessionService.GenerateToken();
        user.FocusGuardApiKeyHash = AuthSessionService.HashToken(key);
        user.FocusGuardApiKeyCreatedAt = now;
        return key;
    }

    /// <summary>Permanently revokes the studylife-focusguard API key (hash is deleted) - the
    /// extension gets 401 from the next poll onward. The other four slots are untouched.</summary>
    [Authorize(Policy = StudyLifeAuthorizationPolicies.SessionOnly)]
    [HttpPost("focusguard-api-key/revoke")]
    public async Task<IActionResult> RevokeFocusGuardApiKey()
    {
        var userId = HttpContext.SessionAuthUserId()!.Value; // guaranteed by [Authorize(SessionOnly)]
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        user.FocusGuardApiKeyHash = null;
        user.FocusGuardApiKeyCreatedAt = null;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Same CSPRNG technique as CalendarTokenProvider.GenerateToken: 32 bytes, base64url
    // without padding - already URL-safe, because the token travels as part of the client
    // route path (/shared/{token}) and the API path (/api/progress/shared/{token}).
    private static string GenerateShareToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // internal instead of private: reused by BackupController (JSON export), so the export
    // projection doesn't have to duplicate the same mapping a second time.
    internal static UserSettingsDto ToDto(UserSettingsEntity e) => new()
    {
        Version = e.Version,
        SelectedCourseIds = string.IsNullOrEmpty(e.SelectedCourseIds)
            ? new List<int> { 1, 2, 3, 4 }
            : CommaSeparatedIds.Parse(e.SelectedCourseIds),
        CompletedCourseIds = CommaSeparatedIds.Parse(e.CompletedCourseIds),
        Theme = e.Theme,
        AccentColor = string.IsNullOrWhiteSpace(e.AccentColor) ? "coral" : e.AccentColor,
        AutoSwitchFocus = e.AutoSwitchFocus,
        AutoSwitchMinutesBefore = e.AutoSwitchMinutesBefore,
        MotivationalStyle = e.MotivationalStyle,
        SessionReminderMinutes = string.IsNullOrWhiteSpace(e.SessionReminderMinutes) ? "60,30,10,5,3,2,1" : e.SessionReminderMinutes,
        CourseGoalReminderDays = string.IsNullOrWhiteSpace(e.CourseGoalReminderDays) ? "14,7,3,1,0" : e.CourseGoalReminderDays,
        InactivityThresholdDays = e.InactivityThresholdDays > 0 ? e.InactivityThresholdDays : 5,
        StudyWindowStartHour = e.StudyWindowStartHour is >= 0 and <= 23 ? e.StudyWindowStartHour : 8,
        StudyWindowEndHour = e.StudyWindowEndHour is > 0 and <= 23 ? e.StudyWindowEndHour : 21,
        StudyDays = string.IsNullOrWhiteSpace(e.StudyDays) ? "0,1,2,3,4,5,6" : e.StudyDays,
        TargetGraduationDate = e.TargetGraduationDate,
        CustomTimerModes = e.CustomTimerModes,
        WeeklyGoalMinHours = e.WeeklyGoalMinHours is >= 1 and <= 100 ? e.WeeklyGoalMinHours : 25,
        WeeklyGoalMaxHours = e.WeeklyGoalMaxHours is >= 1 and <= 100 ? e.WeeklyGoalMaxHours : 30,
        MonthlyGoalMinHours = e.MonthlyGoalMinHours is >= 1 and <= 400 ? e.MonthlyGoalMinHours : 100,
        MonthlyGoalMaxHours = e.MonthlyGoalMaxHours is >= 1 and <= 400 ? e.MonthlyGoalMaxHours : 130,
        SessionRemindersEnabled = e.SessionRemindersEnabled,
        CourseGoalRemindersEnabled = e.CourseGoalRemindersEnabled,
        InactivityRemindersEnabled = e.InactivityRemindersEnabled,
        AchievementNotificationsEnabled = e.AchievementNotificationsEnabled,
        WeeklyReportEnabled = e.WeeklyReportEnabled,
        DailyMotivationEnabled = e.DailyMotivationEnabled,
        PerCourseInactivityRemindersEnabled = e.PerCourseInactivityRemindersEnabled,
        StreakRiskRemindersEnabled = e.StreakRiskRemindersEnabled,
        WeeklyGoalNudgeEnabled = e.WeeklyGoalNudgeEnabled,
        CourseAlmostDoneRemindersEnabled = e.CourseAlmostDoneRemindersEnabled,
        BestStudyTimeRemindersEnabled = e.BestStudyTimeRemindersEnabled,
        ComebackNudgeEnabled = e.ComebackNudgeEnabled,
        NewRecordNotificationsEnabled = e.NewRecordNotificationsEnabled,
        MonthlyReportEnabled = e.MonthlyReportEnabled,
        LastBackupDownloadAt = e.LastBackupDownloadAt,
        ActiveStudyProgramId = e.ActiveStudyProgramId,
        ProgressShareEnabled = e.ProgressShareEnabled,
        ProgressShareToken = e.ProgressShareToken,
    };
}
