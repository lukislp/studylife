namespace StudyLife.Shared;

/// <summary>
/// Everything the statistics page (Stats.razor) renders that is derived from sessions/history/
/// settings/courses/goals/quotas/programmes/notes - computed by <see cref="StatsSummaryBuilder"/>
/// so client and server run the exact same code (owner decision: every metric computed in exactly
/// ONE place, see DashboardSummaryDtos.cs for the dashboard's twin of this file). Grouped by the
/// page's three progressive-render phases, because the page fills in phase by phase and each
/// group is copied into the page's fields at its own render point (see Stats.razor.cs's
/// OnTextLoadedAsync).
///
/// Deliberately carries NO localized text: anything built from the i18n table (T.*) stays on the
/// client, so this DTO holds the raw numbers/indices/ids the client formats with. Purely numeric
/// formatting that never touches T (e.g. "3h 20m", "dd.MM.") IS carried as a string, so the
/// server produces byte-identical labels; culture-dependent formatting ("MMM" month names) is
/// NOT - those carry the raw date and the client formats it in its own culture, exactly as today.
///
/// Native health data (cardio fitness / VO2max trend) is deliberately absent: it never leaves the
/// device, see Stats.Health.razor.cs.
/// </summary>
public class StatsSummaryDto
{
    /// <summary>Phase 1 group - everything behind the page's _statsLoading flag.</summary>
    public StatsCoreSummaryDto Core { get; set; } = new();

    /// <summary>Phase 2 group - everything behind _notesLoading.</summary>
    public StatsNotesSummaryDto Notes { get; set; } = new();

    /// <summary>Phase 3 group - everything behind _extendedLoading (except the native health
    /// card, which is not part of this DTO at all).</summary>
    public StatsExtendedSummaryDto Extended { get; set; } = new();
}

/// <summary>
/// Phase 1 group: the course list, the summary/progress tiles and the large majority of the
/// charts - everything computable from settings + courses + goals + sessions + the
/// <see cref="StatsSummaryBuilder.HistoryDays"/>-day history.
/// </summary>
public class StatsCoreSummaryDto
{
    /// <summary>Course list rows, already ordered by hours descending.</summary>
    public List<StatsCourseRowDto> CourseRows { get; set; } = new();

    public int TotalSessions { get; set; }

    /// <summary>Pure number formatting ("12h 30m"), no localized text involved.</summary>
    public string TotalHoursLabel { get; set; } = "0h";

    /// <summary>ECTS-weighted average grade, already formatted with the documented comma
    /// convention; "–" when no grade has been recorded.</summary>
    public string AverageGradeLabel { get; set; } = "–";

    public int EctsEarned { get; set; }
    public int EctsTotal { get; set; }
    public double EctsPercent { get; set; }

    public StatsForecastDto Forecast { get; set; } = new();

    public StatsMonthComparisonDto MonthComparison { get; set; } = new();

    public List<StatsGradeBucketDto> GradeDistribution { get; set; } = new();

    /// <summary>Average grade per semester, ascending by semester.</summary>
    public List<StatsSemesterGradeDto> GradeHistory { get; set; } = new();

    public List<StatsGradeTimelinePointDto> GradeTimeline { get; set; } = new();

    public StatsHoursGradeScatterDto HoursGradeScatter { get; set; } = new();

    public StatsHoursEctsScatterDto HoursEctsScatter { get; set; } = new();

    public StatsHeatmapDto Heatmap { get; set; } = new();

    public StatsDonutDto Donut { get; set; } = new();

    public StatsRhythmDto Rhythm { get; set; } = new();

    public StatsTimeHeatmapDto TimeHeatmap { get; set; } = new();

    public StatsMonthlyBreakdownDto MonthlyBreakdown { get; set; } = new();

    public List<StatsEctsTimelinePointDto> EctsTimeline { get; set; } = new();

    public List<StatsEctsPlanPointDto> EctsPlan { get; set; } = new();

    public List<StatsProductivityWeekDto> ProductivityWeeks { get; set; } = new();

    public List<StatsGoalHistoryWeekDto> GoalHistoryWeeks { get; set; } = new();

