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
    // date?". Shares every guard with the forecast, see DashboardSummaryBuilder.
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

    // Best-record card: single best all-time day/week, plus whether today/the current week
    // already ties or beats that all-time best.
    private string _bestDayHoursLabel = "0h";
    private string _bestDayDateLabel = "–";
    private bool _bestDayIsNew;
    private string _bestWeekHoursLabel = "0h";
    private string _bestWeekRangeLabel = "–";
    private bool _bestWeekIsNew;

    /// <summary>Phase 5 result -> the fields the markup binds to. Every label here is pure number
    /// or date formatting done by the builder; only the achievement names below need T.</summary>
    private void ApplyProgressSummary(DashboardProgressSummaryDto p)
    {
        _forecastAvailable = p.Forecast.Available;
        _forecastAlreadyDone = p.Forecast.AlreadyDone;
        _forecastDateLabel = p.Forecast.DateLabel;

        _gradGoalVisible = p.GraduationGoal.Visible;
        _gradGoalExpired = p.GraduationGoal.Expired;
        _gradGoalOnTrack = p.GraduationGoal.OnTrack;
        _gradGoalRequiredValue = p.GraduationGoal.RequiredValue;
        _gradGoalPaceValue = p.GraduationGoal.PaceValue;
        _gradGoalTargetDateValue = p.GraduationGoal.TargetDateValue;

        _monthCompCurrentLabel = p.MonthComparison.CurrentLabel;
        _monthCompVsLastMonthLabel = p.MonthComparison.VsLastMonthLabel;
        _monthCompVsLastMonthUp = p.MonthComparison.VsLastMonthUp;
        _monthCompHasYearData = p.MonthComparison.HasYearData;
        _monthCompVsLastYearLabel = p.MonthComparison.VsLastYearLabel;
        _monthCompVsLastYearUp = p.MonthComparison.VsLastYearUp;

        _bestDayHoursLabel = p.BestRecords.BestDayHoursLabel;
        _bestDayDateLabel = p.BestRecords.BestDayDateLabel;
        _bestDayIsNew = p.BestRecords.BestDayIsNew;
        _bestWeekHoursLabel = p.BestRecords.BestWeekHoursLabel;
        _bestWeekRangeLabel = p.BestRecords.BestWeekRangeLabel;
        _bestWeekIsNew = p.BestRecords.BestWeekIsNew;

        ApplyAchievements(p.Achievements);
    }
}
