namespace StudyLife.Shared;

/// <summary>
/// The raw inputs the printable study report (Report.razor) has after its fetches - exactly
/// what Report.razor.cs's OnTextLoadedAsync loads, mapped to shared types so the server can
/// assemble the same input from the database. <see cref="Now"/> is the caller's wall clock:
/// nothing in <see cref="ReportSummaryBuilder"/> ever reads DateTime.Now/Today itself, so the
/// result is a pure function of this object. The page's own _generatedAt timestamp ("generated
/// on ...") stays a page-level concern, read separately and not part of this input - unlike
/// every other date used here, it isn't part of the report's DATA, just of when it was printed.
/// </summary>
public class ReportSummaryInput
{
    public UserSettingsDto Settings { get; set; } = new();

    /// <summary>All courses of the ACTIVE study programme (GET /api/courses is already
    /// programme-scoped). Defines the id set course goals/history below are filtered through.</summary>
    public List<CourseDto> AllCourses { get; set; } = new();

    /// <summary>Course goals (GET /api/coursegoals), unscoped - the builder applies the
    /// active-programme filter itself.</summary>
    public List<CourseGoalDto> Goals { get; set; } = new();

    /// <summary>Full history (GET /api/sessions/history?days=<see cref="ReportSummaryBuilder.HistoryDays"/>,
    /// studied-only, the endpoint's default), unscoped - a study record must show the ENTIRE
    /// study time to date, not the ±7/90-day window the dashboard/stats tiles use.</summary>
    public List<StudySessionDto> History { get; set; } = new();

    /// <summary>ECTS quotas per elective group of the active programme.</summary>
    public IReadOnlyDictionary<string, int> GroupQuotas { get; set; } = new Dictionary<string, int>();

    /// <summary>All of the user's study programmes - used to resolve the active programme's
    /// display name.</summary>
    public List<StudyProgramSummaryDto> StudyPrograms { get; set; } = new();

    /// <summary>The caller's wall clock.</summary>
    public DateTime Now { get; set; }
}

/// <summary>
/// Everything the printable study report renders - computed by <see cref="ReportSummaryBuilder"/>
/// so client and server run the exact same code. Report.razor.cs is a single phase (the print
/// document is one coherent unit), so unlike DashboardSummaryDto/WrappedSummaryDto this isn't
/// split into phase groups.
/// </summary>
public class ReportSummaryDto
{
    /// <summary>Sorted by semester, then by hours descending within a semester - the
    /// chronological order of the study progression, deliberately not by hours like the stats
    /// page (see StatsSummaryBuilder).</summary>
    public List<ReportCourseRowDto> CourseRows { get; set; } = new();

    public int TotalSessions { get; set; }

    /// <summary>Pure number formatting ("12h 30m"), no localized text involved.</summary>
    public string TotalHoursLabel { get; set; } = "0h 0m";

    /// <summary>ECTS-weighted average grade, already formatted with the documented comma
    /// convention; null (not "–") when no grade has been recorded - the client supplies its own
    /// localized empty text.</summary>
    public string? AverageGradeLabel { get; set; }

    public int EctsEarned { get; set; }
    public int EctsTotal { get; set; }
    public double EctsPercent { get; set; }

    /// <summary>Display name of the active study programme (built-in or custom).</summary>
    public string ProgrammeName { get; set; } = "";

    /// <summary>Null when the history is empty (nothing studied yet).</summary>
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
}

/// <summary>
/// One course row of the report table. Carries the plain fields
/// StatsCourseListCard.CourseStatRow needs (that record type lives in the Client project, so
/// this shared DTO cannot reference it directly) - Report.razor.cs maps each row into that record
/// with the remaining, report-irrelevant fields (BarPercent etc., stats-page-only visuals) at
/// their defaults, exactly as the original hand-written call did.
/// </summary>
public class ReportCourseRowDto
{
    public CourseDto Course { get; set; } = new();
    public double Hours { get; set; }
    public int SessionCount { get; set; }
    public bool IsCompleted { get; set; }

    /// <summary>Days until the course goal's target date (negative = overdue); null when the
    /// course has no goal or no target date.</summary>
    public int? DaysRemaining { get; set; }
    public string? CompletionNote { get; set; }
    public decimal? Grade { get; set; }
}