    public List<StatsInactivityWeekDto> InactivityWeeks { get; set; } = new();

    public List<StatsLengthBucketDto> SessionLengthBuckets { get; set; } = new();

    public StatsCourseComparisonDto CourseComparison { get; set; } = new();

    public List<StatsCourseBalanceRowDto> CourseBalance { get; set; } = new();
}

/// <summary>Phase 2 group: the notes-vs-study-time correlation card, the only card that needs
/// the notes fetch.</summary>
public class StatsNotesSummaryDto
{
    /// <summary>Empty when fewer than <see cref="StatsSummaryBuilder.NotesCorrelationMinNotes"/>
    /// notes fall into the 12-week window - the card then shows its empty state.</summary>
    public List<StatsNotesCorrelationWeekDto> CorrelationWeeks { get; set; } = new();
}

/// <summary>Phase 3 group: the two cards behind the slower fetches - the semester comparison
/// (needs the ~10-year history) and the cross-programme comparison (needs every programme's
/// catalog).</summary>
public class StatsExtendedSummaryDto
{
    public StatsSemesterComparisonDto SemesterComparison { get; set; } = new();

    /// <summary>Empty with fewer than two programmes - the card then stays hidden entirely.</summary>
    public List<StatsProgramRowDto> ProgramComparison { get; set; } = new();
}

/// <summary>
/// One row of the course list. Carries the whole <see cref="CourseDto"/> (name/icon/color/ECTS)
/// because the card renders all of it; nothing here is localized.
/// </summary>
public class StatsCourseRowDto
{
    public CourseDto Course { get; set; } = new();
    public double Hours { get; set; }
    public int SessionCount { get; set; }
    public bool IsCompleted { get; set; }
    /// <summary>Days until the course goal's target date - null for completed courses and for
    /// courses without a dated goal. Deliberately uncapped at the low end (negative = overdue).</summary>
    public int? DaysRemaining { get; set; }
    public string? CompletionNote { get; set; }
    public decimal? Grade { get; set; }
    public double BarPercent { get; set; }
    /// <summary>Null when there isn't enough history to compare (&lt; 1h logged 30-60 days ago).</summary>
    public double? TrendPercent { get; set; }
    /// <summary>Mini sparkline: weekly hours of the last 12 weeks, normalized per course to its
    /// own maximum (0-100). Null = no sessions in the window, sparkline is omitted.</summary>
    public List<double>? Spark { get; set; }
    public double RingPercent { get; set; }
    public int EctsEarned { get; set; }
}

/// <summary>ECTS completion forecast (same shape as the dashboard's).</summary>
public class StatsForecastDto
{
    public bool Available { get; set; }
    public bool AlreadyDone { get; set; }
    /// <summary>"dd.MM.yyyy"; empty when not available.</summary>
    public string DateLabel { get; set; } = "";
}

/// <summary>This month vs. last month, within the 12-month history window. The delta label is an
/// absolute value; the direction is carried by <see cref="Up"/>.</summary>
public class StatsMonthComparisonDto
{
    public bool Up { get; set; }
    public string DeltaLabel { get; set; } = "0h";
}

/// <summary>One half-grade band of the grade distribution. The band labels ("1,0–1,5", "&gt; 4,0")
/// are fixed notation of the German grading scale, not i18n text.</summary>
public class StatsGradeBucketDto
{
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public double Percent { get; set; }
}

/// <summary>One point of the per-semester grade history. The client turns
/// <see cref="Semester"/> into the localized "S{0}" label.</summary>
public class StatsSemesterGradeDto
{
    public int Semester { get; set; }
    public decimal AvgGrade { get; set; }
    public int CourseCount { get; set; }
    public double BarPercent { get; set; }
}

/// <summary>One individual grade in chronological order. CourseName comes from the goal row, not
/// from the i18n table.</summary>
public class StatsGradeTimelinePointDto
{
    public DateTime Date { get; set; }
    public string CourseName { get; set; } = "";
    public string Color { get; set; } = "";
    public decimal Grade { get; set; }
    public double BarPercent { get; set; }
}

