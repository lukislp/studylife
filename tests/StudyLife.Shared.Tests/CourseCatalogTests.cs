using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

public class CalcTotalEctsTests
{
    private static CourseDto Ungrouped(int id, int ects) => new() { Id = id, Ects = ects };

    private static CourseDto Grouped(int id, string group, int ects) =>
        new() { Id = id, Group = group, Ects = ects };

    [Fact]
    public void EmptyCourseList_StillCountsTheFourFixedGroupQuotas()
    {
        // CalcTotalEcts adds GroupEctsQuotas.Values.Sum() unconditionally - it never checks
        // whether any course in `courses` actually belongs to those groups. 5+10+10+30 = 55.
        var result = CourseCatalog.CalcTotalEcts(Array.Empty<CourseDto>());

        Assert.Equal(55, result);
    }

    [Fact]
    public void OnlyUngroupedCourses_SumsThemPlusTheFixedGroupQuotas()
    {
        var courses = new[] { Ungrouped(1, 5), Ungrouped(2, 10) };

        var result = CourseCatalog.CalcTotalEcts(courses);

        Assert.Equal(15 + 55, result);
    }

    [Fact]
    public void GroupedCourses_ContributeOnlyTheFixedQuota_RegardlessOfActualEctsSum()
    {
        // 3 courses of 5 ECTS each in a 5-ECTS quota group (15 total) still only count as 5
        // towards the total, because CalcTotalEcts uses the static quota, not the group's sum.
        var courses = new[]
        {
            Grouped(1, "Wahlpflichtmodule A (5 ECTS)", 5),
            Grouped(2, "Wahlpflichtmodule A (5 ECTS)", 5),
            Grouped(3, "Wahlpflichtmodule A (5 ECTS)", 5),
        };

        var result = CourseCatalog.CalcTotalEcts(courses);

        Assert.Equal(55, result); // ungrouped=0 + fixed quotas sum (group content is irrelevant here)
    }

    [Fact]
    public void AppliedAICourses_TotalsTheDocumented180Ects()
    {
        var result = CourseCatalog.CalcTotalEcts(CourseCatalog.AppliedAICourses);

        Assert.Equal(180, result);
    }
}

public class CalcEctsEarnedTests
{
    private static CourseDto Ungrouped(int id, int ects) => new() { Id = id, Ects = ects };

    private static CourseDto Grouped(int id, string group, int ects) =>
        new() { Id = id, Group = group, Ects = ects };

    [Fact]
    public void EmptyCompletedIds_ReturnsZero()
    {
        var courses = new[] { Ungrouped(1, 5), Grouped(2, "Wahlpflichtmodule A (5 ECTS)", 5) };

        var result = CourseCatalog.CalcEctsEarned(courses, Array.Empty<int>());

        Assert.Equal(0, result);
    }

    [Fact]
    public void CompletingEveryAppliedAICourse_EarnedEqualsTotal()
    {
        var courses = CourseCatalog.AppliedAICourses;
        var allIds = courses.Select(c => c.Id);

        var earned = CourseCatalog.CalcEctsEarned(courses, allIds);
        var total = CourseCatalog.CalcTotalEcts(courses);

        Assert.Equal(180, earned);
        Assert.Equal(total, earned);
    }

    [Fact]
    public void CompletingAllCoursesInAGroup_CapsAtGroupQuota_DoesNotOverCount()
    {
        // 3 courses of 5 ECTS in a 5-ECTS-quota group = 15 raw ECTS, but only 5 may be earned.
        var courses = new[]
        {
            Grouped(1, "Wahlpflichtmodule A (5 ECTS)", 5),
            Grouped(2, "Wahlpflichtmodule A (5 ECTS)", 5),
            Grouped(3, "Wahlpflichtmodule A (5 ECTS)", 5),
        };

        var earned = CourseCatalog.CalcEctsEarned(courses, new[] { 1, 2, 3 });

        Assert.Equal(5, earned);
    }

    [Fact]
    public void CompletingSomeCoursesInAGroup_StillCapsAtQuota_EvenBelowFullGroup()
    {
        // Completing only 2 of the 3 courses (10 raw ECTS) still exceeds the 5-ECTS quota.
        var courses = new[]
        {
            Grouped(1, "Wahlpflichtmodule A (5 ECTS)", 5),
            Grouped(2, "Wahlpflichtmodule A (5 ECTS)", 5),
            Grouped(3, "Wahlpflichtmodule A (5 ECTS)", 5),
        };

        var earned = CourseCatalog.CalcEctsEarned(courses, new[] { 1, 2 });

        Assert.Equal(5, earned);
    }

    [Fact]
    public void CompletingOneCourse_BelowQuota_CountsInFull()
    {
        var courses = new[]
        {
            Grouped(1, "Wahlpflichtmodule B (10 ECTS)", 5),
            Grouped(2, "Wahlpflichtmodule B (10 ECTS)", 5),
        };

        var earned = CourseCatalog.CalcEctsEarned(courses, new[] { 1 });

        Assert.Equal(5, earned);
    }

    [Fact]
    public void GroupNotInQuotaDictionary_CountsFully_NoCap()
    {
        // A group name that isn't one of the 4 known quota entries falls back to the raw sum
        // (TryGetValue miss -> quota defaults to `earned`), so nothing is capped.
        var courses = new[]
        {
            Grouped(1, "Some Custom Group", 5),
            Grouped(2, "Some Custom Group", 5),
        };

        var earned = CourseCatalog.CalcEctsEarned(courses, new[] { 1, 2 });

        Assert.Equal(10, earned);
    }

