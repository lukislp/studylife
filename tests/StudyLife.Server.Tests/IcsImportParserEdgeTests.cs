// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Tolerance of IcsImportParser towards the broken or minimal exports real calendar tools
/// produce: lines without a colon or without a property name, parameters without "=", events
/// without DTEND (all-day vs. timed default), an end before the start, missing SUMMARY,
/// unparseable or empty dates. None of these may throw or produce a zero-length session.
/// </summary>
public class IcsImportParserEdgeTests
{
    private static string Calendar(params string[] eventLines) =>
        string.Join("\r\n", ["BEGIN:VCALENDAR", "VERSION:2.0", "BEGIN:VEVENT", .. eventLines, "END:VEVENT", "END:VCALENDAR"]);

    [Fact]
    public void AllDayEventWithoutEnd_LastsOneDay_AndMissingSummaryBecomesAnEmptyTitle()
    {
        var events = IcsImportParser.Parse(Calendar("DTSTART;VALUE=DATE:20261105"));

        var ev = Assert.Single(events);
        Assert.Equal(new DateTime(2026, 11, 5), ev.StartTime);
        Assert.Equal(new DateTime(2026, 11, 6), ev.EndTime);
        Assert.Equal("", ev.Title);
        Assert.Null(ev.Description);
        Assert.False(ev.HasUnexpandedRecurrence);
    }

    [Fact]
    public void TimedEventWithoutEnd_LastsOneHour()
    {
        var ev = Assert.Single(IcsImportParser.Parse(Calendar("DTSTART:20261105T090000", "SUMMARY:Lecture")));
        Assert.Equal(new DateTime(2026, 11, 5, 9, 0, 0), ev.StartTime);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0), ev.EndTime);
    }

    [Fact]
    public void EndBeforeStart_IsRepairedToOneHour()
    {
        var ev = Assert.Single(IcsImportParser.Parse(Calendar("DTSTART:20261105T090000", "DTEND:20261105T080000", "SUMMARY:Broken")));
        Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0), ev.EndTime);
    }

    [Fact]
    public void UnparseableEnd_FallsBackToOneHour()
    {
        var ev = Assert.Single(IcsImportParser.Parse(Calendar("DTSTART:20261105T090000", "DTEND:not-a-date")));
        Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0), ev.EndTime);
    }

    [Theory]
    [InlineData("DTSTART:")]
    [InlineData("DTSTART;VALUE=DATE:2026-11-05")]
    [InlineData("DTSTART:20261340")]
    public void EventWithEmptyOrInvalidStart_IsSkipped(string dtstart) =>
        Assert.Empty(IcsImportParser.Parse(Calendar(dtstart, "SUMMARY:Ignored")));

    [Fact]
    public void LinesWithoutColonOrName_AndParametersWithoutEquals_AreIgnored()
    {
        var events = IcsImportParser.Parse(Calendar(
            "this line has no colon",
            ":value without a property name",
            "DTSTART;GARBAGE;VALUE=DATE:20261105",
            "SUMMARY;LANGUAGE:Exam",
            "RRULE:FREQ=WEEKLY"));

        var ev = Assert.Single(events);
        Assert.Equal(new DateTime(2026, 11, 5), ev.StartTime);
        Assert.Equal("Exam", ev.Title);
        Assert.True(ev.HasUnexpandedRecurrence);
    }

    [Fact]
    public void EndVeventWithoutBegin_IsIgnored() =>
        Assert.Empty(IcsImportParser.Parse("BEGIN:VCALENDAR\r\nDTSTART:20261105T090000\r\nEND:VEVENT\r\nEND:VCALENDAR"));
}
