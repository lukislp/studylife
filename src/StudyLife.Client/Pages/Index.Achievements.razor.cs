using StudyLife.Client.Components.Dashboard;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Index
{
    // Achievements: an "all-time" milestone tracker (see BuildAchievementsAsync). Deliberately its
    // own long-range fetch (~10 years) rather than reusing `history` above, since HistoryDays=400
    // is tuned for month-quota/trend/streak and would undercount total hours/sessions across a
    // multi-year degree.
    private List<DashboardAchievementsCard.Achievement> _achievements = new();
    private int _achievementsUnlocked;
    private const int AchievementHistoryDays = 3650;

    // Total note count, used by the "Notes taken" achievement category - reuses BuildLatestNoteAsync's
    // existing /api/notes fetch instead of firing a second request just for the count.
    private int _notesCount;

    // Count of study programs marked IsCompleted=true (manual flag, see StudyProgramsController),
    // used by the "study programs completed" achievement category below. Fetched fresh via
    // GET api/studyprograms in LoadDataAsync - deliberately NOT gated behind refreshHeavyHistory
    // since it's a small, cheap list endpoint, unlike the ~10-year session history fetch.
    private int _programsCompleted;

    // Achievements (Task 6): substantially expanded from the original 5 categories. _allTimeHistory
    // is already filtered to "studied" sessions (GetHistory defaults to onlyCompleted=true, see the
    // fetch above), so every new category below can use it directly without re-filtering - same
    // assumption the original 5 categories already relied on.
    //
    // activeCourseIds: same Id set LoadDataAsync already builds to scope history/goals/ECTS to the
    // active Studiengang (see its "Aktiver-Studiengang-Scope" comment). settings.CompletedCourseIds
    // spans EVERY programme the user has ever created (it's a flat UserSettings field, not
    // per-programme), so coursesCompleted below must be intersected with it explicitly - unlike
    // _ectsTotal/_ectsEarned (already programme-scoped via the CourseCatalog calls in LoadDataAsync)
    // or _allTimeHistory (already filtered), a raw settings.CompletedCourseIds.Count would silently
    // leak completed-course tallies from other programmes into a freshly switched, empty one.
    private void BuildAchievements(UserSettings settings, HashSet<int> activeCourseIds)
    {
        var totalHours = _allTimeHistory.Sum(s => (s.EndTime - s.StartTime).TotalHours);
        var totalSessions = _allTimeHistory.Count;
        var longestStreak = StudyMetrics.CalcLongestStreak(_allTimeHistory.Select(s => s.StartTime));
        var coursesCompleted = settings.CompletedCourseIds.Count(id => activeCourseIds.Contains(id));
        var allCoursesDone = _ectsTotal > 0 && _ectsEarned >= _ectsTotal;

        var earlyBirdCount = _allTimeHistory.Count(s => s.StartTime.Hour < 7);
        var nightOwlCount = _allTimeHistory.Count(s => s.StartTime.Hour >= 22);
        var weekendCount = _allTimeHistory.Count(s => s.StartTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        var longestSessionHours = _allTimeHistory.Count > 0 ? _allTimeHistory.Max(s => (s.EndTime - s.StartTime).TotalHours) : 0;

        var weeklyGroups = _allTimeHistory.GroupBy(s => StudyMetrics.WeekStartOf(s.StartTime)).ToList();
        var perfectWeeks = settings.WeeklyGoalMinHours > 0
            ? weeklyGroups.Count(g => g.Sum(s => (s.EndTime - s.StartTime).TotalHours) >= settings.WeeklyGoalMinHours)
            : 0;
        var maxCourseDiversity = weeklyGroups.Count > 0
            ? weeklyGroups.Max(g => g.Select(s => s.CourseId).Distinct().Count())
            : 0;

        // Thresholds + unlock computation come from the shared AchievementCatalog (StudyLife.Shared) -
        // this partial only keeps the i18n names/icons/UI. See AchievementCatalog's doc comment for
        // the other two consumers that must stay in sync (StudyMetrics.CountUnlockedAchievements,
        // BackgroundTaskService.Reports's RunAchievementCheckAsync).
        var achievements = new List<DashboardAchievementsCard.Achievement>();
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.HoursTiers, totalHours))
            achievements.Add(new DashboardAchievementsCard.Achievement("⏱", string.Format(T.AchievementHoursName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.StreakTiers, longestStreak))
            achievements.Add(new DashboardAchievementsCard.Achievement("🔥", string.Format(T.AchievementStreakName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.SessionsTiers, totalSessions))
            achievements.Add(new DashboardAchievementsCard.Achievement("✅", string.Format(T.AchievementSessionsName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.CoursesTiers, coursesCompleted))
            achievements.Add(new DashboardAchievementsCard.Achievement("🎓", string.Format(T.AchievementCoursesName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        achievements.Add(new DashboardAchievementsCard.Achievement("🏆", T.AchievementAllCoursesName ?? "", allCoursesDone, allCoursesDone ? 1 : 0, 1));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.EarlyBirdTiers, earlyBirdCount))
            achievements.Add(new DashboardAchievementsCard.Achievement("🌅", string.Format(T.AchievementEarlyBirdName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.NightOwlTiers, nightOwlCount))
            achievements.Add(new DashboardAchievementsCard.Achievement("🦉", string.Format(T.AchievementNightOwlName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.WeekendTiers, weekendCount))
            achievements.Add(new DashboardAchievementsCard.Achievement("🏖", string.Format(T.AchievementWeekendWarriorName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.MarathonTiers, longestSessionHours))
            achievements.Add(new DashboardAchievementsCard.Achievement("🏃", string.Format(T.AchievementMarathonName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.PerfectWeekTiers, perfectWeeks))
            achievements.Add(new DashboardAchievementsCard.Achievement("📅", string.Format(T.AchievementPerfectWeekName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.NotesTiers, _notesCount))
            achievements.Add(new DashboardAchievementsCard.Achievement("📝", string.Format(T.AchievementNotesName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.CourseDiversityTiers, maxCourseDiversity))
            achievements.Add(new DashboardAchievementsCard.Achievement("🎯", string.Format(T.AchievementCourseDiversityName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        // Whole-Studiengang completions (_programsCompleted, fetched via GET api/studyprograms in
        // LoadDataAsync): a much rarer, bigger milestone than finishing one course, so only 3 tiers
        // rather than the 4-5 used above. IsCompleted is a purely manual per-programme flag (see
        // StudyProgramsController), never derived from ECTS, so this is intentionally independent of
        // allCoursesDone/coursesCompleted above.
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.ProgramsTiers, _programsCompleted))
            achievements.Add(new DashboardAchievementsCard.Achievement("🏅", string.Format(T.AchievementProgramsName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));

        _achievements = achievements;
        _achievementsUnlocked = achievements.Count(a => a.Unlocked);
    }
}
