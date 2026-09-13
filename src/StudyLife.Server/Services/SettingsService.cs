using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// The /api/settings domain operations: reading the singleton row, the validated full-row
/// upsert with its optional Version precondition, the three progress-share write paths and the
/// built-in-program dismissal - each with the settings-cache bump it owes.
/// <see cref="SettingsController"/> keeps route binding, the ETag/Cache-Control wrapper around
/// the cached GET, the ProgressShareToken masking for API-key callers (which reads
/// HttpContext.Items and must happen after the cache lookup, see the controller) and the
/// mapping of a <see cref="ServiceResult{T}"/> onto a status code.
/// </summary>
public interface ISettingsService
{
    /// <summary>Cache key for the cached GET - changes on every write via the per-user version
    /// counter, so the controller's TTL is only a memory bound.</summary>
    Task<string> CacheKeyAsync();

    /// <summary>Reads the row (or the defaults if it doesn't exist yet) as a DTO - the cache
    /// factory behind GET /api/settings. Deliberately does NOT create a row.</summary>
    Task<UserSettingsDto> LoadAsync();

    /// <summary>Full-row upsert. <see cref="ServiceOutcome.Conflict"/> carries the CURRENT row
    /// so the caller can rebase - see UserSettingsDto.Version for why the precondition is
    /// optional.</summary>
    Task<ServiceResult<UserSettingsDto>> SaveAsync(UserSettingsDto dto);

    Task<UserSettingsDto> EnableProgressShareAsync();
    Task<ServiceResult<UserSettingsDto>> DisableProgressShareAsync();
    Task<ServiceResult<UserSettingsDto>> RegenerateProgressShareTokenAsync();
    Task<ServiceResult<UserSettingsDto>> DismissBuiltInProgramAsync();
}

