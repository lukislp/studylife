namespace StudyLife.Shared;

/// <summary>
/// Everything the dashboard (Index.razor) renders that is derived from sessions/history/
/// settings/courses/goals/quotas/programmes/notes - computed by
/// <see cref="DashboardSummaryBuilder"/> so client and server run the exact same code (owner
/// decision: every metric computed in exactly ONE place, see StudyMetrics.Dashboard.cs and
/// MetricsController). Grouped by the dashboard's three progressive-render phases, because the
/// page fills in phase by phase and each group is copied into the page's fields at its own
/// render point (see Index.razor.cs's LoadDataAsync).
///
/// Deliberately carries NO localized text: anything built from the i18n table (T.*) stays on
/// the client, so this DTO holds the raw numbers/indices/keys the client formats with. Purely
/// numeric formatting that never touches T (e.g. "3h 20m", "dd.MM.yyyy") IS carried as a
/// string, so the server produces byte-identical labels.
///
/// Native health data (HRV readiness, sleep consistency) is deliberately absent: it never
/// leaves the device, see Index.Health.razor.cs.
/// </summary>
public class DashboardSummaryDto
{
    /// <summary>Course pills - the selected courses of the active programme (phase 1).</summary>
    public List<CourseDto> Courses { get; set; } = new();

    public DashboardSessionsSummaryDto Sessions { get; set; } = new();

    public DashboardGoalsSummaryDto Goals { get; set; } = new();

    public DashboardProgressSummaryDto Progress { get; set; } = new();
}

/// <summary>
/// Phase 2 group: everything driven by the session list plus the 400-day history - today/next
/// session, week stats, streak, quotas, trend, today ring, recent sessions, donut, insights,
/// banners and the latest-note preview.
/// </summary>
public class DashboardSessionsSummaryDto
{
    /// <summary>Today's sessions of the active programme, ascending by start time. Raw rows -
    /// the card renders each one individually.</summary>
    public List<StudySessionDto> TodaySessions { get; set; } = new();

    public StudySessionDto? ActiveSession { get; set; }

    public StudySessionDto? UpcomingSession { get; set; }

    public int WeekSessions { get; set; }

    /// <summary>Pure number formatting ("12h", "12h 30m"), no localized text involved.</summary>
    public string WeekHoursLabel { get; set; } = "0h";

    public int Streak { get; set; }

    public int LongestStreak { get; set; }

    public DashboardFocusScoreDto FocusScore { get; set; } = new();

    public DashboardInactivityDto Inactivity { get; set; } = new();

    public DashboardBackupHintDto BackupHint { get; set; } = new();

    public DashboardQuotaTileDto WeekQuota { get; set; } = new();

    public DashboardQuotaTileDto MonthQuota { get; set; } = new();

    public List<DashboardTrendWeekDto> WeeklyTrend { get; set; } = new();

    /// <summary>Week-over-week delta, absolute value already formatted. Null is never produced
    /// by the builder (the page's field is nullable only because it starts out unset).</summary>
    public string? WeekDeltaLabel { get; set; }

    public bool WeekDeltaUp { get; set; }

    /// <summary>Last five studied sessions, newest first.</summary>
    public List<StudySessionDto> RecentSessions { get; set; } = new();

    public DashboardTodayRingDto TodayRing { get; set; } = new();

    public List<DashboardDayDotDto> StreakStrip { get; set; } = new();

    public DashboardMiniDonutDto MiniDonut { get; set; } = new();

    /// <summary>Null when there are fewer than two active courses - the card then shows its
    /// empty state.</summary>
    public DashboardNeglectedCourseDto? NeglectedCourse { get; set; }

    public DashboardProductivityHintDto ProductivityHint { get; set; } = new();

    public DashboardWeekdayInsightDto WeekdayInsight { get; set; } = new();

    public DashboardAnomalyHintDto AnomalyHint { get; set; } = new();

    public DashboardLatestNoteDto LatestNote { get; set; } = new();
}

/// <summary>Today's plan adherence: studied vs. planned sessions. Hidden when nothing was
/// planned today (a ratio against zero is meaningless).</summary>
public class DashboardFocusScoreDto
{
    public bool Visible { get; set; }
    public double Percent { get; set; }
    public int Studied { get; set; }
    public int Planned { get; set; }
}

/// <summary>Inactivity nudge, mirroring InactivityReminderService's threshold logic. Deliberately
/// computed across ALL programmes (see the builder).</summary>
public class DashboardInactivityDto
{
    public bool Show { get; set; }
    public int DaysSinceLastSession { get; set; }
}

