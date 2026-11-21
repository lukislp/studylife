// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Index
{
    // ECTS completion forecast (mirrors Stats.razor's semester-based forecast, same formula)
    private bool _forecastAvailable;
    private bool _forecastAlreadyDone;
    private DateTime? _forecastDate;
    private string _forecastDateFallback = "";
    /// <summary>Resolved at render time in the UI language (see SummaryDateLabels).</summary>
    private string ForecastDateLabel => SummaryDateLabels.Date(_forecastDate, _forecastDateFallback);

    // Desired graduation date: the inverse of the forecast - instead of "when will I be done at
    // my current pace?" the card answers "how many h/week do I need to be done by the target
    // date?". Shares every guard with the forecast, see DashboardSummaryBuilder.
    private bool _gradGoalVisible;
    private bool _gradGoalExpired;
    private bool _gradGoalOnTrack;
    private string _gradGoalRequiredValue = "";
    private string _gradGoalPaceValue = "";
    private DateTime? _gradGoalTargetDate;
    private string _gradGoalTargetDateFallback = "";
    private string GradGoalTargetDateValue => SummaryDateLabels.Date(_gradGoalTargetDate, _gradGoalTargetDateFallback);

    // Month-over-month / year-over-year hours comparison (reuses the achievements' long-range history)
    private string _monthCompCurrentLabel = "0h";
    private string? _monthCompVsLastMonthLabel;
    private bool _monthCompVsLastMonthUp;
    private bool _monthCompHasYearData;
    private string? _monthCompVsLastYearLabel;
    private bool _monthCompVsLastYearUp;

    // Best-record card: single best all-time day/week, plus whether today/the current week
    // already ties or beats that all-time best.
    private string _bestDayHoursLabel = "0h";
    private DateTime? _bestDay;
    private string _bestDayDateFallback = "–";
    private string BestDayDateLabel => SummaryDateLabels.Date(_bestDay, _bestDayDateFallback);
    private bool _bestDayIsNew;
    private string _bestWeekHoursLabel = "0h";
    private DateTime? _bestWeekStart;
    private string _bestWeekRangeFallback = "–";
    private string BestWeekRangeLabel => SummaryDateLabels.WeekRange(_bestWeekStart, _bestWeekRangeFallback);
    private bool _bestWeekIsNew;

    /// <summary>Phase 5 result -> the fields the markup binds to. Every label here is pure number
    /// or date formatting done by the builder; only the achievement names below need T.</summary>
    private void ApplyProgressSummary(DashboardProgressSummaryDto p)
    {
        _forecastAvailable = p.Forecast.Available;
        _forecastAlreadyDone = p.Forecast.AlreadyDone;
        _forecastDate = p.Forecast.Date;
        _forecastDateFallback = p.Forecast.DateLabel;

        _gradGoalVisible = p.GraduationGoal.Visible;
        _gradGoalExpired = p.GraduationGoal.Expired;
        _gradGoalOnTrack = p.GraduationGoal.OnTrack;
        _gradGoalRequiredValue = p.GraduationGoal.RequiredValue;
        _gradGoalPaceValue = p.GraduationGoal.PaceValue;
        _gradGoalTargetDate = p.GraduationGoal.TargetDate;
        _gradGoalTargetDateFallback = p.GraduationGoal.TargetDateValue;

        _monthCompCurrentLabel = p.MonthComparison.CurrentLabel;
        _monthCompVsLastMonthLabel = p.MonthComparison.VsLastMonthLabel;
        _monthCompVsLastMonthUp = p.MonthComparison.VsLastMonthUp;
        _monthCompHasYearData = p.MonthComparison.HasYearData;
        _monthCompVsLastYearLabel = p.MonthComparison.VsLastYearLabel;
        _monthCompVsLastYearUp = p.MonthComparison.VsLastYearUp;

        _bestDayHoursLabel = p.BestRecords.BestDayHoursLabel;
        _bestDay = p.BestRecords.BestDay;
        _bestDayDateFallback = p.BestRecords.BestDayDateLabel;
        _bestDayIsNew = p.BestRecords.BestDayIsNew;
        _bestWeekHoursLabel = p.BestRecords.BestWeekHoursLabel;
        _bestWeekStart = p.BestRecords.BestWeekStart;
        _bestWeekRangeFallback = p.BestRecords.BestWeekRangeLabel;
        _bestWeekIsNew = p.BestRecords.BestWeekIsNew;

        ApplyAchievements(p.Achievements);
    }
}
