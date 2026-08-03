using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Pure unit tests of the .ics parser (no CustomWebApplicationFactory needed - IcsImportParser
/// is a stateless static method). Covers the task scope: floating vs. UTC DTSTART, missing
/// DESCRIPTION, escaping, line folding, and the deliberate non-expansion of RRULE (only the
/// first occurrence + HasUnexpandedRecurrence=true).
/// </summary>
public class IcsImportParserTests
{
    [Fact]
    public void Parse_ThreeEventsWithMixedFieldsAndTimezones_ParsesAllCorrectly()
    {
        var ics = "BEGIN:VCALENDAR\r\n"
            + "VERSION:2.0\r\n"
            + "PRODID:-//Test University//Lectures//EN\r\n"
            + "BEGIN:VEVENT\r\n"
            + "UID:1@uni.example\r\n"
            + "DTSTAMP:20260101T000000Z\r\n"
            + "DTSTART:20260115T090000\r\n"
            + "DTEND:20260115T103000\r\n"
            + "SUMMARY:Analysis 1\r\n"
            + "DESCRIPTION:Kapitel 3\\, Ableitungen\\nBitte Skript mitbringen\r\n"
            + "END:VEVENT\r\n"
            + "BEGIN:VEVENT\r\n"
            + "UID:2@uni.example\r\n"
            + "DTSTAMP:20260101T000000Z\r\n"
            + "DTSTART:20260116T130000Z\r\n"
            + "DTEND:20260116T143000Z\r\n"
            + "SUMMARY:Lineare Algebra\r\n"
            + "END:VEVENT\r\n"
            + "BEGIN:VEVENT\r\n"
            + "UID:3@uni.example\r\n"
            + "DTSTAMP:20260101T000000Z\r\n"
            + "DTSTART:20260120T080000\r\n"
            + "DTEND:20260120T093000\r\n"
            + "SUMMARY:Wöchentliche Vorlesung\r\n"
            + "RRULE:FREQ=WEEKLY;BYDAY=TU;UNTIL=20260601T000000Z\r\n"
            + "END:VEVENT\r\n"
            + "END:VCALENDAR\r\n";

        var events = IcsImportParser.Parse(ics);

        Assert.Equal(3, events.Count);

        // Event 1: floating (naive local) time - taken over directly, no conversion.
        var e1 = events[0];
        Assert.Equal("Analysis 1", e1.Title);
        Assert.Equal(new DateTime(2026, 1, 15, 9, 0, 0), e1.StartTime);
        Assert.Equal(new DateTime(2026, 1, 15, 10, 30, 0), e1.EndTime);
        Assert.Equal("Kapitel 3, Ableitungen\nBitte Skript mitbringen", e1.Description);
        Assert.False(e1.HasUnexpandedRecurrence);

        // Event 2: UTC (Z suffix), no DESCRIPTION present -> null instead of exception.
        var e2 = events[1];
        Assert.Equal("Lineare Algebra", e2.Title);
        Assert.Null(e2.Description);
        Assert.False(e2.HasUnexpandedRecurrence);
        var expectedStartUtc = DateTime.SpecifyKind(new DateTime(2026, 1, 16, 13, 0, 0), DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(expectedStartUtc, e2.StartTime);

        // Event 3: RRULE present -> only the first occurrence, clearly marked instead of silently expanded.
        var e3 = events[2];
        Assert.Equal("Wöchentliche Vorlesung", e3.Title);
        Assert.Equal(new DateTime(2026, 1, 20, 8, 0, 0), e3.StartTime);
        Assert.True(e3.HasUnexpandedRecurrence);
    }

    [Fact]
    public void Parse_FoldedDescriptionLine_UnfoldsBeforeParsing()
    {
        // RFC 5545: a continuation line starts with exactly one space and logically belongs
        // to the previous line - "Ableitungen" must not end up here as its own, broken
        // property line.
        var ics = "BEGIN:VCALENDAR\r\n"
            + "BEGIN:VEVENT\r\n"
            + "DTSTART:20260201T100000\r\n"
            + "DTEND:20260201T110000\r\n"
            + "SUMMARY:Seminar\r\n"
            + "DESCRIPTION:Lange Beschreibung über\r\n"
            + " mehrere gefaltete Zeilen hinweg\r\n"
            + "END:VEVENT\r\n"
            + "END:VCALENDAR\r\n";

        var events = IcsImportParser.Parse(ics);

        var e = Assert.Single(events);
        Assert.Equal("Lange Beschreibung übermehrere gefaltete Zeilen hinweg", e.Description);
    }

    [Fact]
    public void Parse_AllDayEventValueDate_ImportsFullDaySpan()
    {
        var ics = "BEGIN:VCALENDAR\r\n"
            + "BEGIN:VEVENT\r\n"
            + "DTSTART;VALUE=DATE:20260301\r\n"
            + "DTEND;VALUE=DATE:20260302\r\n"
            + "SUMMARY:Prüfungswoche\r\n"
            + "END:VEVENT\r\n"
            + "END:VCALENDAR\r\n";

        var events = IcsImportParser.Parse(ics);

        var e = Assert.Single(events);
        Assert.Equal(new DateTime(2026, 3, 1), e.StartTime);
        Assert.Equal(new DateTime(2026, 3, 2), e.EndTime);
    }

    [Fact]
    public void Parse_MissingDtStart_SkipsEventInsteadOfThrowing()
    {
        var ics = "BEGIN:VCALENDAR\r\n"
            + "BEGIN:VEVENT\r\n"
            + "SUMMARY:Kaputtes Event ohne Start\r\n"
            + "END:VEVENT\r\n"
            + "BEGIN:VEVENT\r\n"
            + "DTSTART:20260210T100000\r\n"
            + "DTEND:20260210T110000\r\n"
            + "SUMMARY:Gültiges Event\r\n"
            + "END:VEVENT\r\n"
            + "END:VCALENDAR\r\n";

        var events = IcsImportParser.Parse(ics);

        var e = Assert.Single(events);
        Assert.Equal("Gültiges Event", e.Title);
    }

    [Fact]
    public void Parse_NoDtEnd_DefaultsToOneHourDuration()
    {
        var ics = "BEGIN:VCALENDAR\r\n"
            + "BEGIN:VEVENT\r\n"
            + "DTSTART:20260210T100000\r\n"
            + "SUMMARY:Ohne Ende\r\n"
            + "END:VEVENT\r\n"
            + "END:VCALENDAR\r\n";

        var events = IcsImportParser.Parse(ics);

        var e = Assert.Single(events);
        Assert.Equal(new DateTime(2026, 2, 10, 10, 0, 0), e.StartTime);
        Assert.Equal(new DateTime(2026, 2, 10, 11, 0, 0), e.EndTime);
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsEmptyList()
    {
        var events = IcsImportParser.Parse("");
        Assert.Empty(events);
    }
}