/// <summary>Backup staleness hint - owner-only, suppressed on demo instances and wherever the
/// raw backup download isn't supported.</summary>
public class DashboardBackupHintDto
{
    public bool Show { get; set; }
    public bool NeverDownloaded { get; set; }
    public int DaysSinceLastBackup { get; set; }
}

/// <summary>Weekly/monthly quota tile. Labels are pure number formatting; the localized target
/// suffix/legend/warning sentence are assembled on the client from these raw values.</summary>
public class DashboardQuotaTileDto
{
    public string HoursLabel { get; set; } = "0h";
    public int TargetMin { get; set; }
    public int TargetMax { get; set; }
    public double Percent { get; set; }
    public double MinPercent { get; set; }
    public bool Warning { get; set; }
    /// <summary>Shortfall to the minimum goal, formatted; empty when there is no shortfall.</summary>
    public string MissingLabel { get; set; } = "";
}

/// <summary>One bar of the 8-week trend chart.</summary>
public class DashboardTrendWeekDto
{
    /// <summary>Week start as "dd.MM".</summary>
    public string Label { get; set; } = "";
    public double Hours { get; set; }
    public double Percent { get; set; }
    public bool IsCurrent { get; set; }
}

/// <summary>Today's ring: studied hours against the daily target derived from the weekly quota.</summary>
public class DashboardTodayRingDto
{
    /// <summary>Capped at 100 - it is an angle. See <see cref="Exceeded"/> for the overshoot.</summary>
    public double RingPercent { get; set; }
    public string HoursLabel { get; set; } = "0h";
    public string DailyTargetLabel { get; set; } = "0h";
    public bool Exceeded { get; set; }
}

/// <summary>One dot of the 7-day streak strip. The label is an invariant "ddd" abbreviation,
/// same as today.</summary>
public class DashboardDayDotDto
{
    public string Label { get; set; } = "";
    public bool Studied { get; set; }
    public bool IsToday { get; set; }
}

/// <summary>Course-time donut over the last 30 days.</summary>
public class DashboardMiniDonutDto
{
    public List<DashboardDonutSliceDto> Slices { get; set; } = new();
    /// <summary>Ready-to-use CSS conic-gradient - colors and percentages only, no localized text.</summary>
    public string Gradient { get; set; } = "";
    public double TotalHours { get; set; }
}

/// <summary>
/// One donut slice. The rendered name is localized on the client: <see cref="IsOther"/> uses the
/// "other courses" label, an unknown course (<see cref="CourseName"/> null) the "course #{id}"
/// fallback format.
/// </summary>
public class DashboardDonutSliceDto
{
    public int CourseId { get; set; }
    public string? CourseName { get; set; }
    public string Color { get; set; } = "";
    public double Hours { get; set; }
    public double Percent { get; set; }
    public bool IsOther { get; set; }
}

/// <summary>Least-attention course. DaysSinceLastStudied is null when it was never studied
/// within the lookback window (StudyMetrics.NeglectedCourseHistoryDays).</summary>
public class DashboardNeglectedCourseDto
{
    public int CourseId { get; set; }
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "";
    public int? DaysSinceLastStudied { get; set; }
}

/// <summary>
/// Time-of-day productivity hint. <see cref="BestBucketIndex"/> indexes
/// <see cref="DashboardSummaryBuilder.TimeOfDayBuckets"/>; the client turns it into the
/// localized bucket name. Exactly one of PlannedStartTimeLabel / ShowSuggestText is set (or
/// neither, for the neutral insight).
/// </summary>
public class DashboardProductivityHintDto
{
    public bool Visible { get; set; }
    public int BestBucketIndex { get; set; }
    /// <summary>A session already overlaps the best window today.</summary>
    public bool Planned { get; set; }
    /// <summary>"HH:mm" of that session; null when nothing is planned in the window.</summary>
    public string? PlannedStartTimeLabel { get; set; }
    /// <summary>Show the "plan one now" suggestion sentence.</summary>
    public bool ShowSuggestText { get; set; }
    public bool ShowPlanLink { get; set; }
}

/// <summary>Best-weekday insight (0 = Monday .. 6 = Sunday), the rotating alternative to the
/// time-of-day hint. Which variant is shown is a client-side coin flip.</summary>
public class DashboardWeekdayInsightDto
{
    public bool Available { get; set; }
    public int BestIndex { get; set; }
}

/// <summary>"Noticeably less this week than usual" banner.</summary>
public class DashboardAnomalyHintDto
{
    public bool Show { get; set; }
    public int PercentVsBaseline { get; set; }
}

