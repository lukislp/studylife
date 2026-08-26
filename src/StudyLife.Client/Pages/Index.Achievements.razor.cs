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
        // Raw per-category counts: previously computed independently here AND (for the first 5)
        // in BackgroundTaskService.Reports's RunAchievementCheckAsync - both now share one
        // implementation (AchievementCatalog.BuildInputs), also used by the metrics API
        // (MetricsController, GET /api/metrics/achievements).
        var inputs = AchievementCatalog.BuildInputs(
            _allTimeHistory, settings.CompletedCourseIds, activeCourseIds,
            settings.WeeklyGoalMinHours, _ectsTotal, _ectsEarned, _notesCount, _programsCompleted);

        // Thresholds + unlock computation come from the shared AchievementCatalog (StudyLife.Shared) -
        // this partial only keeps the i18n names/icons/UI. See AchievementCatalog's doc comment for
        // the other consumers that must stay in sync (StudyMetrics.CountUnlockedAchievements,
        // BackgroundTaskService.Reports's RunAchievementCheckAsync, MetricsController).
        var achievements = new List<DashboardAchievementsCard.Achievement>();
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.HoursTiers, inputs.TotalHours))
            achievements.Add(new DashboardAchievementsCard.Achievement("⏱", string.Format(T.AchievementHoursName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.StreakTiers, inputs.LongestStreak))
            achievements.Add(new DashboardAchievementsCard.Achievement("🔥", string.Format(T.AchievementStreakName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.SessionsTiers, inputs.TotalSessions))
            achievements.Add(new DashboardAchievementsCard.Achievement("✅", string.Format(T.AchievementSessionsName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.CoursesTiers, inputs.CoursesCompleted))
            achievements.Add(new DashboardAchievementsCard.Achievement("🎓", string.Format(T.AchievementCoursesName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        achievements.Add(new DashboardAchievementsCard.Achievement("🏆", T.AchievementAllCoursesName ?? "", inputs.AllCoursesDone, inputs.AllCoursesDone ? 1 : 0, 1));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.EarlyBirdTiers, inputs.EarlyBirdCount))
            achievements.Add(new DashboardAchievementsCard.Achievement("🌅", string.Format(T.AchievementEarlyBirdName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.NightOwlTiers, inputs.NightOwlCount))
            achievements.Add(new DashboardAchievementsCard.Achievement("🦉", string.Format(T.AchievementNightOwlName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.WeekendTiers, inputs.WeekendCount))
            achievements.Add(new DashboardAchievementsCard.Achievement("🏖", string.Format(T.AchievementWeekendWarriorName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.MarathonTiers, inputs.LongestSessionHours))
            achievements.Add(new DashboardAchievementsCard.Achievement("🏃", string.Format(T.AchievementMarathonName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.PerfectWeekTiers, inputs.PerfectWeeks))
            achievements.Add(new DashboardAchievementsCard.Achievement("📅", string.Format(T.AchievementPerfectWeekName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.NotesTiers, inputs.NotesCount))
            achievements.Add(new DashboardAchievementsCard.Achievement("📝", string.Format(T.AchievementNotesName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.CourseDiversityTiers, inputs.MaxCourseDiversity))
            achievements.Add(new DashboardAchievementsCard.Achievement("🎯", string.Format(T.AchievementCourseDiversityName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));
        // Whole-Studiengang completions (_programsCompleted, fetched via GET api/studyprograms in
        // LoadDataAsync): a much rarer, bigger milestone than finishing one course, so only 3 tiers
        // rather than the 4-5 used above. IsCompleted is a purely manual per-programme flag (see
        // StudyProgramsController), never derived from ECTS, so this is intentionally independent of
        // allCoursesDone/coursesCompleted above.
        foreach (var tier in AchievementCatalog.BuildTiers(AchievementCatalog.ProgramsTiers, inputs.ProgramsCompleted))
            achievements.Add(new DashboardAchievementsCard.Achievement("🏅", string.Format(T.AchievementProgramsName ?? "", tier.Threshold), tier.Unlocked, tier.Current, tier.Threshold));

        _achievements = achievements;
        _achievementsUnlocked = achievements.Count(a => a.Unlocked);
    }
}
