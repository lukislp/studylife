namespace StudyLife.Shared;

public class StudySessionDto
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public string CourseColor { get; set; } = "#6C5CE7";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string? Topic { get; set; }
    public string? Notes { get; set; }
    public bool IsCompleted { get; set; }
    public int TimerModeId { get; set; }
    public string? RecurrenceGroupId { get; set; }
}

public class UserSettingsDto
{
    public List<int> SelectedCourseIds { get; set; } = new() { 1, 2, 3, 4 };
    public List<int> CompletedCourseIds { get; set; } = new();
    public string Theme { get; set; } = "dark";
    /// <summary>Preset key of a curated accent color (e.g. "coral", "blue"), not raw hex values.</summary>
    public string AccentColor { get; set; } = "coral";
    public bool AutoSwitchFocus { get; set; } = true;
    public int AutoSwitchMinutesBefore { get; set; } = 2;
    public string MotivationalStyle { get; set; } = "claude";
    /// <summary>Comma-separated minute values before session start, e.g. "60,30,10,5,3,2,1".</summary>
    public string SessionReminderMinutes { get; set; } = "60,30,10,5,3,2,1";
    /// <summary>Comma-separated day values before the course goal date, e.g. "14,7,3,1,0".</summary>
    public string CourseGoalReminderDays { get; set; } = "14,7,3,1,0";
    /// <summary>Number of days without a session after which the inactivity reminder fires.</summary>
    public int InactivityThresholdDays { get; set; } = 5;
    /// <summary>Earliest time (hour, 0-23) at which the exam planner/weekly plan assistant suggests sessions.</summary>
    public int StudyWindowStartHour { get; set; } = 8;
    /// <summary>Latest time (hour, 0-23, exclusive) up to which the exam planner/weekly plan assistant suggests sessions.</summary>
    public int StudyWindowEndHour { get; set; } = 21;
    /// <summary>Comma-separated weekdays (0=Sunday..6=Saturday, System.DayOfWeek values) on which planning is allowed.</summary>
    public string StudyDays { get; set; } = "0,1,2,3,4,5,6";
    /// <summary>Desired graduation date. Null = feature off (no target-hours display on the dashboard).</summary>
    public DateTime? TargetGraduationDate { get; set; }
    /// <summary>
    /// Custom timer modes as a JSON array, e.g.
    /// [{"id":100,"name":"Deep Work","focusMinutes":90,"breakMinutes":20,"rounds":1}].
    /// JSON instead of the usual comma lists, because mode names may contain commas.
    /// IDs start at 100 so they never collide with the built-in modes (1-5). "" = no custom modes.
    /// </summary>
    public string CustomTimerModes { get; set; } = "";
    /// <summary>Minimum desired study workload in hours/week. Replaces the previously hardcoded 25h.</summary>
    public int WeeklyGoalMinHours { get; set; } = 25;
    /// <summary>Maximum desired study workload in hours/week. Replaces the previously hardcoded 30h.</summary>
    public int WeeklyGoalMaxHours { get; set; } = 30;
    /// <summary>Minimum desired study workload in hours/month, independent of the weekly goal.</summary>
    public int MonthlyGoalMinHours { get; set; } = 100;
    /// <summary>Maximum desired study workload in hours/month, independent of the weekly goal.</summary>
    public int MonthlyGoalMaxHours { get; set; } = 130;
    /// <summary>Push reminders before session start (RunPushNotificationsAsync). Default true.</summary>
    public bool SessionRemindersEnabled { get; set; } = true;
    /// <summary>Push reminders before course goal deadlines (RunCourseGoalReminderCheckAsync). Default true.</summary>
    public bool CourseGoalRemindersEnabled { get; set; } = true;
    /// <summary>Inactivity nudges (RunInactivityReminderCheckAsync). Default true.</summary>
    public bool InactivityRemindersEnabled { get; set; } = true;
    /// <summary>Achievement unlock pushes (RunAchievementCheckAsync). Default true.</summary>
    public bool AchievementNotificationsEnabled { get; set; } = true;
    /// <summary>Weekly recap push (RunWeeklyReportAsync). Default true.</summary>
    public bool WeeklyReportEnabled { get; set; } = true;
    /// <summary>Daily motivation push (RunDailyMotivationAsync). Default false (opt-in, new category).</summary>
    public bool DailyMotivationEnabled { get; set; }
    /// <summary>Nudge for a single neglected course (RunPerCourseInactivityCheckAsync), while the user remains active overall. Default false (opt-in, new category).</summary>
    public bool PerCourseInactivityRemindersEnabled { get; set; }
    /// <summary>Timestamp of the last manual backup download (GET /api/backup/database). Null = never.</summary>
    public DateTime? LastBackupDownloadAt { get; set; }
    /// <summary>
    /// Id of the active custom study program (StudyProgramEntity). Null = the built-in
    /// study program (CourseCatalog.AppliedAICourses) is active - the default, so
    /// existing users see no behavior change.
    /// </summary>
    public int? ActiveStudyProgramId { get; set; }
    /// <summary>
    /// Read-only progress link active? Kept separate from the token itself (see ProgressShareToken),
    /// so that disabling it doesn't discard the once-generated token - re-enabling
    /// returns the same URL again. NOT set via the normal PUT, but
    /// exclusively via the dedicated POST /api/settings/progress-share/* endpoints
    /// (ProgressController rationale: the server generates the token, no client round-trip).
    /// </summary>
    public bool ProgressShareEnabled { get; set; }
    /// <summary>Permanent token for GET /api/progress/shared/{token}. Null = never activated.</summary>
    public string? ProgressShareToken { get; set; }
    /// <summary>Warns if the current study streak can still break today. Default false (opt-in, new category).</summary>
    public bool StreakRiskRemindersEnabled { get; set; }
    /// <summary>Gentle mid-week nudge on significant lag behind the weekly goal. Default false (opt-in, new category).</summary>
    public bool WeeklyGoalNudgeEnabled { get; set; }
    /// <summary>"Almost done" nudge for courses with ≥85% topic progress and no recent session. Default false (opt-in, new category).</summary>
    public bool CourseAlmostDoneRemindersEnabled { get; set; }
    /// <summary>Reminds shortly before the user's historically most productive time of day. Default false (opt-in, new category).</summary>
    public bool BestStudyTimeRemindersEnabled { get; set; }
    /// <summary>Gentle, short comeback nudge after exactly 1 day of pause. Default false (opt-in, new category).</summary>
    public bool ComebackNudgeEnabled { get; set; }
    /// <summary>Instant feedback on a new personal record (longest single session so far). Default false (opt-in, new category).</summary>
    public bool NewRecordNotificationsEnabled { get; set; }
    /// <summary>Monthly recap push (RunMonthlyReportAsync), analogous to WeeklyReportEnabled. Default true.</summary>
    public bool MonthlyReportEnabled { get; set; } = true;
}

