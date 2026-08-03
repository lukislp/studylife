using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class WeekStartOfTests
{
    // Week of 2026-07-13 (Monday) .. 2026-07-19 (Sunday).
    public static IEnumerable<object[]> AllWeekdaysMapToSameMonday()
    {
        var monday = new DateTime(2026, 7, 13);
        for (var i = 0; i < 7; i++)
            yield return new object[] { monday.AddDays(i), monday };
    }

    [Theory]
    [MemberData(nameof(AllWeekdaysMapToSameMonday))]
    public void AllSevenWeekdays_MapToTheirWeeksMonday(DateTime date, DateTime expectedMonday)
    {
        Assert.Equal(expectedMonday, StudyMetrics.WeekStartOf(date));
    }

    [Fact]
    public void MondayItself_MapsToItself()
    {
        var monday = new DateTime(2026, 7, 13);
        Assert.Equal(monday, StudyMetrics.WeekStartOf(monday));
    }

    [Fact]
    public void Sunday_MapsToPrecedingMonday_NotFollowing()
    {
        var sunday = new DateTime(2026, 7, 19);
        var expectedMonday = new DateTime(2026, 7, 13);
        Assert.Equal(expectedMonday, StudyMetrics.WeekStartOf(sunday));
    }

    [Fact]
    public void TimeComponent_IsStrippedFromResult()
    {
        var dateWithTime = new DateTime(2026, 7, 15, 14, 30, 45);
        var expectedMonday = new DateTime(2026, 7, 13);
        Assert.Equal(expectedMonday, StudyMetrics.WeekStartOf(dateWithTime));
    }

    [Fact]
    public void WeekSpanningMonthBoundary_ResolvesCorrectly()
    {
        // 2026-08-01 is a Saturday; its Monday is 2026-07-27.
        var saturday = new DateTime(2026, 8, 1);
        var expectedMonday = new DateTime(2026, 7, 27);
        Assert.Equal(expectedMonday, StudyMetrics.WeekStartOf(saturday));
    }
}
