using StudyLife.Client.Components.Dashboard;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Index
{
    // Achievements: an "all-time" milestone tracker. Deliberately its own long-range fetch
    // (~10 years) rather than reusing the phase-2 history, since HistoryDays=400 is tuned for
    // month-quota/trend/streak and would undercount total hours/sessions across a multi-year degree.
    private List<DashboardAchievementsCard.Achievement> _achievements = new();
    private int _achievementsUnlocked;

    /// <summary>
    /// Turns the builder's category/threshold tiers (13 categories, 44 tiers, computed from the
    /// shared AchievementCatalog and therefore automatically in sync with the metrics API and the
    /// server's push notifier) into the localized card rows. This partial only owns the i18n
    /// names/icons - no thresholds, no unlock logic.
    /// </summary>
    private void ApplyAchievements(DashboardAchievementsDto achievements)
    {
        _achievements = achievements.Tiers
            .Select(t => new DashboardAchievementsCard.Achievement(
                AchievementIcon(t.Category), AchievementName(t.Category, t.Threshold), t.Unlocked, t.Current, t.Threshold))
            .ToList();
        _achievementsUnlocked = achievements.Unlocked;
    }

    private static string AchievementIcon(string category) => category switch
    {
        AchievementCatalog.HoursKey => "⏱",
        AchievementCatalog.StreakKey => "🔥",
        AchievementCatalog.SessionsKey => "✅",
        AchievementCatalog.CoursesKey => "🎓",
        AchievementCatalog.AllCoursesKey => "🏆",
        AchievementCatalog.EarlyBirdKey => "🌅",
        AchievementCatalog.NightOwlKey => "🦉",
        AchievementCatalog.WeekendKey => "🏖",
        AchievementCatalog.MarathonKey => "🏃",
        AchievementCatalog.PerfectWeekKey => "📅",
        AchievementCatalog.NotesKey => "📝",
        AchievementCatalog.CourseDiversityKey => "🎯",
        AchievementCatalog.ProgramsKey => "🏅",
        _ => "",
    };

    /// <summary>The "all courses done" milestone is the only category without a threshold in its
    /// name - it is a single yes/no tier.</summary>
    private string AchievementName(string category, int threshold) => category switch
    {
        AchievementCatalog.HoursKey => string.Format(T.AchievementHoursName ?? "", threshold),
        AchievementCatalog.StreakKey => string.Format(T.AchievementStreakName ?? "", threshold),
        AchievementCatalog.SessionsKey => string.Format(T.AchievementSessionsName ?? "", threshold),
        AchievementCatalog.CoursesKey => string.Format(T.AchievementCoursesName ?? "", threshold),
        AchievementCatalog.AllCoursesKey => T.AchievementAllCoursesName ?? "",
        AchievementCatalog.EarlyBirdKey => string.Format(T.AchievementEarlyBirdName ?? "", threshold),
        AchievementCatalog.NightOwlKey => string.Format(T.AchievementNightOwlName ?? "", threshold),
        AchievementCatalog.WeekendKey => string.Format(T.AchievementWeekendWarriorName ?? "", threshold),
        AchievementCatalog.MarathonKey => string.Format(T.AchievementMarathonName ?? "", threshold),
        AchievementCatalog.PerfectWeekKey => string.Format(T.AchievementPerfectWeekName ?? "", threshold),
        AchievementCatalog.NotesKey => string.Format(T.AchievementNotesName ?? "", threshold),
        AchievementCatalog.CourseDiversityKey => string.Format(T.AchievementCourseDiversityName ?? "", threshold),
        AchievementCatalog.ProgramsKey => string.Format(T.AchievementProgramsName ?? "", threshold),
        _ => "",
    };
}