/// <summary>Hours-vs-grade scatter plus its x-axis maximum label (pure number formatting).</summary>
public class StatsHoursGradeScatterDto
{
    public List<StatsHoursGradePointDto> Points { get; set; } = new();
    public string MaxHoursLabel { get; set; } = "0h";
}

/// <summary>Hours-vs-ECTS scatter plus both axis maximum labels.</summary>
public class StatsHoursEctsScatterDto
{
    public List<StatsHoursEctsPointDto> Points { get; set; } = new();
    public string MaxHoursLabel { get; set; } = "0h";
    public string MaxEctsLabel { get; set; } = "0";
}

/// <summary>One point of the hours-vs-grade scatter.</summary>
public class StatsHoursGradePointDto
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "";
    public double Hours { get; set; }
    public decimal Grade { get; set; }
    public double XPercent { get; set; }
    public double YPercent { get; set; }
}

/// <summary>One point of the hours-vs-ECTS scatter.</summary>
public class StatsHoursEctsPointDto
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "";
    public double Hours { get; set; }
    public int EctsEarned { get; set; }
    public double XPercent { get; set; }
    public double YPercent { get; set; }
}

/// <summary>The 53-week calendar heatmap, oldest week first.</summary>
public class StatsHeatmapDto
{
    public List<StatsHeatmapWeekDto> Weeks { get; set; } = new();
}

/// <summary>One column of the calendar heatmap. <see cref="ShowMonthLabel"/> marks the weeks that
/// start a new month - the client renders <see cref="WeekStart"/> as "MMM" there (culture-
/// dependent, hence not pre-formatted) and an empty label everywhere else.</summary>
public class StatsHeatmapWeekDto
{
    public DateTime WeekStart { get; set; }
    public bool ShowMonthLabel { get; set; }
    public List<StatsHeatDayDto> Days { get; set; } = new();
}

/// <summary>One day cell. Level -1 = still in the future (rendered as an empty placeholder),
/// 0 = nothing studied, 1-4 = the fixed &lt;1/&lt;2/&lt;4h bands.</summary>
public class StatsHeatDayDto
{
    public DateTime Date { get; set; }
    public double Hours { get; set; }
    public int Level { get; set; }
    public int SessionCount { get; set; }
    /// <summary>Per-course breakdown for the click popover, hours descending. Empty for future
    /// days.</summary>
    public List<StatsCourseHoursDto> Courses { get; set; } = new();
}

/// <summary>Hours of one course within a heatmap cell. The color is resolved here (it is not
/// localized); the displayed NAME is resolved on the client, which owns the "course #{id}"
/// fallback for since-deleted courses.</summary>
public class StatsCourseHoursDto
{
    public int CourseId { get; set; }
    public string Color { get; set; } = "";
    public double Hours { get; set; }
}

/// <summary>Course-time donut over the whole 12-month history window.</summary>
public class StatsDonutDto
{
    public List<StatsDonutSliceDto> Slices { get; set; } = new();
    /// <summary>Ready-to-use CSS conic-gradient - colors and percentages only. Empty when
    /// nothing was studied.</summary>
    public string Gradient { get; set; } = "";
    public double TotalHours { get; set; }
}

/// <summary>One donut slice, including the data the click drilldown shows.</summary>
public class StatsDonutSliceDto
{
    public int CourseId { get; set; }
    public string Color { get; set; } = "";
    public double Hours { get; set; }
    public double Percent { get; set; }
    public int SessionCount { get; set; }
    /// <summary>12-month mini chart, scaled to THIS course's strongest month.</summary>
    public List<StatsDonutMonthDto> Months { get; set; } = new();
    public List<StatsDonutSessionDto> RecentSessions { get; set; } = new();
}

/// <summary>One month of a donut slice's drilldown chart. The client renders
/// <see cref="MonthStart"/> as "MMM" in its own culture.</summary>
public class StatsDonutMonthDto
{
    public DateTime MonthStart { get; set; }
    public double Hours { get; set; }
    public double Percent { get; set; }
}

/// <summary>One session row of a donut slice's drilldown list.</summary>
public class StatsDonutSessionDto
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string? Topic { get; set; }
}

