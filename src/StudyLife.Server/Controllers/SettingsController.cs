using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
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
    public Task<ActionResult<UserSettingsDto>> Get()
    {
        var cacheKey = $"settings:{_currentUser.AuthUserId}:{_settingsCacheVersion.Value}";
        // 15s TTL - half the 30s client poll interval, so near-simultaneous polls from
        // multiple open clients collapse onto one query while real changes still show
        // up within about one poll cycle.
        return _cache.GetOrSetAsync(this, cacheKey, TimeSpan.FromSeconds(15), async () =>
        {
            var entity = await _db.Settings.AsNoTracking().FirstOrDefaultAsync()
                ?? new UserSettingsEntity();
            return ToDto(entity);
        });
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

        var entity = await _db.Settings.FirstOrDefaultAsync();
        // Only reject *newly set* past dates: an already stored date may continue to be
        // stored after it has elapsed (the client always sends the complete settings object
        // on PUT - otherwise, e.g., a theme change would suddenly fail).
        if (dto.TargetGraduationDate.HasValue
            && dto.TargetGraduationDate.Value.Date < DateTime.Today
            && dto.TargetGraduationDate != entity?.TargetGraduationDate)
            return BadRequest("TargetGraduationDate must not be in the past.");

        if (entity == null)
        {
            entity = new UserSettingsEntity();
            _db.Settings.Add(entity);
        }
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
        entity.LastBackupDownloadAt = dto.LastBackupDownloadAt;
        entity.ActiveStudyProgramId = dto.ActiveStudyProgramId;
        // ProgressShareEnabled/ProgressShareToken deliberately NOT set here - they have their
        // own write path via the three endpoints below (same rationale as LastBackupDownloadAt:
        // "set directly, not via the normal settings PUT"). Reason: Enable must atomically
        // generate a cryptographically strong token if none exists yet - a client roundtrip via
        // the generic PUT could otherwise persist a "half-activated" state (Enabled=true,
        // Token=null).
        await _db.SaveChangesAsync();
        _settingsCacheVersion.Value++;
        return ToDto(entity);
    }

    /// <summary>
    /// Activates the read-only progress link (ProgressController.GetShared) and always
    /// generates a new token while doing so - Disable now also deletes the token (see there),
    /// so there's never an "old" token left that could be reused.
    /// </summary>
    [HttpPost("progress-share/enable")]
    public async Task<ActionResult<UserSettingsDto>> EnableProgressShare()
    {
        var entity = await _db.Settings.FirstOrDefaultAsync();
        if (entity == null)
        {
            entity = new UserSettingsEntity();
            _db.Settings.Add(entity);
        }
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
    [HttpGet("ha-api-key")]
    public async Task<ActionResult<HaApiKeyStatusDto>> GetHaApiKeyStatus()
    {
        if (SessionUser is not int userId) return Unauthorized();
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
    [HttpPost("ha-api-key/generate")]
    public async Task<ActionResult<HaApiKeyGenerateResponseDto>> GenerateHaApiKey()
    {
        if (SessionUser is not int userId) return Unauthorized();
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
    [HttpPost("ha-api-key/revoke")]
    public async Task<IActionResult> RevokeHaApiKey()
    {
        if (SessionUser is not int userId) return Unauthorized();
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
    [HttpGet("ai-api-key")]
    public async Task<ActionResult<AiApiKeyStatusDto>> GetAiApiKeyStatus()
    {
        if (SessionUser is not int userId) return Unauthorized();
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
    /// ever stored here. A failed registration doesn't fail key generation itself (an
    /// studylife-ai outage shouldn't block this StudyLife-native feature) - the user just
    /// can't use /agent or get their notes ingested until it's retried.</summary>
    [HttpPost("ai-api-key/generate")]
    public async Task<ActionResult<AiApiKeyGenerateResponseDto>> GenerateAiApiKey(CancellationToken ct)
    {
        if (SessionUser is not int userId) return Unauthorized();
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        var key = AuthSessionService.GenerateToken();
        user.AiApiKeyHash = AuthSessionService.HashToken(key);
        user.AiApiKeyCreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _aiProxyClient.RegisterKeyAsync(userId, key, ct);
        return new AiApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = user.AiApiKeyCreatedAt.Value };
    }

    /// <summary>Permanently revokes the studylife-ai API key (hash is deleted) - studylife-ai
    /// gets 401 from the next request onward. Home Assistant's key is untouched. Also tells
    /// studylife-ai to forget its registered copy (AiProxyClient.RevokeKeyAsync) - without
    /// this, a revoked-here key would keep working there indefinitely.</summary>
    [HttpPost("ai-api-key/revoke")]
    public async Task<IActionResult> RevokeAiApiKey(CancellationToken ct)
    {
        if (SessionUser is not int userId) return Unauthorized();
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        user.AiApiKeyHash = null;
        user.AiApiKeyCreatedAt = null;
        await _db.SaveChangesAsync();
        await _aiProxyClient.RevokeKeyAsync(userId, ct);
        return NoContent();
    }

    // Same three-endpoint shape again, for the separate studylife-mcp key slot
    // (AuthUserEntity.McpApiKeyHash). Unlike the ai-api-key group above, there is no
    // server-to-server registration call here: studylife-mcp is a locally-run MCP server (like
    // Home Assistant, not like the hosted studylife-ai microservice) that the user configures
    // with the plaintext key themselves - this backend never needs to hand it to anyone.

    /// <summary>Status for the setup card: does an MCP-integration key exist, and since when?
    /// Same "never the plaintext" rule as GetHaApiKeyStatus.</summary>
    [HttpGet("mcp-api-key")]
    public async Task<ActionResult<McpApiKeyStatusDto>> GetMcpApiKeyStatus()
    {
        if (SessionUser is not int userId) return Unauthorized();
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();
        return new McpApiKeyStatusDto { HasKey = user.McpApiKeyHash != null, CreatedAt = user.McpApiKeyCreatedAt };
    }

    /// <summary>Generates a new long-lived per-user API key for studylife-mcp (immediately
    /// replaces any existing one in this slot only - ApiKeyHash/AiApiKeyHash are untouched).
    /// Same one-time-plaintext shape as GenerateHaApiKey.</summary>
    [HttpPost("mcp-api-key/generate")]
    public async Task<ActionResult<McpApiKeyGenerateResponseDto>> GenerateMcpApiKey()
    {
        if (SessionUser is not int userId) return Unauthorized();
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        var key = AuthSessionService.GenerateToken();
        user.McpApiKeyHash = AuthSessionService.HashToken(key);
        user.McpApiKeyCreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new McpApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = user.McpApiKeyCreatedAt.Value };
    }

    /// <summary>Permanently revokes the studylife-mcp API key (hash is deleted) - studylife-mcp
    /// gets 401 from the next request onward. The other two slots are untouched.</summary>
    [HttpPost("mcp-api-key/revoke")]
    public async Task<IActionResult> RevokeMcpApiKey()
    {
        if (SessionUser is not int userId) return Unauthorized();
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
    [HttpGet("capture-api-key")]
    public async Task<ActionResult<CaptureApiKeyStatusDto>> GetCaptureApiKeyStatus()
    {
        if (SessionUser is not int userId) return Unauthorized();
        var user = await _db.AuthUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();
        return new CaptureApiKeyStatusDto { HasKey = user.CaptureApiKeyHash != null, CreatedAt = user.CaptureApiKeyCreatedAt };
    }

    /// <summary>Generates a new long-lived per-user API key for studylife-capture (immediately
    /// replaces any existing one in this slot only - the other three slots are untouched).
    /// Same one-time-plaintext shape as GenerateHaApiKey.</summary>
    [HttpPost("capture-api-key/generate")]
    public async Task<ActionResult<CaptureApiKeyGenerateResponseDto>> GenerateCaptureApiKey()
    {
        if (SessionUser is not int userId) return Unauthorized();
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        var key = AuthSessionService.GenerateToken();
        user.CaptureApiKeyHash = AuthSessionService.HashToken(key);
        user.CaptureApiKeyCreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new CaptureApiKeyGenerateResponseDto { ApiKey = key, CreatedAt = user.CaptureApiKeyCreatedAt.Value };
    }

    /// <summary>Permanently revokes the studylife-capture API key (hash is deleted) - the
    /// extension gets 401 from the next request onward. The other three slots are untouched.</summary>
    [HttpPost("capture-api-key/revoke")]
    public async Task<IActionResult> RevokeCaptureApiKey()
    {
        if (SessionUser is not int userId) return Unauthorized();
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Unauthorized();

        user.CaptureApiKeyHash = null;
        user.CaptureApiKeyCreatedAt = null;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>AuthUserId of the request, but ONLY if it came via a real validated passkey
    /// session (same pattern as AuthController.SessionAuthUserId) - API-key requests
    /// don't set the SessionItemKey and are rejected here.</summary>
    private int? SessionUser =>
        HttpContext.Items.ContainsKey(AuthSessionService.SessionItemKey)
        && _currentUser.AuthUserId is var userId and > 0
            ? userId
            : null;

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
        SelectedCourseIds = string.IsNullOrEmpty(e.SelectedCourseIds)
            ? new List<int> { 1, 2, 3, 4 }
            : e.SelectedCourseIds.Split(',').Select(int.Parse).ToList(),
        CompletedCourseIds = string.IsNullOrEmpty(e.CompletedCourseIds)
            ? new List<int>()
            : e.CompletedCourseIds.Split(',').Select(int.Parse).ToList(),
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