/// <summary>
/// Response for GET /api/progress/shared/{token} (public, reachable without login/API key) -
/// deliberately its own compact DTO instead of exposing CourseDto/CourseGoalDto directly: only
/// a progress snapshot (ECTS, grade average, active courses), no notes/sessions/settings.
/// </summary>
public class ProgressShareDto
{
    public int TotalEcts { get; set; }
    public int EarnedEcts { get; set; }
    public decimal? AverageGrade { get; set; }
    public int CoursesCompletedCount { get; set; }
    public int CoursesTotalCount { get; set; }
    public List<ProgressShareCourseDto> ActiveCourses { get; set; } = new();
    public DateTime GeneratedAt { get; set; }
}

/// <summary>A single active course in the progress snapshot, with topic progress (0-100).</summary>
public class ProgressShareCourseDto
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "📚";
    public string Color { get; set; } = "#6C5CE7";
    public int Ects { get; set; }
    public int Semester { get; set; } = 1;
    public int TopicProgressPercent { get; set; }
}

public class NoteDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int? CourseId { get; set; }
    public int? SessionId { get; set; }
}

public class CourseGoalDto
{
    public int CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public DateTime? TargetDate { get; set; }
    public string? CompletionNote { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>Final grade, German grading system (1.0 = best, 5.0 = failed).</summary>
    public decimal? Grade { get; set; }
    /// <summary>Comma-separated list of checked-off topic names from CourseCatalog.Topics.</summary>
    public string CompletedTopics { get; set; } = "";
    public string? Tag { get; set; }
}

/// <summary>
/// Resource (link) of a course, e.g. lecture slides URL, course website, or book link.
/// CourseId uses the same shared integer ID space as CourseGoalDto.CourseId - works
/// identically for the built-in catalog and custom courses.
/// </summary>
public class CourseResourceDto
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Request for POST /api/planner/exam-plan - server-side variant of the exam planner logic
/// from Client/Pages/Planner.razor, so it can also be triggered without a browser
/// (e.g. from the Home Assistant service "generate_exam_plan"). Creates and saves the
/// suggested sessions directly, without an intermediate confirmation step.
/// </summary>
public class ExamPlanRequestDto
{
    public int CourseId { get; set; }
    public DateTime ExamDate { get; set; }
    /// <summary>Minutes per session. Null = server default (90).</summary>
    public int? SessionLengthMinutes { get; set; }
    /// <summary>Total planned study hours until the exam. Null = automatically estimated from open topics (1.5 h/topic).</summary>
    public double? TotalHours { get; set; }
}

/// <summary>
/// List entry for GET /api/studyprograms - populates the study program switcher in setup.
/// The built-in study program is delivered as a synthetic entry with Id == null
/// (it has no DB row); custom study programs carry their real DB Id.
/// </summary>
public class StudyProgramSummaryDto
{
    /// <summary>Null = the built-in study program (CourseCatalog.AppliedAICourses).</summary>
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    /// <summary>
    /// Manual completion flag (PUT /api/studyprograms/{id}/completed). Always false for the
    /// built-in study program (no DB row). Completed programs remain selectable in
    /// the switcher, just marked with a checkmark.
    /// </summary>
    public bool IsCompleted { get; set; }
}

/// <summary>
/// Request for PUT /api/studyprograms/{id}/completed - sets/removes the purely manual
/// completion flag of a custom study program. There is deliberately no
/// automation behind it (not even at 100% ECTS).
/// </summary>
public class SetStudyProgramCompletedDto
{
    public bool IsCompleted { get; set; }
}

/// <summary>
/// Response for GET /api/studyprograms/{id}: name + ECTS quotas per elective group
/// of the study program. The client needs the quotas for the program-aware
/// CourseCatalog.CalcTotalEcts/CalcEctsEarned overloads (Index/Stats).
/// </summary>
public class StudyProgramDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Group name → maximum creditable ECTS (same semantics as CourseCatalog.GroupEctsQuotas).</summary>
    public Dictionary<string, int> GroupEctsQuotas { get; set; } = new();
}