    [Fact]
    public void MultipleGroupsCompletedSimultaneously_EachCappedIndependently()
    {
        var courses = new[]
        {
            Grouped(1, "Wahlpflichtmodule A (5 ECTS)", 5),
            Grouped(2, "Wahlpflichtmodule A (5 ECTS)", 5), // group A: 2x5=10, capped to 5
            Grouped(3, "Wahlpflichtmodule B (10 ECTS)", 5),
            Grouped(4, "Wahlpflichtmodule B (10 ECTS)", 5), // group B: 2x5=10, exactly at quota
            Ungrouped(5, 5),
        };

        var earned = CourseCatalog.CalcEctsEarned(courses, new[] { 1, 2, 3, 4, 5 });

        Assert.Equal(5 + 10 + 5, earned);
    }

    [Fact]
    public void UnknownCompletedId_NotInCatalog_IsIgnored()
    {
        var courses = new[] { Ungrouped(1, 5) };

        var earned = CourseCatalog.CalcEctsEarned(courses, new[] { 1, 999 });

        Assert.Equal(5, earned);
    }
}

/// <summary>
/// The program-aware CalcEctsEarned overload that takes an explicit quota dictionary
/// (used for custom study programs, where quotas come from CourseGroupEntity rows instead
/// of the static GroupEctsQuotas).
/// </summary>
public class CalcEctsEarnedWithQuotaDictionaryTests
{
    private static CourseDto Ungrouped(int id, int ects) => new() { Id = id, Ects = ects };

    private static CourseDto Grouped(int id, string group, int ects) =>
        new() { Id = id, Group = group, Ects = ects };

    [Fact]
    public void CompletedGroupExceedingItsQuota_IsCappedAtTheDictionaryQuota()
    {
        var courses = new[]
        {
            Grouped(1, "Electives", 5),
            Grouped(2, "Electives", 5),
            Grouped(3, "Electives", 5), // 15 raw ECTS completed, quota caps at 10
        };
        var quotas = new Dictionary<string, int> { ["Electives"] = 10 };

        var earned = CourseCatalog.CalcEctsEarned(courses, new[] { 1, 2, 3 }, quotas);

        Assert.Equal(10, earned);
    }

    [Fact]
    public void CompletedGroupBelowItsQuota_CountsTheRawSum()
    {
        var courses = new[] { Grouped(1, "Electives", 5), Grouped(2, "Electives", 5) };
        var quotas = new Dictionary<string, int> { ["Electives"] = 20 };

        var earned = CourseCatalog.CalcEctsEarned(courses, new[] { 1 }, quotas);

        Assert.Equal(5, earned); // min(5 earned, 20 quota)
    }

    [Fact]
    public void GroupWithoutAQuotaEntry_CountsInFull_NoCap()
    {
        // Documented contract: "Groups without a quota entry count in full."
        var courses = new[] { Grouped(1, "Unquoted Group", 6), Grouped(2, "Unquoted Group", 6) };

        var earned = CourseCatalog.CalcEctsEarned(courses, new[] { 1, 2 }, new Dictionary<string, int>());

        Assert.Equal(12, earned);
    }

    [Fact]
    public void MixedUngroupedAndMultipleGroups_EachGroupCappedIndependently()
    {
        var courses = new[]
        {
            Ungrouped(1, 5),
            Grouped(2, "A", 5),
            Grouped(3, "A", 5), // group A: 10 raw, capped to 5
            Grouped(4, "B", 5), // group B: 5 raw, quota 10 -> counts 5
        };
        var quotas = new Dictionary<string, int> { ["A"] = 5, ["B"] = 10 };

        var earned = CourseCatalog.CalcEctsEarned(courses, new[] { 1, 2, 3, 4 }, quotas);

        Assert.Equal(5 + 5 + 5, earned);
    }

    [Fact]
    public void UncompletedGroupedCourses_DoNotContributeTowardsTheQuota()
    {
        var courses = new[] { Grouped(1, "A", 5), Grouped(2, "A", 5) };
        var quotas = new Dictionary<string, int> { ["A"] = 10 };

        var earned = CourseCatalog.CalcEctsEarned(courses, Array.Empty<int>(), quotas);

        Assert.Equal(0, earned);
    }
}

/// <summary>
/// The program-aware CalcTotalEcts overload with an explicit quota dictionary - the
/// counterpart of the earned calculation above.
/// </summary>
public class CalcTotalEctsWithQuotaDictionaryTests
{
    private static CourseDto Grouped(int id, string group, int ects) =>
        new() { Id = id, Group = group, Ects = ects };

    [Fact]
    public void GroupWithQuota_ContributesTheQuotaNotTheRawSum()
    {
        var courses = new[]
        {
            new CourseDto { Id = 1, Ects = 5 },
            Grouped(2, "Electives", 5),
            Grouped(3, "Electives", 5),
        };
        var quotas = new Dictionary<string, int> { ["Electives"] = 8 };

        Assert.Equal(5 + 8, CourseCatalog.CalcTotalEcts(courses, quotas));
    }

    [Fact]
    public void GroupWithoutQuotaEntry_ContributesItsFullSum()
    {
        var courses = new[] { Grouped(1, "Unquoted", 6), Grouped(2, "Unquoted", 4) };

        Assert.Equal(10, CourseCatalog.CalcTotalEcts(courses, new Dictionary<string, int>()));
    }
}
