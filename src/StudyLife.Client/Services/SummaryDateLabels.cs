// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>
/// Formats the raw dates of the shared dashboard/stats summaries (StudyLife.Shared's
/// DashboardSummaryDto/StatsSummaryDto) in the UI language via <see cref="LocalDate"/>. The
/// summaries are built by the same code on the server and in the WASM fallback and therefore
/// cannot know the UI language; they carry the raw date next to a fixed-format label. The label
/// is only the fallback for a response from an older server that does not send the date yet.
///
/// Callers keep the raw date and resolve the label at render time (computed properties), so a
/// live language switch re-renders every chart axis in the new format without a reload.
/// </summary>
public static class SummaryDateLabels
{
    /// <summary>Full numeric date, e.g. 02.12.2034 / 02/12/2034.</summary>
    public static string Date(DateTime? value, string fallback) =>
        value is { } v ? LocalDate.Short(v) : fallback;

    /// <summary>Day and month, e.g. 19.10. / 19/10.</summary>
    public static string DayMonth(DateTime? value, string fallback) =>
        value is { } v ? LocalDate.DayMonth(v) : fallback;

    /// <summary>Compact day and month for narrow axes, e.g. 19.10 / 19/10.</summary>
    public static string AxisDayMonth(DateTime? value, string fallback) =>
        value is { } v ? LocalDate.AxisDayMonth(v) : fallback;

    /// <summary>Month and two-digit year, e.g. 05.26 / 05/26.</summary>
    public static string MonthYear(DateTime? value, string fallback) =>
        value is { } v ? LocalDate.MonthYear(v) : fallback;

    /// <summary>Clock time, 12h for English and 24h elsewhere. Null stays null (nothing planned).</summary>
    public static string? Time(DateTime? value, string? fallback) =>
        value is { } v ? LocalDate.Time(v) : fallback;

    /// <summary>Monday-to-Sunday range of the week starting at <paramref name="weekStart"/>,
    /// e.g. 17.08. – 23.08.2026 / 17/08 – 23/08/2026.</summary>
    public static string WeekRange(DateTime? weekStart, string fallback) =>
        weekStart is { } ws ? LocalDate.DayRange(ws, ws.AddDays(6)) : fallback;
}
