using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class CountUnlockedAchievementsTests
{
    private static (int Unlocked, int Total) AllZero() => StudyMetrics.CountUnlockedAchievements(
        totalHours: 0, longestStreak: 0, totalSessions: 0, coursesCompleted: 0, allCoursesDone: false,
        earlyBirdCount: 0, nightOwlCount: 0, weekendCount: 0, longestSessionHours: 0,
        perfectWeeks: 0, notesCount: 0, maxCourseDiversity: 0, programsCompleted: 0);

    [Fact]
    public void AllMetricsZero_NothingUnlocked()
    {
        var (unlocked, total) = AllZero();

        Assert.Equal(0, unlocked);
        // 5 hours + 4 streak + 4 sessions + 4 courses + 1 all-courses + 3 early-bird + 3 night-owl +
        // 3 weekend + 3 marathon + 5 perfect-week + 3 notes + 3 diversity + 3 programs = 44 tiers total.
        Assert.Equal(44, total);
    }

    [Fact]
    public void AllMetricsMaxed_EverythingUnlocked()
    {
        var (unlocked, total) = StudyMetrics.CountUnlockedAchievements(
            totalHours: 5000, longestStreak: 500, totalSessions: 2000, coursesCompleted: 50, allCoursesDone: true,
            earlyBirdCount: 200, nightOwlCount: 200, weekendCount: 300, longestSessionHours: 10,
            perfectWeeks: 60, notesCount: 200, maxCourseDiversity: 10, programsCompleted: 5);

        Assert.Equal(total, unlocked);
        Assert.Equal(44, total);
    }

    [Fact]
    public void OnlyHoursThresholdsCounted_WhenOnlyHoursQualify()
    {
        // 500 hours crosses the 25/100/500 tiers (3 of 5 hour tiers), everything else stays at zero.
        var (unlocked, _) = StudyMetrics.CountUnlockedAchievements(
            totalHours: 500, longestStreak: 0, totalSessions: 0, coursesCompleted: 0, allCoursesDone: false,
            earlyBirdCount: 0, nightOwlCount: 0, weekendCount: 0, longestSessionHours: 0,
            perfectWeeks: 0, notesCount: 0, maxCourseDiversity: 0, programsCompleted: 0);

        Assert.Equal(3, unlocked);
    }

    [Fact]
    public void ThresholdIsInclusive_ExactBoundaryCountsAsUnlocked()
    {
        var (unlockedAtBoundary, _) = StudyMetrics.CountUnlockedAchievements(
            totalHours: 25, longestStreak: 0, totalSessions: 0, coursesCompleted: 0, allCoursesDone: false,
            earlyBirdCount: 0, nightOwlCount: 0, weekendCount: 0, longestSessionHours: 0,
            perfectWeeks: 0, notesCount: 0, maxCourseDiversity: 0, programsCompleted: 0);
        var (unlockedJustBelow, _) = StudyMetrics.CountUnlockedAchievements(
            totalHours: 24.99, longestStreak: 0, totalSessions: 0, coursesCompleted: 0, allCoursesDone: false,
            earlyBirdCount: 0, nightOwlCount: 0, weekendCount: 0, longestSessionHours: 0,
            perfectWeeks: 0, notesCount: 0, maxCourseDiversity: 0, programsCompleted: 0);

        Assert.Equal(1, unlockedAtBoundary);
        Assert.Equal(0, unlockedJustBelow);
    }
}
