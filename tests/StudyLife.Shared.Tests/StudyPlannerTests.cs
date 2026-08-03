using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>
/// Slot search and weighting of the planner (StudyPlanner) - deliberately untested until the
/// planner protection rule was lifted (2026-07-19), now part of the coverage target.
/// </summary>
public class StudyPlannerTests
{
    // Fixed reference week: 2026-07-27 is a Monday.
    private static readonly DateTime Monday = new(2026, 7, 27);

    // ── ParseStudyDays ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc,9,-1")] // only invalid tokens → falls back like empty
    public void ParseStudyDays_EmptyOrInvalid_AllowsAllDays(string? csv)
    {
        Assert.Equal(7, StudyPlanner.ParseStudyDays(csv).Count);
    }

    [Fact]
    public void ParseStudyDays_Subset_ParsesDotNetDayOfWeekValues()
    {
        var days = StudyPlanner.ParseStudyDays("1,3,5");
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }, days);
    }

    [Fact]
    public void ParseStudyDays_MixedValidAndInvalid_KeepsOnlyValid()
    {
        var days = StudyPlanner.ParseStudyDays("0,foo,6,7");
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Sunday, DayOfWeek.Saturday }, days);
    }

    // ── FindFreeSlots ────────────────────────────────────────────────────────

    [Fact]
    public void FindFreeSlots_StaysInsideDayWindow()
    {
        var slots = StudyPlanner.FindFreeSlots(Monday, Monday, busyIntervals: new List<(DateTime, DateTime)>(),
            slotLength: TimeSpan.FromMinutes(90), maxSlotsPerDay: 10, maxTotalSlots: 10);

        Assert.NotEmpty(slots);
        Assert.Equal(Monday.AddHours(StudyPlanner.DefaultDayStartHour), slots[0].Start);
        Assert.All(slots, s =>
        {
            Assert.True(s.Start >= Monday.AddHours(StudyPlanner.DefaultDayStartHour));
            Assert.True(s.End <= Monday.AddHours(StudyPlanner.DefaultDayEndHour));
        });
    }

    [Fact]
    public void FindFreeSlots_SkipsBusyIntervalsAndResumesAfterThem()
    {
        var busy = new List<(DateTime, DateTime)> { (Monday.AddHours(8), Monday.AddHours(12)) };

        var slots = StudyPlanner.FindFreeSlots(Monday, Monday, busy,
            TimeSpan.FromMinutes(60), maxSlotsPerDay: 1, maxTotalSlots: 1);

        var slot = Assert.Single(slots);
        Assert.Equal(Monday.AddHours(12), slot.Start); // conflict end, already on a half hour
        Assert.All(slots, s => Assert.DoesNotContain(busy, b => b.Item1 < s.End && b.Item2 > s.Start));
    }

    [Fact]
    public void FindFreeSlots_SkipsDisallowedWeekdays()
    {
        var slots = StudyPlanner.FindFreeSlots(Monday, Monday.AddDays(6), new List<(DateTime, DateTime)>(),
            TimeSpan.FromMinutes(90), maxSlotsPerDay: 2, maxTotalSlots: 20,
            allowedDays: new HashSet<DayOfWeek> { DayOfWeek.Tuesday });

        Assert.NotEmpty(slots);
        Assert.All(slots, s => Assert.Equal(DayOfWeek.Tuesday, s.Start.DayOfWeek));
    }

    [Fact]
    public void FindFreeSlots_HonorsPerDayAndTotalMaxima()
    {
        var slots = StudyPlanner.FindFreeSlots(Monday, Monday.AddDays(4), new List<(DateTime, DateTime)>(),
            TimeSpan.FromMinutes(60), maxSlotsPerDay: 2, maxTotalSlots: 5);

        Assert.Equal(5, slots.Count);
        Assert.All(slots.GroupBy(s => s.Start.Date), g => Assert.True(g.Count() <= 2));
    }

    [Fact]
    public void FindFreeSlots_LateStartTime_IsRoundedUpToNextHalfHour()
    {
        var from = Monday.AddHours(9).AddMinutes(10);

        var slots = StudyPlanner.FindFreeSlots(from, Monday, new List<(DateTime, DateTime)>(),
            TimeSpan.FromMinutes(60), maxSlotsPerDay: 1, maxTotalSlots: 1);

        Assert.Equal(Monday.AddHours(9).AddMinutes(30), Assert.Single(slots).Start);
    }

    [Fact]
    public void FindFreeSlots_LeavesBufferBetweenSlotsOnSameDay()
    {
        var slots = StudyPlanner.FindFreeSlots(Monday, Monday, new List<(DateTime, DateTime)>(),
            TimeSpan.FromMinutes(60), maxSlotsPerDay: 3, maxTotalSlots: 3);

        Assert.Equal(3, slots.Count);
        for (var i = 1; i < slots.Count; i++)
            Assert.True(slots[i].Start >= slots[i - 1].End.AddMinutes(30));
    }

    [Theory]
    [InlineData(0)]   // maxTotalSlots <= 0
    [InlineData(-1)]
    public void FindFreeSlots_NonPositiveTotal_ReturnsEmpty(int maxTotal)
    {
        Assert.Empty(StudyPlanner.FindFreeSlots(Monday, Monday.AddDays(3), new List<(DateTime, DateTime)>(),
            TimeSpan.FromMinutes(60), 3, maxTotal));
    }

    [Fact]
    public void FindFreeSlots_InvalidWindowOrRange_ReturnsEmpty()
    {
        // Daily window with end <= start
        Assert.Empty(StudyPlanner.FindFreeSlots(Monday, Monday, new List<(DateTime, DateTime)>(),
            TimeSpan.FromMinutes(60), 3, 3, dayStartHour: 12, dayEndHour: 12));
        // toDate before fromDate
        Assert.Empty(StudyPlanner.FindFreeSlots(Monday, Monday.AddDays(-1), new List<(DateTime, DateTime)>(),
            TimeSpan.FromMinutes(60), 3, 3));
    }

    // ── WeightedRoundRobin ───────────────────────────────────────────────────

    [Fact]
    public void WeightedRoundRobin_EmptyOrNonPositive_ReturnsEmpty()
    {
        Assert.Empty(StudyPlanner.WeightedRoundRobin(new Dictionary<string, double>(), 5));
        Assert.Empty(StudyPlanner.WeightedRoundRobin(new Dictionary<string, double> { ["a"] = 1 }, 0));
    }

    [Fact]
    public void WeightedRoundRobin_DistributesProportionally()
    {
        var picks = StudyPlanner.WeightedRoundRobin(new Dictionary<string, double> { ["a"] = 2, ["b"] = 1 }, 6);

        Assert.Equal(6, picks.Count);
        Assert.Equal(4, picks.Count(p => p == "a"));
        Assert.Equal(2, picks.Count(p => p == "b"));
    }

    [Fact]
    public void WeightedRoundRobin_InterleavesInsteadOfBlocks()
    {
        // Smooth WRR property: with equal weight, the selection alternates
        // instead of delivering all of A first and then all of B.
        var picks = StudyPlanner.WeightedRoundRobin(new Dictionary<string, double> { ["a"] = 1, ["b"] = 1 }, 4);

        Assert.Equal(4, picks.Count);
        Assert.NotEqual(picks[0], picks[1]);
        Assert.NotEqual(picks[2], picks[3]);
    }
}
