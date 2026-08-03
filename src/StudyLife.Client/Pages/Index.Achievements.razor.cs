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

        var achievements = new List<DashboardAchievementsCard.Achievement>();
        foreach (var t in new[] { 25, 100, 500, 1000, 2000 })
            achievements.Add(new DashboardAchievementsCard.Achievement("⏱", string.Format(T.AchievementHoursName ?? "", t), totalHours >= t, totalHours, t));
        foreach (var t in new[] { 7, 30, 100, 365 })
            achievements.Add(new DashboardAchievementsCard.Achievement("🔥", string.Format(T.AchievementStreakName ?? "", t), longestStreak >= t, longestStreak, t));
        foreach (var t in new[] { 50, 200, 500, 1000 })
            achievements.Add(new DashboardAchievementsCard.Achievement("✅", string.Format(T.AchievementSessionsName ?? "", t), totalSessions >= t, totalSessions, t));
        foreach (var t in new[] { 1, 10, 20, 30 })
            achievements.Add(new DashboardAchievementsCard.Achievement("🎓", string.Format(T.AchievementCoursesName ?? "", t), coursesCompleted >= t, coursesCompleted, t));
        achievements.Add(new DashboardAchievementsCard.Achievement("🏆", T.AchievementAllCoursesName ?? "", allCoursesDone, allCoursesDone ? 1 : 0, 1));
        foreach (var t in new[] { 5, 25, 100 })
            achievements.Add(new DashboardAchievementsCard.Achievement("🌅", string.Format(T.AchievementEarlyBirdName ?? "", t), earlyBirdCount >= t, earlyBirdCount, t));
        foreach (var t in new[] { 5, 25, 100 })
            achievements.Add(new DashboardAchievementsCard.Achievement("🦉", string.Format(T.AchievementNightOwlName ?? "", t), nightOwlCount >= t, nightOwlCount, t));
        foreach (var t in new[] { 10, 50, 150 })
            achievements.Add(new DashboardAchievementsCard.Achievement("🏖", string.Format(T.AchievementWeekendWarriorName ?? "", t), weekendCount >= t, weekendCount, t));
        foreach (var t in new[] { 2, 4, 6 })
            achievements.Add(new DashboardAchievementsCard.Achievement("🏃", string.Format(T.AchievementMarathonName ?? "", t), longestSessionHours >= t, longestSessionHours, t));
        foreach (var t in new[] { 1, 4, 12, 26, 52 })
            achievements.Add(new DashboardAchievementsCard.Achievement("📅", string.Format(T.AchievementPerfectWeekName ?? "", t), perfectWeeks >= t, perfectWeeks, t));
        foreach (var t in new[] { 5, 25, 100 })
            achievements.Add(new DashboardAchievementsCard.Achievement("📝", string.Format(T.AchievementNotesName ?? "", t), _notesCount >= t, _notesCount, t));
        foreach (var t in new[] { 2, 4, 6 })
            achievements.Add(new DashboardAchievementsCard.Achievement("🎯", string.Format(T.AchievementCourseDiversityName ?? "", t), maxCourseDiversity >= t, maxCourseDiversity, t));
        // Whole-Studiengang completions (_programsCompleted, fetched via GET api/studyprograms in
        // LoadDataAsync): a much rarer, bigger milestone than finishing one course, so only 3 tiers
        // rather than the 4-5 used above. IsCompleted is a purely manual per-programme flag (see
        // StudyProgramsController), never derived from ECTS, so this is intentionally independent of
        // allCoursesDone/coursesCompleted above.
        foreach (var t in new[] { 1, 2, 3 })
            achievements.Add(new DashboardAchievementsCard.Achievement("🏅", string.Format(T.AchievementProgramsName ?? "", t), _programsCompleted >= t, _programsCompleted, t));

        _achievements = achievements;
        _achievementsUnlocked = achievements.Count(a => a.Unlocked);
    }
}