/// <summary>
/// Request for POST /api/studyprograms - creates a complete custom
/// study program in ONE call: name + elective groups + courses. Courses reference
/// their group via the group name (Group), null = mandatory module without a group.
/// </summary>
public class CreateStudyProgramRequestDto
{
    public string Name { get; set; } = "";
    public List<CreateStudyProgramGroupDto> Groups { get; set; } = new();
    public List<CreateStudyProgramCourseDto> Courses { get; set; } = new();
}

public class CreateStudyProgramGroupDto
{
    public string Name { get; set; } = "";
    /// <summary>Maximum creditable ECTS of this group, no matter how many courses are completed.</summary>
    public int EctsQuota { get; set; }
}

public class CreateStudyProgramCourseDto
{
    public int Semester { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Color { get; set; } = "#6C5CE7";
    public string Icon { get; set; } = "📚";
    public int Ects { get; set; } = 5;
    /// <summary>Name of the elective group from Groups. Null/empty = mandatory module.</summary>
    public string? Group { get; set; }
    public List<string> Topics { get; set; } = new();
}

/// <summary>
/// Live state of the focus timer (Client/Services/TimerService.cs), so external
/// consumers (e.g. Home Assistant) can see whether a focus session is
/// currently running - without duplicating the full timer logic. Only pushed on state
/// transitions (start/pause/phase change/completion), not every second.
/// </summary>
public class TimerStateDto
{
    public int? SessionId { get; set; }
    public bool IsRunning { get; set; }
    public bool IsBreak { get; set; }
    public int CurrentRound { get; set; }
    public int TimerModeId { get; set; }
    /// <summary>Absolute point in time at which the current focus/break phase ends. Null if not active.</summary>
    public DateTime? PhaseEndsAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Only set in the GET response: server time at the moment of the request. Consumers use it
    /// to compute the remaining time against the server clock instead of their local one - clock drift
    /// between devices thus doesn't show up in the display (remote timer banner on the focus page).</summary>
    public DateTime? ServerNow { get; set; }
}

/// <summary>PUT api/timerstate/liveactivity-token - body for the app-only reporting of an
/// ActivityKit push token (live activity push, paid profile). Null/empty = activity ended,
/// delete token (no further push delivery for this user).</summary>
public class LiveActivityPushTokenDto
{
    public string? Token { get; set; }
}

/// <summary>
/// A single VEVENT parsed from an uploaded .ics file (POST
/// /api/sessions/import-ics) - not yet a StudySessionEntity, just the suggestion for the
/// client-side review list (course assignment is missing, which the app can't guess).
/// </summary>
public class IcsImportEventDto
{
    public string Title { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string? Description { get; set; }
    /// <summary>
    /// true = the VEVENT contained an RRULE (recurring appointment). It is deliberately NOT
    /// expanded (out of scope for import) - StartTime/EndTime are
    /// just the first occurrence. The client shows a hint for this in the review list.
    /// </summary>
    public bool HasUnexpandedRecurrence { get; set; }
}

/// <summary>Response of POST /api/sessions/import-ics: all parsed VEVENTs for review.</summary>
public class IcsImportResultDto
{
    public List<IcsImportEventDto> Events { get; set; } = new();
}

/// <summary>
/// Reusable template for quickly created sessions, e.g. "Analysis lecture, 90 min,
/// Mondays 10:00" (GET/POST/DELETE /api/sessiontemplates). DefaultWeekday/DefaultStartTime are
/// just a display/suggestion aid on the client, not an enforced rule.
/// </summary>
public class SessionTemplateDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public string CourseColor { get; set; } = "#6C5CE7";
    public int DurationMinutes { get; set; } = 60;
    public string? Topic { get; set; }
    /// <summary>0=Sunday..6=Saturday (System.DayOfWeek values).</summary>
    public int? DefaultWeekday { get; set; }
    public TimeSpan? DefaultStartTime { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Response of GET /api/settings/ha-api-key: status of the long-lived per-user API key for
/// Home Assistant (phase 3) - deliberately ONLY existence + creation timestamp, never the plaintext
/// (like a password, that is only ever visible in the generate response).
/// </summary>
public class HaApiKeyStatusDto
{
    public bool HasKey { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>
/// Response of POST /api/settings/ha-api-key/generate: the ONLY moment where the
/// plaintext key exists - the server only stores the SHA-256 hash. The user must
/// copy the value now; retrieving it again is impossible, only a
/// regeneration (which immediately invalidates the old key).
/// </summary>
public class HaApiKeyGenerateResponseDto
{
    public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Response of GET /api/settings/ai-api-key: status of the long-lived per-user API key for the
/// studylife-ai integration - separate slot from the Home Assistant key (see
/// AuthUserEntity.AiApiKeyHash), same "existence + timestamp only, never the plaintext" shape as
/// HaApiKeyStatusDto.
/// </summary>
public class AiApiKeyStatusDto
{
    public bool HasKey { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>
/// Response of POST /api/settings/ai-api-key/generate - same one-time-plaintext shape as
/// HaApiKeyGenerateResponseDto, for the separate studylife-ai key slot.
/// </summary>
public class AiApiKeyGenerateResponseDto
{
    public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Response of GET /api/system/calendar-token (session-authenticated via the normal
/// /api gate): the permanent calendar token for the ICS subscription URL on the setup page - replaces
/// the former unauthenticated bootstrap-key endpoint, which additionally delivered the (by now
/// abolished) global API key.
/// </summary>
public class CalendarTokenResponseDto
{
    public string CalendarToken { get; set; } = "";
}

/// <summary>
/// Response of POST /api/system/regenerate-calendar-token (CalendarTokenProvider.Regenerate):
/// the new permanent calendar token, the old one is invalid from now on - every existing
/// calendar subscription must be re-subscribed with this new URL afterward.
/// </summary>
public class RegenerateCalendarTokenResponseDto
{
    public string CalendarToken { get; set; } = "";
}

/// <summary>Response of GET /api/system/version (unauthenticated) - the version number set at build
/// time via "-p:Version=$NEXT_VERSION" (see .gitlab-ci.yml).</summary>
public class VersionResponseDto
{
    public string Version { get; set; } = "";
}

/// <summary>Body of POST /api/auth/register/begin (phase 2, passkey login).</summary>
public class PasskeyRegisterBeginRequestDto
{
    public string DisplayName { get; set; } = "";
    /// <summary>Only checked for the very first registration ever (SetupSecretService) -
    /// every subsequent one deliberately stays open (family signup). Null/empty for all later
    /// registrations.</summary>
    public string? SetupSecret { get; set; }
}

/// <summary>
/// Response of POST /api/auth/register/begin, /register/begin-additional, and /login/begin:
/// the WebAuthn options as a JSON string serialized by Fido2NetLib (OptionsJson) plus a
/// server-side reference (OptionsId) to the cached challenge. OptionsJson is
/// deliberately transported as a string instead of a nested object, so the client side
/// can pass it unchanged to JSON.parse in the JS module - Fido2NetLib's Base64url
/// conventions are thus guaranteed to remain untouched by other serializer settings.
/// </summary>
public class PasskeyBeginResponseDto
{
    public string OptionsId { get; set; } = "";
    public string OptionsJson { get; set; } = "";
}

/// <summary>
/// Response of POST /api/auth/register/complete and /login/complete. Token is the ONE-TIME
/// plaintext session token (only its SHA-256 hash is stored server-side); null for the
/// "additional passkey for one's own account" path, which doesn't issue a new session -
/// instead Pending=true is set as long as an already logged-in device hasn't yet
/// approved the new passkey via the device list (see PasskeyCredentialEntity.ApprovedAt).
/// </summary>
public class PasskeyCompleteResponseDto
{
    public string? Token { get; set; }
    public string DisplayName { get; set; } = "";
    public bool Pending { get; set; }
}

/// <summary>POST /api/auth/handoff (native app shell, PKCE-style token handoff - see
/// AppReturnContext.BuildTokenReturnRedirectAsync): exchanges a real session token for a
/// short-lived, single-use code bound to a code_challenge, so the token itself never has to
/// travel through a custom-URL-scheme/loopback redirect that a different process could
/// potentially intercept.</summary>
public class AuthHandoffRequestDto
{
    public string Token { get; set; } = "";
    public string CodeChallenge { get; set; } = "";
}

/// <summary>Response of POST /api/auth/handoff.</summary>
public class AuthHandoffResponseDto
{
    public string Code { get; set; } = "";
}

/// <summary>POST /api/auth/exchange (native app shell): redeems a handoff code for the real
/// session token by proving possession of the code_verifier whose SHA-256 matches the
/// code_challenge given at handoff time - see AuthController.Exchange.</summary>
public class AuthExchangeRequestDto
{
    public string Code { get; set; } = "";
    public string CodeVerifier { get; set; } = "";
}

/// <summary>Response of POST /api/auth/exchange.</summary>
public class AuthExchangeResponseDto
{
    public string Token { get; set; } = "";
}

/// <summary>Response of POST /api/auth/recovery/generate - the plaintext codes only exist
/// in this one response, server-side only hashes are stored.</summary>
public class RecoveryCodesResponseDto
{
    public List<string> Codes { get; set; } = new();
}

/// <summary>GET /api/auth/recovery/status for the setup card.</summary>
public class RecoveryStatusDto
{
    public int TotalCount { get; set; }
    public int UnusedCount { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>POST /api/auth/recovery/login (emergency login with a one-time code).</summary>
public class RecoveryLoginRequestDto
{
    public string? Code { get; set; }
}

/// <summary>
/// Entry for GET /api/auth/credentials (passkey management in the speed-dial FAB). Deliberately
/// contains neither CredentialId nor PublicKey - unnecessarily sensitive for a plain device list.
/// Pending=true as long as an additional passkey hasn't yet been approved by an
/// already logged-in device - cannot yet be used to log in in this state.
/// </summary>
public class PasskeyListItemDto
{
    public int Id { get; set; }
    public string? DeviceLabel { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool Pending { get; set; }
}

/// <summary>Body of PUT /api/auth/credentials/{id}/label (DeviceLabel freely editable).</summary>
public class PasskeyRenameRequestDto
{
    public string Label { get; set; } = "";
}

/// <summary>
/// Response of GET /api/auth/demo - lets the login page discover whether this instance is
/// a public read-only demo (DEMO_MODE=true) and should auto-sign-in via
/// POST /api/auth/demo-login instead of showing the passkey UI. Always demo:false on a
/// normal deployment.
/// </summary>
public class DemoInfoDto
{
    public bool Demo { get; set; }
}

/// <summary>
/// Response of GET /api/auth/account-info - client info about one's own account that isn't
/// a setting (hence its own DTO instead of a field on UserSettingsDto, which would otherwise also
/// have to go through ComputeSettingsHash, even though IsOwner isn't user-configurable).
/// </summary>
public class AccountInfoDto
{
    /// <summary>True only for the first-ever registered user (owner of the installation) - only
    /// they may use the raw backup/restore endpoints (BackupController.IsOwnerAsync). The
    /// client hides the corresponding UI for all other users, instead of letting them
    /// run into an unusable 403.</summary>
    public bool IsOwner { get; set; }
}

/// <summary>
/// Response of POST /api/auth/link/begin (session-required, triggered from the already logged-in
/// device): a short-lived, one-time-display link code for a NEW, not yet
/// logged-in device - an alternative to the browser-dependent WebAuthn cross-device/hybrid transport
/// (QR code + Bluetooth), which isn't reliably available/discoverable on every browser/OS.
/// </summary>
public class DeviceLinkCodeResponseDto
{
    public string Code { get; set; } = "";
    public int ExpiresInSeconds { get; set; }
}

/// <summary>Body of POST /api/auth/register/begin-linked (public, NO session required -
/// the code itself assigns the new device to the account that generated it).</summary>
public class DeviceLinkRedeemRequestDto
{
    public string Code { get; set; } = "";
}

/// <summary>
/// Entry for GET /api/push/subscriptions (device management in the speed-dial FAB). Deliberately
/// does NOT contain Endpoint/P256dh/Auth - those are sensitive push credentials, unnecessary
/// for a plain device list. EndpointHash is a SHA256 hash of the endpoint; the client hashes its
/// own known subscription identically on the client side, to mark "this device" without
/// the real endpoint ever going out over the API.
/// </summary>
public class PushSubscriptionListItemDto
{
    public int Id { get; set; }
    /// <summary>Null for legacy records from before the AddPushSubscriptionDeviceInfo migration.</summary>
    public DateTime? CreatedAt { get; set; }
    public string? UserAgent { get; set; }
    public string EndpointHash { get; set; } = "";
}