/// <summary>Study rhythm card: hours per weekday and per time-of-day bucket.</summary>
public class StatsRhythmDto
{
    /// <summary>Seven entries, 0 = Monday. Raw hours - the client pairs them with the localized
    /// weekday names and computes the bar percentages against <see cref="WeekdayMax"/>, exactly
    /// as it does after a live language switch today.</summary>
    public List<double> WeekdayHours { get; set; } = new();

    /// <summary>Max(1, peak weekday hours) - the denominator of the weekday bars.</summary>
    public double WeekdayMax { get; set; } = 1;

    /// <summary>Time-of-day buckets, complete: their labels ("00-06" …) are fixed notation, not
    /// i18n text.</summary>
    public List<StatsBarPointDto> TimeOfDay { get; set; } = new();
}

/// <summary>One bar of a labelled bar chart whose label is not localized.</summary>
public class StatsBarPointDto
{
    public string Label { get; set; } = "";
    public double Hours { get; set; }
    public double Percent { get; set; }
}

/// <summary>
/// Weekday × hour heatmap. The two grids are jagged (7 rows of 24) rather than multidimensional
/// so they survive JSON serialisation; the client copies them back into its [7,24] arrays.
/// </summary>
public class StatsTimeHeatmapDto
{
    public List<List<double>> HoursByCell { get; set; } = new();
    public List<List<int>> SessionCountByCell { get; set; } = new();
    /// <summary>Per-course breakdown for the click detail panel - only for cells that have any.</summary>
    public List<StatsTimeHeatmapCellDto> CellCourses { get; set; } = new();
    /// <summary>Max(1, strongest cell) - the levels are quarters of this, not absolute cutoffs.</summary>
    public double MaxCell { get; set; } = 1;
}

/// <summary>Per-course hours of one weekday/hour cell, hours descending.</summary>
public class StatsTimeHeatmapCellDto
{
    public int Weekday { get; set; }
    public int Hour { get; set; }
    public List<StatsCourseHoursDto> Courses { get; set; } = new();
}

/// <summary>
/// Raw facts of the stacked monthly breakdown. Deliberately NOT the finished segments: the
/// long-tail "other" label and the fallback names for since-deleted courses are localized, so the
/// client assembles the stacks from these facts (the same code path a live language switch
/// already uses).
/// </summary>
public class StatsMonthlyBreakdownDto
{
    public List<DateTime> MonthStarts { get; set; } = new();
    /// <summary>Hours per course, one dictionary per entry of <see cref="MonthStarts"/>.</summary>
    public List<Dictionary<int, double>> PerMonthCourseHours { get; set; } = new();
    /// <summary>Every course of the window, total hours descending - defines the stacking order.</summary>
    public List<int> OrderedIds { get; set; } = new();
    /// <summary>The courses that get their own legend entry/segment; the rest collapse into
    /// "other".</summary>
    public List<int> TopIds { get; set; } = new();
    /// <summary>Max(1, strongest month total) - the shared denominator of all segments.</summary>
    public double MaxMonthTotal { get; set; } = 1;
}

/// <summary>One point of the cumulative ECTS timeline.</summary>
public class StatsEctsTimelinePointDto
{
    public DateTime Date { get; set; }
    public int CumulativeEcts { get; set; }
    public double Percent { get; set; }
}

/// <summary>One month of the actual-vs-target ECTS plan. Actual is null for months in the future.
/// The label ("MM.yy") is numeric-only, hence pre-formatted.</summary>
public class StatsEctsPlanPointDto
{
    public string Label { get; set; } = "";
    public int? ActualEcts { get; set; }
    public double? ActualPercent { get; set; }
    public int TargetEcts { get; set; }
    public double TargetPercent { get; set; }
}

/// <summary>One week of the productivity score. Percent is null for weeks without any studied
/// session (no misleading 0% bar).</summary>
public class StatsProductivityWeekDto
{
    public string Label { get; set; } = "";
    public double? Percent { get; set; }
}

/// <summary>One week of the weekly-goal history.</summary>
public class StatsGoalHistoryWeekDto
{
    public DateTime WeekStart { get; set; }
    public bool Met { get; set; }
    public double Hours { get; set; }
}

