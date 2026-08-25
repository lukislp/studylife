using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class AchievementCatalogTests
{
    [Fact]
    public void AllCategories_HaveExpectedTierCounts_TotalingFortyFour()
    {
        // 5+4+4+4+1(allcourses, not a tier array)+3+3+3+3+5+3+3+3 = 44 tiers total (13 categories).
        Assert.Equal(5, AchievementCatalog.HoursTiers.Length);
        Assert.Equal(4, AchievementCatalog.StreakTiers.Length);
        Assert.Equal(4, AchievementCatalog.SessionsTiers.Length);
        Assert.Equal(4, AchievementCatalog.CoursesTiers.Length);
        Assert.Equal(3, AchievementCatalog.EarlyBirdTiers.Length);
        Assert.Equal(3, AchievementCatalog.NightOwlTiers.Length);
        Assert.Equal(3, AchievementCatalog.WeekendTiers.Length);
        Assert.Equal(3, AchievementCatalog.MarathonTiers.Length);
        Assert.Equal(5, AchievementCatalog.PerfectWeekTiers.Length);
        Assert.Equal(3, AchievementCatalog.NotesTiers.Length);
        Assert.Equal(3, AchievementCatalog.CourseDiversityTiers.Length);
        Assert.Equal(3, AchievementCatalog.ProgramsTiers.Length);

        var total = AchievementCatalog.HoursTiers.Length + AchievementCatalog.StreakTiers.Length
            + AchievementCatalog.SessionsTiers.Length + AchievementCatalog.CoursesTiers.Length + 1
            + AchievementCatalog.EarlyBirdTiers.Length + AchievementCatalog.NightOwlTiers.Length
            + AchievementCatalog.WeekendTiers.Length + AchievementCatalog.MarathonTiers.Length
            + AchievementCatalog.PerfectWeekTiers.Length + AchievementCatalog.NotesTiers.Length
            + AchievementCatalog.CourseDiversityTiers.Length + AchievementCatalog.ProgramsTiers.Length;
        Assert.Equal(44, total);
    }

    // Regression test for audit finding D1: the server's copy of these thresholds had silently
    // drifted, truncating exactly these five top tiers (1000h/2000h hours, 365-day streak,
    // 1000 sessions, 30 courses) - a push for each of these badges never fired. Asserting their
    // presence here means any future re-drift in a hand-copied consumer is caught immediately.
    [Fact]
    public void PreviouslyMissingServerTiers_ArePresentInTheCatalog()
    {
        Assert.Contains(1000, AchievementCatalog.HoursTiers);
        Assert.Contains(2000, AchievementCatalog.HoursTiers);
        Assert.Contains(365, AchievementCatalog.StreakTiers);
        Assert.Contains(1000, AchievementCatalog.SessionsTiers);
        Assert.Contains(30, AchievementCatalog.CoursesTiers);
    }

    [Fact]
    public void BuildTiers_ReturnsOneTierPerThreshold_InThresholdOrder()
    {
        var tiers = AchievementCatalog.BuildTiers(AchievementCatalog.HoursTiers, current: 0);

        Assert.Equal(AchievementCatalog.HoursTiers.Length, tiers.Count);
        for (var i = 0; i < tiers.Count; i++)
            Assert.Equal(AchievementCatalog.HoursTiers[i], tiers[i].Threshold);
    }

    [Fact]
    public void BuildTiers_UnlocksTiersAtOrBelowCurrentValue_LeavesHigherTiersLocked()
    {
        // 500 crosses the 25/100/500 tiers but not 1000/2000.
        var tiers = AchievementCatalog.BuildTiers(AchievementCatalog.HoursTiers, current: 500);

        Assert.True(tiers[0].Unlocked); // 25
        Assert.True(tiers[1].Unlocked); // 100
        Assert.True(tiers[2].Unlocked); // 500
        Assert.False(tiers[3].Unlocked); // 1000
        Assert.False(tiers[4].Unlocked); // 2000
        Assert.All(tiers, t => Assert.Equal(500, t.Current));
    }

    [Fact]
    public void BuildTiers_ThresholdIsInclusive_ExactBoundaryUnlocks()
    {
        var atBoundary = AchievementCatalog.BuildTiers(AchievementCatalog.StreakTiers, current: 7);
        var justBelow = AchievementCatalog.BuildTiers(AchievementCatalog.StreakTiers, current: 6.99);

        Assert.True(atBoundary[0].Unlocked);
        Assert.False(justBelow[0].Unlocked);
    }

    [Fact]
    public void BuildTiers_EmptyThresholds_ReturnsEmpty()
    {
        var tiers = AchievementCatalog.BuildTiers(Array.Empty<int>(), current: 100);

        Assert.Empty(tiers);
    }
}
