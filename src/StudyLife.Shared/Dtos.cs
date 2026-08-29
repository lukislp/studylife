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
    /// <summary>
    /// Optimistic-concurrency token (audit S4/S5), mirrors UserSettingsEntity.Version 1:1. GET
    /// ALWAYS returns the row's actual current value (never null). On PUT, this is nullable so
    /// "the field is simply absent from the request JSON" can be told apart from "the client
    /// explicitly sent version 0": null = no precondition, the server accepts the write
    /// unconditionally and keeps today's last-writer-wins behavior (compatibility for older
    /// clients, Home Assistant, and ad-hoc scripts against the API that don't know this field
    /// exists). Non-null = the write is rejected with 409 Conflict unless it still equals the
    /// row's current Version; on success the server increments it and returns the new value.
    /// The Blazor client always sends the Version it last fetched (see AppStateService).
    /// </summary>
    public int? Version { get; set; }
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
    public bool IsMarkdown { get; set; }
    public string? SourceUrl { get; set; }
    /// <summary>Comma-separated tags from capture enrichment (see NoteEntity.EnrichedAt) -
    /// server-assigned, read-only from the client's point of view (Create/Update never accept
    /// it, see NotesController).</summary>
    public string? Tags { get; set; }
    /// <summary>One-sentence AI-generated summary from capture enrichment - same read-only
    /// contract as Tags.</summary>
    public string? Summary { get; set; }
    /// <summary>Ids of existing notes studylife-ai found similar to this capture - same
    /// read-only contract as Tags/Summary. Empty (not null) when there are none, matching
    /// UserSettingsDto.SelectedCourseIds's List&lt;int&gt; convention rather than NoteEntity's
    /// raw comma-separated string.</summary>
    public List<int> RelatedNoteIds { get; set; } = new();
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
    /// <summary>
    /// Optional client-assigned send order (audit S6): TimerService fires a PUT on every state
    /// transition without awaiting the previous one, so two rapid transitions (e.g. Start
    /// immediately followed by a phase change) can arrive at the server out of order and would
    /// otherwise leave a stale state visible until the next transition. A monotonically
    /// increasing value (the client uses unix milliseconds) lets TimerStateController.Save
    /// silently drop an out-of-order PUT instead of applying it. Null = no ordering info
    /// (older clients, or a non-sequence-aware pusher like Home Assistant) - the server then
    /// falls back to plain last-write-wins, exactly as before this field existed.
    /// </summary>
    public long? ClientSequence { get; set; }
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
/// Response of GET /api/settings/mcp-api-key: status of the long-lived per-user API key for the
/// studylife-mcp integration - separate slot from the Home Assistant and studylife-ai keys (see
/// AuthUserEntity.McpApiKeyHash), same "existence + timestamp only, never the plaintext" shape as
/// HaApiKeyStatusDto.
/// </summary>
public class McpApiKeyStatusDto
{
    public bool HasKey { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>
/// Response of POST /api/settings/mcp-api-key/generate - same one-time-plaintext shape as
/// HaApiKeyGenerateResponseDto, for the separate studylife-mcp key slot.
/// </summary>
public class McpApiKeyGenerateResponseDto
{
    public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Status of the per-user API key (existence + creation date, never the plaintext) for the
/// studylife-capture browser extension - separate slot from the other three keys (see
/// AuthUserEntity.CaptureApiKeyHash), same "existence + timestamp only" shape as HaApiKeyStatusDto.
/// </summary>
public class CaptureApiKeyStatusDto
{
    public bool HasKey { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>
/// Response of POST /api/settings/capture-api-key/generate - same one-time-plaintext shape as
/// HaApiKeyGenerateResponseDto, for the separate studylife-capture key slot.
/// </summary>
public class CaptureApiKeyGenerateResponseDto
{
    public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Status of the per-user API key (existence + creation date, never the plaintext) for the
/// studylife-focusguard browser extension - separate slot from the other four keys (see
/// AuthUserEntity.FocusGuardApiKeyHash), same "existence + timestamp only" shape as
/// CaptureApiKeyStatusDto. No generate DTO for this slot (unlike ha/ai) - like capture and mcp,
/// provisioning happens exclusively through the consent flow (FocusGuardConnect below).
/// </summary>
public class FocusGuardApiKeyStatusDto
{
    public bool HasKey { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>
/// Response of POST /api/settings/focusguard-api-key/generate - same one-time-plaintext shape as
/// CaptureApiKeyGenerateResponseDto, for the separate studylife-focusguard key slot. Kept
/// alongside the consent flow (not the UI's actual path, see SetupExternalConnectionsCard) for
/// the same uniform admin/test surface every other slot has.
/// </summary>
public class FocusGuardApiKeyGenerateResponseDto
{
    public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>Status DTO for the studylife-focustunes key slot - same shape as
/// FocusGuardApiKeyStatusDto, see AuthUserEntity.FocusTunesApiKeyHash.</summary>
public class FocusTunesApiKeyStatusDto
{
    public bool HasKey { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>Generate-response DTO for the studylife-focustunes key slot - same shape as
/// FocusGuardApiKeyGenerateResponseDto.</summary>
public class FocusTunesApiKeyGenerateResponseDto
{
    public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>Status DTO for the studylife-tray key slot - same shape as
/// FocusGuardApiKeyStatusDto, see AuthUserEntity.TrayApiKeyHash.</summary>
public class TrayApiKeyStatusDto
{
    public bool HasKey { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>Generate-response DTO for the studylife-tray key slot - same shape as
/// FocusGuardApiKeyGenerateResponseDto.</summary>
public class TrayApiKeyGenerateResponseDto
{
    public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>One entry in the list of a user's named webhooks API keys (see
/// WebhookApiKeyEntity) - never carries the plaintext or hash, only the display metadata a
/// "manage your keys" UI needs.</summary>
public class WebhookApiKeyDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>Request to create a new named webhooks API key.</summary>
public class CreateWebhookApiKeyRequestDto
{
    public string Name { get; set; } = "";
}

/// <summary>Response to creating a new named webhooks API key - the only place the plaintext is
/// ever returned, same "shown once" pattern as every other *ApiKeyGenerateResponseDto.</summary>
public class CreateWebhookApiKeyResponseDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Request/response shapes for POST /api/ai/chat, /api/ai/agent, /api/ai/agent/confirm - the
/// AiProxyController passes bodies through byte-for-byte, so these must match studylife-ai's own
/// pydantic schemas exactly (field names, including its snake_case ones - see
/// src/studylife_ai/schemas/chat.py and agent.py in the studylife-ai repo). Multi-word fields
/// need an explicit JsonPropertyName: no built-in naming policy converts PascalCase to
/// snake_case, only casing (PascalCase/camelCase), so "ThreadId" would never auto-match
/// "thread_id" without one.
/// </summary>
public class AiChatMessageDto
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public class AiChatRequestDto
{
    public List<AiChatMessageDto> Messages { get; set; } = new();
}

/// <summary>One parsed SSE `data: {...}` line from /api/ai/chat - exactly one of
/// Delta/Sources/Error is set per event, mirroring the three event shapes documented in
/// studylife-ai's api/chat.py.</summary>
public class AiChatStreamEventDto
{
    public string? Delta { get; set; }
    public List<AiChatSourceDto>? Sources { get; set; }
    public string? Error { get; set; }
}

public class AiChatSourceDto
{
    [System.Text.Json.Serialization.JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("entity_id")]
    public int EntityId { get; set; }
    public string Title { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("course_id")]
    public int? CourseId { get; set; }
}

public class AiAgentRequestDto
{
    public string Message { get; set; } = "";
}

public class AiPendingActionDto
{
    public string Tool { get; set; } = "";
    public Dictionary<string, object> Args { get; set; } = new();
    public string Description { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("thread_id")]
    public string ThreadId { get; set; } = "";
}

public class AiAgentResponseDto
{
    public string? Answer { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("pending_actions")]
    public List<AiPendingActionDto> PendingActions { get; set; } = new();
}

public class AiConfirmRequestDto
{
    [System.Text.Json.Serialization.JsonPropertyName("thread_id")]
    public string ThreadId { get; set; } = "";
    public string Decision { get; set; } = "";
    public string? Message { get; set; }
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
    /// <summary>
    /// Registration invite token (audit finding A10, Registration:Mode=invite) - optional, so
    /// existing clients/callers that never send it keep working unchanged in "open" mode (and
    /// during bootstrap, where the gate doesn't apply at all, see RegistrationGateService). Read
    /// by the client from the "/register?invite=&lt;token&gt;" link's query string. Validated at
    /// register/begin (RegistrationGateService.CheckBeginAsync) but only CONSUMED at
    /// register/complete, so a begin alone never burns it.
    /// </summary>
    public string? InviteToken { get; set; }
}

/// <summary>
/// 403 payload for a gated register/begin or register/complete call (audit finding A10) -
/// Reason is one of three stable strings the client switches on to show the right localized
/// message instead of a generic failure: "closed" (Registration:Mode=closed), "invite_required"
/// (Registration:Mode=invite, no token given), "invite_invalid" (token unknown/expired/already
/// used). Never returned during bootstrap (RegistrationGateService is skipped entirely then).
/// </summary>
public class RegistrationGateErrorDto
{
    public string Reason { get; set; } = "";
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

/// <summary>
/// Response of GET /api/auth/whoami - lets a satellite (studylife-mcp, studylife-ai, ...)
/// resolve the REAL AuthUserId behind whatever credential it was just given, instead of
/// inventing its own identity from a hash of the credential (identity contract v1, audit A1).
/// Credential names which of the four API-key slots matched, or "session" for a passkey
/// session token - see AuthController.Whoami.
/// </summary>
public class WhoamiResponseDto
{
    public int UserId { get; set; }
    public string Credential { get; set; } = "";
}

/// <summary>Body of POST /api/auth/mcp-connect (identity contract v1 §2, session-required):
/// the browser page /connect/mcp forwards the redirect_uri/state it received from studylife-mcp's
/// OAuth authorize redirect unchanged.</summary>
public class McpConnectRequestDto
{
    public string RedirectUri { get; set; } = "";
    public string State { get; set; } = "";
}

/// <summary>Response of POST /api/auth/mcp-connect: the URL the Blazor page navigates to next,
/// carrying the single-use assertion back to studylife-mcp's own callback.</summary>
public class McpConnectResponseDto
{
    public string RedirectTo { get; set; } = "";
}

/// <summary>Body of POST /api/auth/mcp-assertion-exchange (identity contract v1 §2 step 4,
/// exempt from the API gate - the assertion itself is the credential).</summary>
public class McpAssertionExchangeRequestDto
{
    public string Assertion { get; set; } = "";
}

/// <summary>Response of POST /api/auth/mcp-assertion-exchange: the real AuthUserId and the
/// plaintext MCP key studylife-mcp needs to build its own StudyLife client for this user -
/// this is the only place the plaintext leaves the server for this flow.</summary>
public class McpAssertionExchangeResponseDto
{
    public int UserId { get; set; }
    public string McpApiKey { get; set; } = "";
}

/// <summary>Body of POST /api/auth/capture-connect (identity contract v1 §2, generalized to a
/// second audience/slot alongside mcp-connect, session-required): the browser page
/// /connect/capture forwards the redirect_uri/state it received from the studylife-capture
/// extension's chrome.identity launchWebAuthFlow (a normal https://&lt;id&gt;.chromiumapp.org/
/// redirect_uri, no special-casing needed) unchanged. Same shape as McpConnectRequestDto,
/// deliberately its own type rather than a shared one - see McpConnectRequestDto/
/// CaptureConnectResponseDto and the "near-identical trio, on purpose" convention this repo
/// already uses for the per-slot API-key endpoints in SettingsController.</summary>
public class CaptureConnectRequestDto
{
    public string RedirectUri { get; set; } = "";
    public string State { get; set; } = "";
}

/// <summary>Response of POST /api/auth/capture-connect: the URL the Blazor page navigates to
/// next, carrying the single-use, capture-audience-bound assertion back to the extension's own
/// chrome.identity callback.</summary>
public class CaptureConnectResponseDto
{
    public string RedirectTo { get; set; } = "";
}

/// <summary>Body of POST /api/auth/capture-assertion-exchange (identity contract v1 §2 step 4,
/// generalized to the capture audience - exempt from the API gate, the assertion itself is the
/// credential). An mcp-connect assertion presented here is rejected (audience mismatch, see
/// AuthController.RedeemConsentAssertionAsync) - the two audiences' assertions are never
/// interchangeable even though they share the same cache/expiry/single-use machinery.</summary>
public class CaptureAssertionExchangeRequestDto
{
    public string Assertion { get; set; } = "";
}

/// <summary>Response of POST /api/auth/capture-assertion-exchange: the real AuthUserId and the
/// plaintext studylife-capture key the extension needs to authenticate its own requests - this
/// is the only place the plaintext leaves the server for this flow.</summary>
public class CaptureAssertionExchangeResponseDto
{
    public int UserId { get; set; }
    public string CaptureApiKey { get; set; } = "";
}

/// <summary>Body of POST /api/auth/focusguard-connect - same shape and role as
/// CaptureConnectRequestDto, third audience/slot in the consent flow (identity contract v1 §2).
/// The extension's own chrome.identity.launchWebAuthFlow supplies redirect_uri/state exactly like
/// the capture flow does.</summary>
public class FocusGuardConnectRequestDto
{
    public string RedirectUri { get; set; } = "";
    public string State { get; set; } = "";
}

/// <summary>Response of POST /api/auth/focusguard-connect: the URL the Blazor page navigates to
/// next, carrying the single-use, focusguard-audience-bound assertion back to the extension's own
/// chrome.identity callback.</summary>
public class FocusGuardConnectResponseDto
{
    public string RedirectTo { get; set; } = "";
}

/// <summary>Body of POST /api/auth/focusguard-assertion-exchange (identity contract v1 §2 step 4,
/// third audience - exempt from the API gate, the assertion itself is the credential). A
/// capture- or mcp-connect assertion presented here is rejected (audience mismatch, see
/// AuthController.RedeemConsentAssertionAsync).</summary>
public class FocusGuardAssertionExchangeRequestDto
{
    public string Assertion { get; set; } = "";
}

/// <summary>Response of POST /api/auth/focusguard-assertion-exchange: the real AuthUserId and the
/// plaintext studylife-focusguard key the extension needs to authenticate its own requests - this
/// is the only place the plaintext leaves the server for this flow.</summary>
public class FocusGuardAssertionExchangeResponseDto
{
    public int UserId { get; set; }
    public string FocusGuardApiKey { get; set; } = "";
}

/// <summary>Body of POST /api/auth/focustunes-connect - same shape and role as
/// FocusGuardConnectRequestDto, a fourth audience/slot in the consent flow.</summary>
public class FocusTunesConnectRequestDto
{
    public string RedirectUri { get; set; } = "";
    public string State { get; set; } = "";
}

/// <summary>Response of POST /api/auth/focustunes-connect.</summary>
public class FocusTunesConnectResponseDto
{
    public string RedirectTo { get; set; } = "";
}

/// <summary>Body of POST /api/auth/focustunes-assertion-exchange (step 4, fourth audience).</summary>
public class FocusTunesAssertionExchangeRequestDto
{
    public string Assertion { get; set; } = "";
}

/// <summary>Response of POST /api/auth/focustunes-assertion-exchange.</summary>
public class FocusTunesAssertionExchangeResponseDto
{
    public int UserId { get; set; }
    public string FocusTunesApiKey { get; set; } = "";
}

/// <summary>Body of POST /api/auth/tray-connect - same shape and role as
/// FocusTunesConnectRequestDto, a fifth audience/slot in the consent flow. The studylife-tray
/// desktop app supplies an RFC 8252 loopback redirect_uri here instead of a chrome.identity
/// callback, since it isn't a browser extension.</summary>
public class TrayConnectRequestDto
{
    public string RedirectUri { get; set; } = "";
    public string State { get; set; } = "";
}

/// <summary>Response of POST /api/auth/tray-connect.</summary>
public class TrayConnectResponseDto
{
    public string RedirectTo { get; set; } = "";
}

/// <summary>Body of POST /api/auth/tray-assertion-exchange (step 4, fifth audience).</summary>
public class TrayAssertionExchangeRequestDto
{
    public string Assertion { get; set; } = "";
}

/// <summary>Response of POST /api/auth/tray-assertion-exchange.</summary>
public class TrayAssertionExchangeResponseDto
{
    public int UserId { get; set; }
    public string TrayApiKey { get; set; } = "";
}

/// <summary>
/// GET /api/auth/oauth-clients/{clientId} - fetched by the frontend BEFORE rendering the consent
/// screen for a dynamically registered client (see OAuthClientEntity). Unlike the 5 hardcoded
/// audiences above (whose consent copy is hardcoded client-side per audience), a dynamic client's
/// name/description/requested scopes have to come from somewhere - this is that lookup.
/// </summary>
public class OAuthClientInfoDto
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> RequestedScopes { get; set; } = new();
}

/// <summary>Body of POST /api/auth/connect - the generic, ClientId-parameterized counterpart to
/// McpConnect/CaptureConnect/etc. Approve action for a dynamically registered client.</summary>
public class GenericConnectRequestDto
{
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string State { get; set; } = "";
}

/// <summary>Response of POST /api/auth/connect.</summary>
public class GenericConnectResponseDto
{
    public string RedirectTo { get; set; } = "";
}

/// <summary>Body of POST /api/auth/assertion-exchange - unlike the 5 hardcoded per-audience
/// exchange endpoints, this one is shared across every dynamic client, so ClientId has to be
/// supplied explicitly (it's what used to be implicit in the endpoint's own URL).</summary>
public class GenericAssertionExchangeRequestDto
{
    public string ClientId { get; set; } = "";
    public string Assertion { get; set; } = "";
}

/// <summary>Response of POST /api/auth/assertion-exchange.</summary>
public class GenericAssertionExchangeResponseDto
{
    public int UserId { get; set; }
    public string ApiKey { get; set; } = "";
}

/// <summary>One of a developer's own registered OAuthClientEntity rows (DeveloperController's
/// list/detail shape) - never carries a secret, there is none: unlike an API key, a client
/// registration itself has nothing to hide from its own owner.</summary>
public class DeveloperClientDto
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> AllowedRedirectUris { get; set; } = new();
    public List<string> RequestedScopes { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

/// <summary>Body of POST /api/developer/clients - registers a new OAuthClientEntity. Every
/// requested scope must be a member of ApiKeyScopes.PubliclyGrantable, enforced server-side.</summary>
public class CreateDeveloperClientRequestDto
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> AllowedRedirectUris { get; set; } = new();
    public List<string> RequestedScopes { get; set; } = new();
}

/// <summary>Body of PUT /api/developer/clients/{clientId} - Name/Description/AllowedRedirectUris/
/// RequestedScopes are all editable after the fact (the "scope erweiterbar" requirement) -
/// ClientId itself never changes once registered.</summary>
public class UpdateDeveloperClientRequestDto
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> AllowedRedirectUris { get; set; } = new();
    public List<string> RequestedScopes { get; set; } = new();
}

/// <summary>Status DTO for the studylife-developers portal key slot - same shape as
/// AiApiKeyStatusDto, see AuthUserEntity.DeveloperApiKeyHash.</summary>
public class DeveloperApiKeyStatusDto
{
    public bool HasKey { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>Generate-response DTO for the studylife-developers portal key slot - same shape as
/// AiApiKeyGenerateResponseDto. The frontend discards ApiKey (toggle, not reveal - see
/// SetupDeveloperCard).</summary>
public class DeveloperApiKeyGenerateResponseDto
{
    public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
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
    /// <summary>
    /// The caller's own AuthUserId (audit S7): lets AppStateService learn which account is
    /// currently active WITHOUT a dedicated round trip - this endpoint is already fetched via
    /// GetIsOwnerAsync, so the client's offline-read-cache namespacing (S7: cache keys are
    /// per-account, to stop a shared-browser user B from cold-starting offline into user A's
    /// leftover data) piggybacks on the same request instead of also calling
    /// GET /api/auth/whoami (that endpoint accepts API keys too, which this client never uses -
    /// account-info's SessionOnly policy already matches exactly what the client authenticates
    /// with).
    /// </summary>
    public int UserId { get; set; }
}

/// <summary>
/// Response of POST /api/auth/invites (owner-only, audit finding A10): the PLAINTEXT invite
/// token, shown exactly once - only its SHA-256 hash is stored server-side (AuthInviteEntity),
/// same pattern as HaApiKeyGenerateResponseDto/RecoveryCodesResponseDto. The client builds the
/// shareable link itself as "{origin}/register?invite={Token}".
/// </summary>
public class CreateInviteResponseDto
{
    public int Id { get; set; }
    public string Token { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Entry of GET /api/auth/invites (owner-only, audit finding A10) - deliberately never the token
/// itself (like PasskeyListItemDto never carries CredentialId/PublicKey). UsedAt/UsedByUserId let
/// the client show "used" vs. "expired" vs. still-active without a separate status enum; expiry
/// is derived client-side from ExpiresAt against the current time (no server round trip needed to
/// keep the list's expired/active split up to date while it's open).
/// </summary>
public class InviteListItemDto
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
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

// Audit finding D2: the five small response DTOs below replace bare `Ok(new { ... })` anonymous
// objects in BackupController/DictationController/PushController/SystemController - those
// compiled fine, but a plain `IActionResult` return type gives the OpenAPI generator nothing to
// introspect (no component schema, no documented response body at all; every other controller
// action already returns ActionResult<T> and gets a real schema "for free"). Property names/
// casing are unchanged from the anonymous objects they replace - this is a pure typing change,
// byte-identical on the wire (verified against StudyLife.Client's own hand-mirrored records for
// these same endpoints, e.g. SetupRestoreCard.razor's RestoreStatusResponse and
// NotificationService.cs's VapidPublicKeyResponse - exactly the kind of drift-prone duplication
// this whole audit finding exists to eliminate).

/// <summary>Response of GET /api/backup/restore/status.</summary>
public class RestoreStatusResponseDto
{
    public bool Pending { get; set; }
    public DateTime? StagedAt { get; set; }
}

/// <summary>Response of a successful POST /api/backup/restore/cancel (a staged restore existed
/// and was discarded). The "nothing to cancel" case answers 404 instead, with a plain
/// <c>{ error }</c> body - not worth its own schema for a single string field.</summary>
public class RestoreCancelResponseDto
{
    public string Status { get; set; } = "cancelled";
}

/// <summary>Response of POST /api/dictate.</summary>
public class DictationResponseDto
{
    public string Text { get; set; } = "";
}

/// <summary>Response of GET /api/push/publickey.</summary>
public class PushPublicKeyResponseDto
{
    public string PublicKey { get; set; } = "";
}

/// <summary>Response of GET /api/system/capabilities.</summary>
public class SystemCapabilitiesResponseDto
{
    public bool RawBackupSupported { get; set; }
}

// Audit finding M4: JSON export/import ("v2") - BackupController.Export/ImportJson. Unlike the
// original v1 export (5 of 9 user-owned tables, nested DTOs serialized PascalCase because that
// code path bypassed the app's shared JsonSerializerOptions), this covers every user-owned
// table and is serialized with the exact same options as the rest of the API. StudyProgramDto/
// CourseGroupDto/CustomCourseDto don't exist as API-facing DTOs with raw ids (StudyProgramSummaryDto/
// StudyProgramDetailDto are deliberately id-light for the client), so the three export-only DTOs
// below carry the real database ids - required to remap cross-references (CourseGroup.StudyProgramId,
// CustomCourse.StudyProgramId/CourseGroupId, and everywhere a shifted CustomCourseIdOffset id is
// used) onto freshly assigned ids on import.

/// <summary>Export/import shape of a custom study program row. Id is the real database id,
/// re-assigned on import - see BackupController.ImportJson.</summary>
public class StudyProgramExportDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsCompleted { get; set; }
}

/// <summary>Export/import shape of an elective group row. StudyProgramId points at a
/// StudyProgramExportDto.Id in the SAME export file - remapped to the newly assigned study
/// program id on import.</summary>
public class CourseGroupExportDto
{
    public int Id { get; set; }
    public int StudyProgramId { get; set; }
    public string Name { get; set; } = "";
    public int EctsQuota { get; set; }
}

/// <summary>Export/import shape of a custom course row. StudyProgramId/CourseGroupId point at
/// ids in the same export file (the latter nullable - "no elective group"), both remapped on
/// import; Id itself is the RAW database id (not the externally shifted CourseDto.Id =
/// StudyProgramCatalog.CustomCourseIdOffset + Id) - the shift is re-applied wherever a shifted
/// id is referenced (sessions/goals/resources/templates CourseId, settings' comma-separated id
/// lists) using the NEW id after import.</summary>
public class CustomCourseExportDto
{
    public int Id { get; set; }
    public int StudyProgramId { get; set; }
    public int Semester { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Color { get; set; } = "#6C5CE7";
    public string Icon { get; set; } = "📚";
    public int Ects { get; set; } = 5;
    public int? CourseGroupId { get; set; }
    /// <summary>Comma-separated, same raw format as CustomCourseEntity.Topics (unlike
    /// CourseDto.Topics, which is already split into a List&lt;string&gt; for API clients).</summary>
    public string Topics { get; set; } = "";
}

/// <summary>
/// GET /api/backup/export response envelope ("v2", audit finding M4) and POST
/// /api/backup/import-json request body. FormatVersion distinguishes this from a v1 file (a bare
/// object without this property, which deserializes FormatVersion as 0 thanks to case-insensitive/
/// tolerant JSON binding) - BackupController.ImportJson accepts both: a v1 file simply leaves the
/// newer collections (StudyPrograms/CourseGroups/CustomCourses/SessionTemplates) at their default
/// empty list, so those tables just import nothing instead of failing the whole request.
/// Deliberately excluded from every table below: PushSubscriptions (this browser's endpoint
/// registrations, not transferable), SentReminders (internal dedup bookkeeping), TimerState
/// (transient live state), and everything under AuthUserEntity/PasskeyCredentialEntity/
/// AuthSessionEntity/RecoveryCodeEntity/SystemSecretsEntity/AiKeyOutboxEntity (auth/session/
/// infrastructure rows, not user data - importing these would either be meaningless across
/// accounts or a security hole, e.g. re-planting a session token hash).
/// </summary>
public class BackupExportDto
{
    // Deliberately NO "= 2" default here: [FromBody] model binding constructs this via the
    // parameterless constructor FIRST (running property initializers) and only THEN overwrites
    // whatever properties are actually present in the JSON - a default of 2 would make a legacy
    // v1 file (no formatVersion property at all) silently look like v2. The plain int default
    // (0) is exactly the "no formatVersion property present" signal BackupController.ImportJson
    // checks for. Export() sets this explicitly to 2 when building a fresh envelope.
    public int FormatVersion { get; set; }
    public DateTime ExportedAt { get; set; }
    /// <summary>Server version at export time (same source as VersionResponseDto) - purely
    /// informational, never checked on import.</summary>
    public string AppVersion { get; set; } = "";
    public List<StudySessionDto> Sessions { get; set; } = new();
    public List<NoteDto> Notes { get; set; } = new();
    public List<CourseGoalDto> CourseGoals { get; set; } = new();
    public List<CourseResourceDto> CourseResources { get; set; } = new();
    public UserSettingsDto Settings { get; set; } = new();
    public List<StudyProgramExportDto> StudyPrograms { get; set; } = new();
    public List<CourseGroupExportDto> CourseGroups { get; set; } = new();
    public List<CustomCourseExportDto> CustomCourses { get; set; } = new();
    public List<SessionTemplateDto> SessionTemplates { get; set; } = new();
}

/// <summary>
/// Response of POST /api/backup/import-json: per-table counts of rows actually inserted, and of
/// rows/references dropped because they pointed at something not found in the file (a dangling
/// custom-course/study-program/group/note/session reference - tolerant like CommaSeparatedIds,
/// never a hard failure of the whole import). Both dictionaries are keyed by a short, stable
/// name per table/reference kind - see BackupController.ImportJson for the exact key list.
/// </summary>
public class BackupImportResponseDto
{
    public Dictionary<string, int> Imported { get; set; } = new();
    public Dictionary<string, int> Dropped { get; set; } = new();
}

// Metrics API (docs/api/metrics-contract-v1, owner decision: every metric computed in exactly
// ONE place, served by the server - see MetricsController and docs/ARCHITECTURE.md "Metrics
// API"). Field names/casing/nullability are the FIXED wire contract shared with studylife-hacs
// (the Home Assistant integration consumes GET /api/metrics/summary and /api/metrics/achievements
// directly, see ApiKeyScopes.Ha) - do not rename without coordinating a contract version bump.

/// <summary>Response of GET /api/metrics/summary?program=&amp;now= - every field is always
/// present unless explicitly documented as nullable below.</summary>
public class MetricsSummaryDto
{
    /// <summary>The `now` the response was computed against (echoes the query param, or the
    /// server's local now if omitted) - naive local, no offset, same convention as every other
    /// DateTime in this API.</summary>
    public DateTime AsOf { get; set; }
    public MetricsProgramDto Program { get; set; } = new();
    public MetricsStreakDto Streak { get; set; } = new();
    public MetricsHoursDto Hours { get; set; } = new();
    public MetricsQuotaDto WeekQuota { get; set; } = new();
    public MetricsQuotaDto MonthQuota { get; set; } = new();
    public MetricsEctsDto Ects { get; set; } = new();
    /// <summary>ECTS-weighted average grade (StudyMetrics.CalcWeightedAverageGrade). Null when
    /// no course has a grade recorded yet.</summary>
    public decimal? AverageGrade { get; set; }
    public MetricsForecastDto Forecast { get; set; } = new();
    public MetricsMonthComparisonDto MonthComparison { get; set; } = new();
    /// <summary>Null when the "≥2 active courses" gate isn't met (StudyMetrics.CalcNeglectedCourse) -
    /// see that function for the full rule set.</summary>
    public MetricsNeglectedCourseDto? NeglectedCourse { get; set; }
    /// <summary>The last COMPLETED Mon-Sun week (StudyMetrics.CalcLastCompletedWeekReport) -
    /// always available, unlike the app's own current-week push recap.</summary>
    public MetricsWeeklyReportDto WeeklyReport { get; set; } = new();
    /// <summary>Selected courses with ≥1 studied session, sorted by hours descending.</summary>
    public List<MetricsCourseHoursDto> CourseHours { get; set; } = new();
    public MetricsTopicsDto Topics { get; set; } = new();
    /// <summary>Open goals with a target date, soonest first, max 5.</summary>
    public List<MetricsUpcomingGoalDto> UpcomingCourseGoals { get; set; } = new();
}

/// <summary>The resolved study programme (see MetricsController's `program` query param
/// resolution, same convention as GET /api/courses). Id null = built-in.</summary>
public class MetricsProgramDto
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsBuiltIn { get; set; }
}

public class MetricsStreakDto
{
    public int Current { get; set; }
    public int Longest { get; set; }
}

/// <summary>Total/TotalSessions are all-time studied hours/session count (same aggregation as
/// AchievementCatalog.BuildInputs' TotalHours/TotalSessions).</summary>
public class MetricsHoursDto
{
    public double Week { get; set; }
    public double Month { get; set; }
    public double Total { get; set; }
    public int TotalSessions { get; set; }
}

/// <summary>Shape shared by WeekQuota and MonthQuota (StudyMetrics.CalcQuota).</summary>
public class MetricsQuotaDto
{
    public double Hours { get; set; }
    public int TargetMin { get; set; }
    public int TargetMax { get; set; }
    public double Percent { get; set; }
    public double MinPercent { get; set; }
    public bool Warning { get; set; }
    public double MissingHours { get; set; }
}

public class MetricsEctsDto
{
    public int Earned { get; set; }
    public int Total { get; set; }
}

/// <summary>StudyMetrics.CalcForecast, trimmed to the fields the wire contract needs (no
/// BaselineWeeksNeeded/ReferenceWeeklyHours - those only feed the client's own graduation-goal
/// inverse calculation, which stays client-side).</summary>
public class MetricsForecastDto
{
    public bool Available { get; set; }
    public bool AlreadyDone { get; set; }
    /// <summary>Null when Available is false.</summary>
    public DateTime? Date { get; set; }
    public double RecentWeeklyHours { get; set; }
}

/// <summary>StudyMetrics.CalcMonthComparison. SameMonthLastYearHours/DeltaVsLastYear are null
/// exactly when HasYearData is false.</summary>
public class MetricsMonthComparisonDto
{
    public double CurrentMonthHours { get; set; }
    public double PreviousMonthHours { get; set; }
    public double DeltaVsPreviousMonth { get; set; }
    public bool HasYearData { get; set; }
    public double? SameMonthLastYearHours { get; set; }
    public double? DeltaVsLastYear { get; set; }
}

/// <summary>StudyMetrics.CalcNeglectedCourse. LastStudied/DaysSince are null when the course was
/// never studied within the lookback window (StudyMetrics.NeglectedCourseHistoryDays).</summary>
public class MetricsNeglectedCourseDto
{
    public int CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public DateTime? LastStudied { get; set; }
    public int? DaysSince { get; set; }
}

/// <summary>StudyMetrics.CalcLastCompletedWeekReport. WeekId is an ISO week string
/// ("{year}-W{week:D2}"). TopCourseName is null exactly when SessionCount is 0.</summary>
public class MetricsWeeklyReportDto
{
    public string WeekId { get; set; } = "";
    public double Hours { get; set; }
    public double DeltaVsPreviousWeek { get; set; }
    public string? TopCourseName { get; set; }
    public int SessionCount { get; set; }
}

/// <summary>StudyMetrics.CalcCourseHours, one entry per selected course with ≥1 studied session.</summary>
public class MetricsCourseHoursDto
{
    public int CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public string CourseColor { get; set; } = "#6C5CE7";
    public double Hours { get; set; }
    public int SessionCount { get; set; }
}

/// <summary>StudyMetrics.CalcTopicsProgress.</summary>
public class MetricsTopicsDto
{
    public int Completed { get; set; }
    public int Total { get; set; }
}

/// <summary>One entry of StudyMetrics.CalcUpcomingCourseGoals. DaysLeft is uncapped at the low
/// end (negative = overdue).</summary>
public class MetricsUpcomingGoalDto
{
    public int CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public DateTime TargetDate { get; set; }
    public int DaysLeft { get; set; }
}

/// <summary>Response of GET /api/metrics/achievements?program= - all 44 tiers across every
/// category in AchievementCatalog order.</summary>
public class MetricsAchievementsDto
{
    public int Unlocked { get; set; }
    public int Total { get; set; }
    public List<MetricsAchievementTierDto> Tiers { get; set; } = new();
}

/// <summary>One achievement tier. Category is one of AchievementCatalog's stable string keys
/// (HoursKey/StreakKey/.../ProgramsKey) - see that class for the full list and why they must
/// stay stable.</summary>
public class MetricsAchievementTierDto
{
    public string Category { get; set; } = "";
    public int Threshold { get; set; }
    public bool Unlocked { get; set; }
    public double Current { get; set; }
}

/// <summary>Body of POST /api/webhooks (WebhooksProxyController.Create), forwarded to
/// studylife-webhooks almost verbatim. Events is a plain list of event-type strings (see
/// WebhookEventTypes) - not validated against a closed enum here or on studylife-webhooks' side,
/// so a new event type never needs a contract change on either end.</summary>
public class CreateWebhookRequestDto
{
    public string TargetUrl { get; set; } = "";
    public List<string> Events { get; set; } = new();
}