/// <summary>Latest-note preview. NotesCount feeds the "notes taken" achievement category and
/// comes from the same fetch, exactly as today.</summary>
public class DashboardLatestNoteDto
{
    public NoteDto? Note { get; set; }
    public string Excerpt { get; set; } = "";
    public string? CourseName { get; set; }
    public int NotesCount { get; set; }
}

/// <summary>
/// Phase 3 group: goals, ECTS/average grade, topic progress and the completed-programme count
/// (the last one is only consumed by the achievements group, but is fetched with the goals).
/// </summary>
public class DashboardGoalsSummaryDto
{
    /// <summary>Free-text tag per course, for the course pills.</summary>
    public Dictionary<int, string?> CourseTags { get; set; } = new();

    /// <summary>Days until the course's goal target date, only within the cutoff window.
    /// Deliberately uncapped at the low end (negative = overdue).</summary>
    public Dictionary<int, int> CourseDeadlineDays { get; set; } = new();

    public List<DashboardUpcomingGoalDto> UpcomingGoals { get; set; } = new();

    public int EctsEarned { get; set; }
    public int EctsTotal { get; set; }
    public double EctsPercent { get; set; }

    /// <summary>ECTS-weighted average grade, already formatted with the documented comma
    /// convention; "–" when no grade has been recorded.</summary>
    public string AverageGradeLabel { get; set; } = "–";

    public int TopicsCompleted { get; set; }
    public int TopicsTotal { get; set; }
    public double TopicsPercent { get; set; }

    public int ProgramsCompleted { get; set; }
}

/// <summary>One upcoming (open, dated) course goal.</summary>
public class DashboardUpcomingGoalDto
{
    public int CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public DateTime? TargetDate { get; set; }
    public int DaysLeft { get; set; }
}

/// <summary>
/// Phase 5 group: everything computed from the ~10-year history - forecast, graduation goal,
/// month/year comparison, all-time records and achievements.
/// </summary>
public class DashboardProgressSummaryDto
{
    public DashboardForecastDto Forecast { get; set; } = new();

    public DashboardGraduationGoalDto GraduationGoal { get; set; } = new();

    public DashboardMonthComparisonDto MonthComparison { get; set; } = new();

    public DashboardBestRecordsDto BestRecords { get; set; } = new();

    public DashboardAchievementsDto Achievements { get; set; } = new();
}

/// <summary>ECTS completion forecast.</summary>
public class DashboardForecastDto
{
    public bool Available { get; set; }
    public bool AlreadyDone { get; set; }
    /// <summary>"dd.MM.yyyy"; empty when not available.</summary>
    public string DateLabel { get; set; } = "";
}

/// <summary>Desired-graduation-date card: the inverse of the forecast. Values are formatted with
/// the same one-decimal comma convention the card has always used.</summary>
public class DashboardGraduationGoalDto
{
    public bool Visible { get; set; }
    public bool Expired { get; set; }
    public bool OnTrack { get; set; }
    public string RequiredValue { get; set; } = "";
    public string PaceValue { get; set; } = "";
    public string TargetDateValue { get; set; } = "";
}

/// <summary>Month-over-month / year-over-year comparison. Delta labels are absolute values; the
/// direction is carried by the Up flags.</summary>
public class DashboardMonthComparisonDto
{
    public string CurrentLabel { get; set; } = "0h";
    public string VsLastMonthLabel { get; set; } = "0h";
    public bool VsLastMonthUp { get; set; }
    public bool HasYearData { get; set; }
    public string VsLastYearLabel { get; set; } = "0h";
    public bool VsLastYearUp { get; set; }
}

/// <summary>All-time best day/week, plus whether today/this week already ties or beats it.</summary>
public class DashboardBestRecordsDto
{
    public string BestDayHoursLabel { get; set; } = "0h";
    public string BestDayDateLabel { get; set; } = "–";
    public bool BestDayIsNew { get; set; }
    public string BestWeekHoursLabel { get; set; } = "0h";
    public string BestWeekRangeLabel { get; set; } = "–";
    public bool BestWeekIsNew { get; set; }
}

/// <summary>
/// Achievement tiers in render order. Mirrors MetricsAchievementsDto's shape (category key +
/// threshold + unlocked + current) so both consumers stay in sync; the client owns the
/// per-category icon and localized name.
/// </summary>
public class DashboardAchievementsDto
{
    public int Unlocked { get; set; }
    public int Total { get; set; }
    public List<DashboardAchievementTierDto> Tiers { get; set; } = new();
}

/// <summary>One achievement tier. Category is an AchievementCatalog key.</summary>
public class DashboardAchievementTierDto
{
    public string Category { get; set; } = "";
    public int Threshold { get; set; }
    public bool Unlocked { get; set; }
    public double Current { get; set; }
}
