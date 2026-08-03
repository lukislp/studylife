using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class IsStudiedTests
{
    private static StudySessionDto Session(DateTime start, DateTime end, bool isCompleted) =>
        new() { StartTime = start, EndTime = end, IsCompleted = isCompleted };

    [Fact]
    public void ReturnsTrue_WhenCompletedFlagSet_EvenIfEndIsInFuture()
    {
        var now = new DateTime(2026, 7, 13, 10, 0, 0);
        var session = Session(now.AddHours(-1), now.AddHours(2), isCompleted: true);

        Assert.True(StudyMetrics.IsStudied(session, now));
    }

    [Fact]
    public void ReturnsTrue_WhenNotCompleted_ButEndTimeIsInPast()
    {
        var now = new DateTime(2026, 7, 13, 10, 0, 0);
        var session = Session(now.AddHours(-3), now.AddHours(-1), isCompleted: false);

        Assert.True(StudyMetrics.IsStudied(session, now));
    }

    [Fact]
    public void ReturnsFalse_WhenNotCompleted_AndEndTimeIsInFuture()
    {
        var now = new DateTime(2026, 7, 13, 10, 0, 0);
        var session = Session(now.AddHours(-1), now.AddHours(1), isCompleted: false);

        Assert.False(StudyMetrics.IsStudied(session, now));
    }

    [Fact]
    public void ReturnsTrue_WhenEndTimeExactlyEqualsNow()
    {
        var now = new DateTime(2026, 7, 13, 10, 0, 0);
        var session = Session(now.AddHours(-1), now, isCompleted: false);

        Assert.True(StudyMetrics.IsStudied(session, now));
    }

    [Fact]
    public void ReturnsFalse_WhenNotCompleted_AndSessionStartsInFuture()
    {
        var now = new DateTime(2026, 7, 13, 10, 0, 0);
        var session = Session(now.AddHours(1), now.AddHours(2), isCompleted: false);

        Assert.False(StudyMetrics.IsStudied(session, now));
    }
}