/// <summary>One bar of the continuous hours-per-week trend.</summary>
public class StatsInactivityWeekDto
{
    public string Label { get; set; } = "";
    public double Hours { get; set; }
    public double Percent { get; set; }
}

/// <summary>One bucket of the session-length histogram. The labels ("&lt;30m", "120m+") are fixed
/// notation, not i18n text.</summary>
public class StatsLengthBucketDto
{
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public double Percent { get; set; }
}

/// <summary>
/// Raw facts of the grouped per-course comparison chart - same reasoning as
/// <see cref="StatsMonthlyBreakdownDto"/>: the course names are resolved on the client.
/// <see cref="TopCourseIds"/> empty = nothing studied in the window, the card shows its empty
/// state.
/// </summary>
public class StatsCourseComparisonDto
{
    public List<int> TopCourseIds { get; set; } = new();
    public List<DateTime> WeekStarts { get; set; } = new();
    /// <summary>Hours per course, one dictionary per entry of <see cref="WeekStarts"/> - every
    /// dictionary holds exactly the <see cref="TopCourseIds"/> keys.</summary>
    public List<Dictionary<int, double>> PerWeekPerCourse { get; set; } = new();
    /// <summary>One shared scale across all weeks/courses, so the bars stay comparable.</summary>
    public double MaxHours { get; set; } = 1;
}

/// <summary>One row of the ECTS-vs-time balance card, largest deviation first.</summary>
public class StatsCourseBalanceRowDto
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "";
    public double TargetPercent { get; set; }
    public double ActualPercent { get; set; }
}

/// <summary>One week of the notes-vs-hours correlation chart.</summary>
public class StatsNotesCorrelationWeekDto
{
    public string Label { get; set; } = "";
    public int NotesCount { get; set; }
    public double NotesPercent { get; set; }
    public double Hours { get; set; }
    public double HoursPercent { get; set; }
}

/// <summary>
/// Raw facts of the current-vs-previous-semester comparison. The three metric ROWS are assembled
/// on the client (their titles come from the i18n table); everything numeric is decided here.
/// <see cref="HasData"/> false = the card shows its empty state.
/// </summary>
public class StatsSemesterComparisonDto
{
    public bool HasData { get; set; }
    public int CurrentSemester { get; set; }
    public double CurrentHours { get; set; }
    /// <summary>Null when no grade has been recorded in the current semester.</summary>
    public decimal? CurrentGrade { get; set; }
    public int CurrentEcts { get; set; }
    public double PreviousHours { get; set; }
    /// <summary>Average of the previous semesters' averages; null when none of them has a grade.</summary>
    public double? PreviousGrade { get; set; }
    public double PreviousEcts { get; set; }
    public double MaxHoursScale { get; set; } = 1;
    public double MaxEctsScale { get; set; } = 1;
}

/// <summary>One programme of the cross-programme comparison. Name comes from the programme row,
/// GradeLabel is the same formatted average grade convention as everywhere else (null = ungraded).</summary>
public class StatsProgramRowDto
{
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public bool IsCompleted { get; set; }
    public double Hours { get; set; }
    public int SessionCount { get; set; }
    public int EctsEarned { get; set; }
    public int EctsTotal { get; set; }
    public string? GradeLabel { get; set; }
    public double BarPercent { get; set; }
}

/// <summary>
/// One programme's catalog for the cross-programme comparison - the server-side equivalent of the
/// client's per-programme fan-out (GET api/courses?program={id} plus GET api/studyprograms/{id}).
/// <see cref="ProgramId"/> null is the built-in programme, whose quotas are the static
/// <see cref="CourseCatalog.GroupEctsQuotas"/> - <see cref="GroupQuotas"/> is ignored for it, the
/// same way the client never fetches a detail row for it.
/// </summary>
public class StatsProgramCatalogDto
{
    public int? ProgramId { get; set; }
    public List<CourseDto> Courses { get; set; } = new();
    /// <summary>ECTS quotas per elective group. An empty dictionary is the documented fallback
    /// when the programme's detail row could not be loaded (groups then count as full).</summary>
    public Dictionary<string, int> GroupQuotas { get; set; } = new();
}
