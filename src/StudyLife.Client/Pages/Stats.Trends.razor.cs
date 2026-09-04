using StudyLife.Client.Components.Stats;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private bool _forecastAvailable;
    private bool _forecastAlreadyDone;
    private string _forecastDateLabel = "";
    private string _monthCompDeltaLabel = "0h";
    private bool _monthCompUp;
    private List<StatsEctsTimelineCard.TimelinePoint> _ectsTimelinePoints = new();
    private List<StatsProductivityScoreCard.WeekPoint> _productivityWeeks = new();
    private List<StatsGoalHistoryCard.WeekMarker> _goalHistoryWeeks = new();
    private List<StatsInactivityTrendCard.WeekBar> _inactivityWeeks = new();
    private List<StatsSessionLengthHistogramCard.LengthBucket> _sessionLengthBuckets = new();
    private List<StatsEctsPlanCard.PlanPoint> _ectsPlanPoints = new();

    /// <summary>Forecast, month comparison and the week/month-bucketed trend charts - all of them
    /// pure numbers, so this is a straight copy of the builder's output into the card shapes.</summary>
    private void ApplyTrends(StatsCoreSummaryDto core)
    {
        _forecastAvailable = core.Forecast.Available;
        _forecastAlreadyDone = core.Forecast.AlreadyDone;
        _forecastDateLabel = core.Forecast.DateLabel;

        _monthCompUp = core.MonthComparison.Up;
        _monthCompDeltaLabel = core.MonthComparison.DeltaLabel;

        _ectsTimelinePoints = core.EctsTimeline
            .Select(p => new StatsEctsTimelineCard.TimelinePoint(p.Date, p.CumulativeEcts, p.Percent))
            .ToList();

        _ectsPlanPoints = core.EctsPlan
            .Select(p => new StatsEctsPlanCard.PlanPoint(p.Label, p.ActualEcts, p.ActualPercent, p.TargetEcts, p.TargetPercent))
            .ToList();

        _productivityWeeks = core.ProductivityWeeks
            .Select(w => new StatsProductivityScoreCard.WeekPoint(w.Label, w.Percent))
            .ToList();

        _goalHistoryWeeks = core.GoalHistoryWeeks
            .Select(w => new StatsGoalHistoryCard.WeekMarker(w.WeekStart, w.Met, w.Hours))
            .ToList();

        _inactivityWeeks = core.InactivityWeeks
            .Select(w => new StatsInactivityTrendCard.WeekBar(w.Label, w.Hours, w.Percent))
            .ToList();

        _sessionLengthBuckets = core.SessionLengthBuckets
            .Select(b => new StatsSessionLengthHistogramCard.LengthBucket(b.Label, b.Count, b.Percent))
            .ToList();
    }
}
