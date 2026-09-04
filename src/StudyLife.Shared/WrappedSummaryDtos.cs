namespace StudyLife.Shared;

/// <summary>
/// The raw inputs the "wrapped" year-in-review page has after its fetches - exactly what
/// Wrapped.razor.cs's OnTextLoadedAsync loads, mapped to shared types so the server can
/// assemble the same input from the database. <see cref="Now"/> is the caller's wall clock:
/// nothing in <see cref="WrappedSummaryBuilder"/> ever reads DateTime.Now/Today itself, so the
/// result is a pure function of this object (and therefore cacheable and testable). Currently
/// unused by the computation itself (both history windows already come pre-filtered from their
/// fetch), but kept for the same single-wall-clock-read contract as DashboardSummaryInput/
/// ReportSummaryInput, and so a future computation here never needs to reach for DateTime.Now.
/// </summary>
public class WrappedSummaryInput
{
    public UserSettingsDto Settings { get; set; } = new();

    /// <summary>All courses of the ACTIVE study programme (GET /api/courses is already
    /// programme-scoped). Defines the id set every session/note below is filtered through.</summary>
    public List<CourseDto> AllCourses { get; set; } = new();

    /// <summary>The recap window (GET /api/sessions/history?days=<see cref="WrappedSummaryBuilder.PeriodHistoryDays"/>,
    /// studied-only, the endpoint's default), unscoped - the builder applies the active-programme
    /// filter itself.</summary>
    public List<StudySessionDto> PeriodHistory { get; set; } = new();

    /// <summary>All-time history behind the achievements count (GET /api/sessions/history?days=
    /// <see cref="WrappedSummaryBuilder.AllTimeHistoryDays"/>, studied-only), unscoped.</summary>
    public List<StudySessionDto> AllTimeHistory { get; set; } = new();

    /// <summary>ECTS quotas per elective group of the active programme.</summary>
    public IReadOnlyDictionary<string, int> GroupQuotas { get; set; } = new Dictionary<string, int>();

    /// <summary>All of the user's study programmes - the completed-programmes achievement is a
    /// deliberately cross-programme milestone.</summary>
    public List<StudyProgramSummaryDto> StudyPrograms { get; set; } = new();

    /// <summary>Notes, for the notes-taken achievement category.</summary>
    public List<NoteDto> Notes { get; set; } = new();

    /// <summary>The caller's wall clock.</summary>
    public DateTime Now { get; set; }
}

/// <summary>
/// Everything the wrapped page renders - computed by <see cref="WrappedSummaryBuilder"/> so
/// client and server run the exact same code. Split into the page's two progressive-render
/// phases (recap, then achievements), same convention as DashboardSummaryDto.
///
/// Deliberately carries NO localized text: the busiest-weekday name and the chronotype
/// sentence stay on the client, built from the raw index/hours here.
/// </summary>
public class WrappedSummaryDto
{
    public WrappedRecapDto Recap { get; set; } = new();

    public WrappedAchievementsDto Achievements { get; set; } = new();
}

/// <summary>Phase 1 (recap period, 365 days): totals, streak, top course, busiest weekday,
/// chronotype hours.</summary>
public class WrappedRecapDto
{
    public double TotalHours { get; set; }

    /// <summary>Pure number formatting ("12h 30m"), no localized text involved.</summary>
    public string TotalHoursLabel { get; set; } = "0h 0m";

    public int TotalSessions { get; set; }

    public int LongestStreak { get; set; }

    /// <summary>Null when nothing was studied in the recap period.</summary>
    public WrappedTopCourseDto? TopCourse { get; set; }

    /// <summary>Null when nothing was studied in the recap period (every weekday at 0h).</summary>
    public WrappedBusiestWeekdayDto? BusiestWeekday { get; set; }

    /// <summary>Hours studied before 7am in the recap period.</summary>
    public double EarlyBirdHours { get; set; }

    /// <summary>Hours studied from 10pm on in the recap period.</summary>
    public double NightOwlHours { get; set; }
}

/// <summary>Course with the most studied hours in the recap period.</summary>
public class WrappedTopCourseDto
{
    public int CourseId { get; set; }
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "";
    public double Hours { get; set; }
}

/// <summary>Weekday (0 = Monday .. 6 = Sunday) with the most studied hours in the recap period -
/// the client turns <see cref="Index"/> into the localized weekday name.</summary>
public class WrappedBusiestWeekdayDto
{
    public int Index { get; set; }
    public double Hours { get; set; }
}

/// <summary>Phase 2 (achievements, all-time history): unlocked/total tier counts, same 13
/// categories as AchievementCatalog/DashboardAchievementsDto.</summary>
public class WrappedAchievementsDto
{
    public int Unlocked { get; set; }
    public int Total { get; set; }
}
