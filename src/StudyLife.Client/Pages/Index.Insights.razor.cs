using StudyLife.Client.Components.Dashboard;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Index
{
    // Mini course-time donut (last 30 days)
    private List<DashboardCourseDonutCard.DonutSlice> _miniDonutSlices = new();
    private string _miniDonutGradient = "";
    private double _miniDonutTotalHours;

    // Latest note preview
    private NoteDto? _latestNote;
    private string _latestNoteExcerpt = "";
    private string? _latestNoteCourseName;

    // Least-attention course (active course studied longest ago, or never)
    private DashboardNeglectedCourseCard.NeglectedCourse? _neglectedCourse;
    private string _neglectedCourseHint = "";

    // Productivity hint: translates the study rhythm (Stats' time-of-day buckets) into a concrete
    // suggestion for today - see DashboardSummaryBuilder. Only visible with sufficient data.
    private bool _productivityHintVisible;
    private string _productivityInsight = "";
    private bool _productivityPlanned;
    private string? _productivityStatus;
    private bool _productivityShowPlanLink;

    // Second, simpler insight (best weekday) that rotates with the time-of-day insight above.
    // _insightVariant is a pure UI coin flip, picked once per page load (OnTextLoadedAsync), same
    // idea as _motivation's random pick, so the card doesn't flicker between variants on every
    // re-render/StateHasChanged - which is why it stays on the client and never enters the shared
    // builder. The builder's own (a)/(b)/(c)/(d) state machine is left completely untouched by it:
    // when the weekday variant wins AND has enough data, its result simply overwrites the four
    // _productivity* fields afterwards; otherwise the time-of-day result stands.
    private int _insightVariant;

    // Anomaly hint ("noticeably less this week than usual")
    private bool _showAnomalyHint;
    private int _anomalyPercentVsBaseline;

    private void ApplyLatestNote(DashboardLatestNoteDto note)
    {
        _latestNote = note.Note;
        _latestNoteExcerpt = note.Excerpt;
        _latestNoteCourseName = note.CourseName;
    }

    private void ApplyNeglectedCourse(DashboardNeglectedCourseDto? pick)
    {
        if (pick == null)
        {
            _neglectedCourse = null;
            _neglectedCourseHint = "";
            return;
        }

        _neglectedCourse = new DashboardNeglectedCourseCard.NeglectedCourse(pick.Name, pick.Icon, pick.Color);
        _neglectedCourseHint = pick.DaysSinceLastStudied.HasValue
            ? string.Format(T.LastStudiedDaysAgo ?? "", pick.DaysSinceLastStudied.Value)
            : string.Format(T.NotStudiedYet ?? "", StudyMetrics.NeglectedCourseHistoryDays);
    }

    private void ApplyMiniDonut(DashboardMiniDonutDto donut)
    {
        _miniDonutTotalHours = donut.TotalHours;
        _miniDonutGradient = donut.Gradient;
        _miniDonutSlices = donut.Slices
            .Select(s => new DashboardCourseDonutCard.DonutSlice(DonutSliceName(s), s.Color, s.Hours, s.Percent))
            .ToList();
    }

    /// <summary>The only localized part of a donut slice: the collapsed "other courses" entry and
    /// the fallback for a course that is no longer in the catalog.</summary>
    private string DonutSliceName(DashboardDonutSliceDto slice) => slice.IsOther
        ? T.OtherCoursesSlice
        : slice.CourseName ?? string.Format(T.CourseFallbackName ?? "", slice.CourseId);

    private void ApplyAnomalyHint(DashboardAnomalyHintDto anomaly)
    {
        _showAnomalyHint = anomaly.Show;
        _anomalyPercentVsBaseline = anomaly.PercentVsBaseline;
    }

    /// <summary>
    /// Turns the builder's raw insight state into the localized card text. The weekday variant
    /// rotation happens here rather than in the builder: which of the two insights is shown is a
    /// per-page-load coin flip (_insightVariant), not a property of the data. If the weekday
    /// variant was picked for this page load AND has enough data, it replaces the time-of-day
    /// insight's output wholesale - the builder's own visibility/planned/status/plan-link logic
    /// stays intact as the fallback whenever the weekday variant isn't available.
    /// </summary>
    private void ApplyInsights(DashboardProductivityHintDto hint, DashboardWeekdayInsightDto weekday)
    {
        _productivityHintVisible = hint.Visible;
        _productivityInsight = hint.Visible
            ? string.Format(T.ProductivityInsightFormat ?? "", TimeOfDayBucketName(hint.BestBucketIndex))
            : "";
        _productivityPlanned = hint.Planned;
        _productivityStatus = hint.PlannedStartTimeLabel != null
            ? string.Format(T.ProductivityPlannedFormat ?? "", hint.PlannedStartTimeLabel)
            : hint.ShowSuggestText ? T.ProductivitySuggestText : null;
        _productivityShowPlanLink = hint.ShowPlanLink;

        if (_insightVariant == 1 && weekday.Available)
        {
            _productivityHintVisible = true;
            _productivityInsight = string.Format(T.ProductivityWeekdayInsightFormat ?? "", WeekdayName(weekday.BestIndex));
            _productivityPlanned = false;
            _productivityStatus = null;
            _productivityShowPlanLink = false;
        }
    }

    /// <summary>Same order as DashboardSummaryBuilder.TimeOfDayBuckets.</summary>
    private string TimeOfDayBucketName(int index)
    {
        var names = new[]
        {
            T.ProductivityBucketNight, T.ProductivityBucketEarlyMorning, T.ProductivityBucketMorning,
            T.ProductivityBucketMidday, T.ProductivityBucketAfternoon, T.ProductivityBucketEvening,
            T.ProductivityBucketLateEvening,
        };
        return names[index];
    }

    /// <summary>0 = Monday .. 6 = Sunday, same convention as the builder's weekday insight.</summary>
    private string WeekdayName(int index)
    {
        var names = new[]
        {
            T.WeekdayMonday, T.WeekdayTuesday, T.WeekdayWednesday, T.WeekdayThursday,
            T.WeekdayFriday, T.WeekdaySaturday, T.WeekdaySunday,
        };
        return names[index];
    }
}
