using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Index
{
    // ECTS completion forecast (mirrors Stats.razor's semester-based forecast, same formula)
    private bool _forecastAvailable;
    private bool _forecastAlreadyDone;
    private string _forecastDateLabel = "";

    // Desired graduation date: the inverse of the forecast - instead of "when will I be done at
    // my current pace?" the card answers "how many h/week do I need to be done by the target
    // date?". Calculated in BuildForecast, since all intermediate values (baselineWeeksNeeded,
    // recentWeeklyHours, referenceWeeklyHours) are already available there.
    private bool _gradGoalVisible;
    private bool _gradGoalExpired;
    private bool _gradGoalOnTrack;
    private string _gradGoalRequiredValue = "";
    private string _gradGoalPaceValue = "";
    private string _gradGoalTargetDateValue = "";

    // Month-over-month / year-over-year hours comparison (reuses the achievements' long-range history)
    private string _monthCompCurrentLabel = "0h";
    private string _monthCompVsLastMonthLabel = "0h";
    private bool _monthCompVsLastMonthUp;
    private bool _monthCompHasYearData;
    private string _monthCompVsLastYearLabel = "0h";
    private bool _monthCompVsLastYearUp;

    // Best-record card (Task 1): single best all-time day/week from _allTimeHistory, plus whether
    // today/the current week already ties or beats that all-time best.
    private string _bestDayHoursLabel = "0h";
    private string _bestDayDateLabel = "–";
    private bool _bestDayIsNew;
    private string _bestWeekHoursLabel = "0h";
    private string _bestWeekRangeLabel = "–";
    private bool _bestWeekIsNew;

    private void BuildForecast(UserSettings settings, List<CourseDto> allCourses, List<StudySessionDto> allTimeHistory)
    {
        // The graduation-goal card shares every guard with the forecast: no target date,
        // everything completed, or missing semester structure -> hide the card entirely.
        _gradGoalVisible = false;
        var forecast = StudyMetrics.CalcForecast(_ectsTotal, _ectsEarned, allCourses,
            settings.WeeklyGoalMinHours, settings.WeeklyGoalMaxHours, allTimeHistory, DateTime.Now);
        _forecastAvailable = forecast.Available;
        _forecastAlreadyDone = forecast.AlreadyDone;
        if (!forecast.Available) return;
        _forecastDateLabel = forecast.ForecastDate!.Value.ToString("dd.MM.yyyy");

        // Desired graduation date: inverse of the forecast. From the same semester baseline model
        // (BaselineWeeksNeeded × configured reference workload, see StudyMetrics.CalcForecast),
        // the structurally still-needed total effort in hours follows; spread across the weeks
        // until the target date, that's the required pace. "On track" = the same 8-week pace
        // (RecentWeeklyHours) that also refines the forecast.
        if (settings.TargetGraduationDate.HasValue)
        {
            _gradGoalVisible = true;
            var targetDate = settings.TargetGraduationDate.Value.Date;
            _gradGoalTargetDateValue = targetDate.ToString("dd.MM.yyyy");
            var weeksUntilTarget = (targetDate - DateTime.Today).TotalDays / 7.0;
            _gradGoalExpired = weeksUntilTarget <= 0;
            if (!_gradGoalExpired)
            {
                var remainingEffortHours = forecast.BaselineWeeksNeeded * forecast.ReferenceWeeklyHours;
                var requiredWeeklyHours = remainingEffortHours / weeksUntilTarget;
                _gradGoalOnTrack = forecast.RecentWeeklyHours >= requiredWeeklyHours;
                _gradGoalRequiredValue = requiredWeeklyHours
                    .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
                _gradGoalPaceValue = forecast.RecentWeeklyHours
                    .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
            }
        }
    }

    private void BuildMonthComparison()
    {
        var result = StudyMetrics.CalcMonthComparison(_allTimeHistory, DateTime.Today);

        _monthCompCurrentLabel = FormatHoursLabel(result.CurrentMonthHours);
        _monthCompVsLastMonthUp = result.DeltaVsPreviousMonth >= 0;
        _monthCompVsLastMonthLabel = FormatHoursLabel(Math.Abs(result.DeltaVsPreviousMonth));

        _monthCompHasYearData = result.HasYearData;
        if (result.HasYearData)
        {
            _monthCompVsLastYearUp = result.DeltaVsLastYear!.Value >= 0;
            _monthCompVsLastYearLabel = FormatHoursLabel(Math.Abs(result.DeltaVsLastYear!.Value));
        }
    }

    // Best-record card (task 1): single all-time best value for a day or a week (Mon-Sun),
    // from _allTimeHistory (already filtered to "studied", see the AchievementHistoryDays fetch
    // above). "New record" badge when today or the current week already reaches/exceeds the
    // previous best - since _allTimeHistory extends to "now", the best value in that case is
    // simply today/this week itself.
    private void BuildBestRecords()
    {
        if (_allTimeHistory.Count == 0)
        {
            _bestDayHoursLabel = FormatHoursLabel(0);
            _bestDayDateLabel = "–";
            _bestDayIsNew = false;
            _bestWeekHoursLabel = FormatHoursLabel(0);
            _bestWeekRangeLabel = "–";
            _bestWeekIsNew = false;
            return;
        }

        var bestDay = _allTimeHistory
            .GroupBy(s => s.StartTime.Date)
            .Select(g => (Day: g.Key, Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .OrderByDescending(x => x.Hours)
            .First();
        _bestDayHoursLabel = FormatHoursLabel(bestDay.Hours);
        _bestDayDateLabel = bestDay.Day.ToString("dd.MM.yyyy");

        var bestWeek = _allTimeHistory
            .GroupBy(s => StudyMetrics.WeekStartOf(s.StartTime))
            .Select(g => (WeekStart: g.Key, Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
            .OrderByDescending(x => x.Hours)
            .First();
        _bestWeekHoursLabel = FormatHoursLabel(bestWeek.Hours);
        _bestWeekRangeLabel = $"{bestWeek.WeekStart:dd.MM.} – {bestWeek.WeekStart.AddDays(6):dd.MM.yyyy}";

        var today = DateTime.Today;
        var todayHours = _allTimeHistory.Where(s => s.StartTime.Date == today).Sum(s => (s.EndTime - s.StartTime).TotalHours);
        _bestDayIsNew = todayHours > 0 && todayHours >= bestDay.Hours;

        var thisWeekStart = StudyMetrics.WeekStartOf(today);
        var thisWeekHours = _allTimeHistory.Where(s => StudyMetrics.WeekStartOf(s.StartTime) == thisWeekStart).Sum(s => (s.EndTime - s.StartTime).TotalHours);
        _bestWeekIsNew = thisWeekHours > 0 && thisWeekHours >= bestWeek.Hours;
    }
}
