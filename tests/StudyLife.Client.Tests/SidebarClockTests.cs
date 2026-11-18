// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using StudyLife.Client.Services;
using StudyLife.Client.Shared;

namespace StudyLife.Client.Tests;

/// <summary>LocalDate keeps its names in static state, so these tests must not overlap with others.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class LocalDateStateCollection
{
    public const string Name = "LocalDate static state";
}

/// <summary>The sidebar clock (a parameterless component the layout never re-renders) follows the UI
/// language: its weekday name switches as soon as the localized names arrive.</summary>
[Collection(LocalDateStateCollection.Name)]
public class SidebarClockTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    private static DateNames German() => new(
        ["So.", "Mo.", "Di.", "Mi.", "Do.", "Fr.", "Sa."],
        ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"],
        ["Jan.", "Feb.", "März", "Apr.", "Mai", "Juni", "Juli", "Aug.", "Sept.", "Okt.", "Nov.", "Dez."],
        ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"]);

    public void Dispose()
    {
        LocalDate.ResetNames();
        _ctx.Dispose();
    }

    [Fact]
    public void The_date_switches_from_the_english_fallback_to_the_ui_language_when_the_names_arrive()
    {
        LocalDate.ApplyNames("de", null);
        var cut = _ctx.Render<SidebarClock>();
        var today = DateTime.Now;
        var english = $"{LocalDate.Weekday(today, true)}, {LocalDate.DayMonth(today)}";
        Assert.Equal(english, cut.Find(".date-display").TextContent);

        LocalDate.ApplyNames("de", German());

        var expected = $"{German().WeekdaysShort[(int)today.DayOfWeek]}, {LocalDate.DayMonth(today)}";
        cut.WaitForAssertion(() => Assert.Equal(expected, cut.Find(".date-display").TextContent));
        Assert.NotEqual(english, expected);
    }

    [Fact]
    public void A_disposed_clock_no_longer_listens_for_language_changes()
    {
        LocalDate.ApplyNames("de", null);
        _ctx.Render<SidebarClock>();
        _ctx.Dispose();

        var ex = Record.Exception(() => LocalDate.ApplyNames("de", German()));

        Assert.Null(ex);
    }
}