public class SettingsService(
    StudyLifeDb db,
    SettingsCacheVersion settingsCacheVersion,
    ICurrentUserAccessor currentUser) : ISettingsService
{
    /// <summary>Memory bound for the version-keyed GET cache, not a freshness mechanism. 15s used
    /// to expire before the 30s client poll ever came back - see SessionService.CacheTtl for the
    /// full reasoning behind ten minutes.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public async Task<string> CacheKeyAsync() =>
        $"settings:{currentUser.AuthUserId}:{await settingsCacheVersion.GetAsync(currentUser.AuthUserId)}";

    public async Task<UserSettingsDto> LoadAsync()
    {
        var entity = await db.Settings.AsNoTracking().FirstOrDefaultAsync()
            ?? new UserSettingsEntity();
        return ToDto(entity);
    }

    public async Task<ServiceResult<UserSettingsDto>> SaveAsync(UserSettingsDto dto)
    {
        if (dto.StudyWindowStartHour is < 0 or > 23 || dto.StudyWindowEndHour is < 0 or > 23 || dto.StudyWindowEndHour <= dto.StudyWindowStartHour)
            return ServiceResult<UserSettingsDto>.Invalid("StudyWindowStartHour/StudyWindowEndHour must be 0-23, end after start.");

        // Only a length guard for the singleton row - the client parses the content tolerantly.
        if (dto.CustomTimerModes is { Length: > 4000 })
            return ServiceResult<UserSettingsDto>.Invalid("CustomTimerModes must be at most 4000 characters long.");

        if (dto.WeeklyGoalMinHours is < 1 or > 100 || dto.WeeklyGoalMaxHours is < 1 or > 100 || dto.WeeklyGoalMaxHours <= dto.WeeklyGoalMinHours)
            return ServiceResult<UserSettingsDto>.Invalid("WeeklyGoalMinHours/WeeklyGoalMaxHours must be 1-100, max greater than min.");

        if (dto.MonthlyGoalMinHours is < 1 or > 400 || dto.MonthlyGoalMaxHours is < 1 or > 400 || dto.MonthlyGoalMaxHours <= dto.MonthlyGoalMinHours)
            return ServiceResult<UserSettingsDto>.Invalid("MonthlyGoalMinHours/MonthlyGoalMaxHours must be 1-400, max greater than min.");

        // Null = built-in study program, otherwise the id must point to an existing program.
        if (dto.ActiveStudyProgramId.HasValue
            && !await db.StudyPrograms.AsNoTracking().AnyAsync(p => p.Id == dto.ActiveStudyProgramId.Value))
            return ServiceResult<UserSettingsDto>.Invalid("ActiveStudyProgramId does not reference an existing study program.");

        // AsNoTracking probe first: get-or-create below already saves a freshly created row, which
        // would otherwise commit before the TargetGraduationDate check below can still reject it.
        var existingTargetGraduationDate = await db.Settings.AsNoTracking()
            .Select(s => (DateTime?)s.TargetGraduationDate).FirstOrDefaultAsync();
        // Only reject *newly set* past dates: an already stored date may continue to be
        // stored after it has elapsed (the client always sends the complete settings object
        // on PUT - otherwise, e.g., a theme change would suddenly fail).
        if (dto.TargetGraduationDate.HasValue
            && dto.TargetGraduationDate.Value.Date < DateTime.Today
            && dto.TargetGraduationDate != existingTargetGraduationDate)
            return ServiceResult<UserSettingsDto>.Invalid("TargetGraduationDate must not be in the past.");

        var entity = await db.Settings.GetOrCreateAsync(db);

        // Optimistic concurrency (audit S4/S5): only enforced when the caller actually sends a
        // Version - see UserSettingsDto.Version for why null must mean "no precondition" rather
        // than "version 0". This check has a narrow, non-atomic window against
        // entity.Version++/SaveChangesAsync below within THIS request (not closed via an EF
        // IsConcurrencyToken/ExecuteUpdate WHERE clause) - deliberately accepted for this app's
        // actual threat model (a couple of personal devices racing across the up-to-30s
        // poll/cache window, not sub-millisecond concurrent writers); closing that residual
        // window is out of scope for this fix.
        if (dto.Version.HasValue && dto.Version.Value != entity.Version)
            return ServiceResult<UserSettingsDto>.Conflict(ToDto(entity));

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
        entity.TelemetryConsent = dto.TelemetryConsent;
        entity.ActiveStudyProgramId = dto.ActiveStudyProgramId;
        // LastBackupDownloadAt deliberately NOT set here (audit F1): it is documented on
        // UserSettingsEntity as "set directly in BackupDataService, not via the normal settings
        // PUT" - but until this fix, this full-row-replace endpoint quietly overwrote it from
        // whatever the client's DTO happened to carry anyway (typically its own last-fetched
        // copy, but a stale/offline client could just as easily send an old or null value and
        // silently revert the backup-reminder state, or forge it outright). Same rationale as
        // the ProgressShareEnabled/ProgressShareToken exclusion below - a field with its own
        // dedicated write path must not also be reachable through the generic PUT.
        // ProgressShareEnabled/ProgressShareToken deliberately NOT set here - they have their
        // own write path via the three methods below (same rationale as LastBackupDownloadAt:
        // "set directly, not via the normal settings PUT"). Reason: enabling must atomically
        // generate a cryptographically strong token if none exists yet - a client roundtrip via
        // the generic PUT could otherwise persist a "half-activated" state (Enabled=true,
        // Token=null).
        entity.Version++;
        await db.SaveChangesAsync();
        await settingsCacheVersion.BumpAsync(currentUser.AuthUserId);
        return ServiceResult<UserSettingsDto>.Success(ToDto(entity));
    }

    /// <summary>
    /// Activates the read-only progress link (ProgressController.GetShared) and always
    /// generates a new token while doing so - disabling now also deletes the token (see there),
    /// so there's never an "old" token left that could be reused.
    /// </summary>
    public async Task<UserSettingsDto> EnableProgressShareAsync()
    {
        var entity = await db.Settings.GetOrCreateAsync(db);
        entity.ProgressShareEnabled = true;
        entity.ProgressShareToken = GenerateShareToken();
        await db.SaveChangesAsync();
        await settingsCacheVersion.BumpAsync(currentUser.AuthUserId);
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
    public async Task<ServiceResult<UserSettingsDto>> DisableProgressShareAsync()
    {
        var entity = await db.Settings.FirstOrDefaultAsync();
        if (entity == null) return ServiceResult<UserSettingsDto>.NotFound();
        entity.ProgressShareEnabled = false;
        entity.ProgressShareToken = null;
        await db.SaveChangesAsync();
        await settingsCacheVersion.BumpAsync(currentUser.AuthUserId);
        return ServiceResult<UserSettingsDto>.Success(ToDto(entity));
    }

    /// <summary>
    /// Manual "regenerate now" (e.g. on suspicion of a leak, or to specifically invalidate a
    /// previously shared link) - immediately breaks any existing link, analogous to the calendar
    /// token (CalendarTokenService). Also enables the feature in the process (regenerating
    /// implies "I want a valid link again").
    /// </summary>
    public async Task<ServiceResult<UserSettingsDto>> RegenerateProgressShareTokenAsync()
    {
        var entity = await db.Settings.FirstOrDefaultAsync();
        if (entity == null) return ServiceResult<UserSettingsDto>.NotFound();
        entity.ProgressShareToken = GenerateShareToken();
        entity.ProgressShareEnabled = true;
        await db.SaveChangesAsync();
        await settingsCacheVersion.BumpAsync(currentUser.AuthUserId);
        return ServiceResult<UserSettingsDto>.Success(ToDto(entity));
    }

    /// <summary>
    /// Hides the built-in study program ("Applied Artificial Intelligence" - the developer's
    /// own real degree, hardcoded as a shared fallback so a new user never sees zero
    /// selectable programs, see StudyProgramService.LoadSummariesAsync) from this user's
    /// switcher for good. Requires at least one real (custom) study program to already exist -
    /// otherwise this account would be left with nothing to fall back to at all - mirrored by
    /// StudyProgramService.DeleteAsync refusing to remove a user's last remaining custom
    /// program once this flag is set. If the built-in program was the active one, switches to
    /// the user's oldest custom program so ActiveStudyProgramId is never left null once the
    /// fallback it means is gone.
    /// </summary>
    public async Task<ServiceResult<UserSettingsDto>> DismissBuiltInProgramAsync()
    {
        var oldestOwnProgramId = await db.StudyPrograms.AsNoTracking()
            .OrderBy(p => p.CreatedAt).Select(p => (int?)p.Id).FirstOrDefaultAsync();
        if (oldestOwnProgramId == null)
            return ServiceResult<UserSettingsDto>.Invalid("Create your own study program before hiding the default one.");

        var entity = await db.Settings.GetOrCreateAsync(db);
        entity.BuiltInProgramDismissed = true;
        entity.ActiveStudyProgramId ??= oldestOwnProgramId;
        await db.SaveChangesAsync();
        await settingsCacheVersion.BumpAsync(currentUser.AuthUserId);
        return ServiceResult<UserSettingsDto>.Success(ToDto(entity));
    }

    // Same CSPRNG technique as the calendar token (AuthSessionService.GenerateToken): 32 bytes,
    // base64url without padding - already URL-safe, because the token travels as part of the
    // client route path (/shared/{token}) and the API path (/api/progress/shared/{token}).
    private static string GenerateShareToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // internal instead of private: reused by BackupDataService (JSON export), so the export
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
        TelemetryConsent = e.TelemetryConsent,
        LastBackupDownloadAt = e.LastBackupDownloadAt,
        ActiveStudyProgramId = e.ActiveStudyProgramId,
        BuiltInProgramDismissed = e.BuiltInProgramDismissed,
        ProgressShareEnabled = e.ProgressShareEnabled,
        ProgressShareToken = e.ProgressShareToken,
    };
}
