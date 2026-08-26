namespace StudyLife.Client.Models;

public class StudySession
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public string CourseName { get; set; } = "";
    public string CourseColor { get; set; } = "#6C5CE7";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string? Topic { get; set; }
    public string? Notes { get; set; }
    public bool IsCompleted { get; set; }
    public int TimerModeId { get; set; }
    public string? RecurrenceGroupId { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
}

public class UserSettings
{
    public int Id { get; set; }
    /// <summary>Optimistic-concurrency token mirrored from UserSettingsDto.Version (audit S4/S5)
    /// - always the value last seen from the server (GET, or a successful/retried PUT response),
    /// resent on the next SaveSettingsAsync so the server can detect a stale read-modify-write.
    /// See AppStateService.SaveSettingsAsync for the 409 refetch-and-retry-once handling.</summary>
    public int Version { get; set; }
    public string DefaultProgramme { get; set; } = "Applied Artificial Intelligence";
    public List<int> SelectedCourseIds { get; set; } = new();
    public List<int> CompletedCourseIds { get; set; } = new();
    public string Theme { get; set; } = "dark";
    public string AccentColor { get; set; } = "coral"; // preset key, see --accent presets in base.css
    public bool AutoSwitchFocus { get; set; } = true;
    public int AutoSwitchMinutesBefore { get; set; } = 2;
    public string MotivationalStyle { get; set; } = "claude"; // claude, zen, intense, hype
    public string SessionReminderMinutes { get; set; } = "60,30,10,5,3,2,1";
    public string CourseGoalReminderDays { get; set; } = "14,7,3,1,0";
    public int InactivityThresholdDays { get; set; } = 5;
    public int StudyWindowStartHour { get; set; } = 8;
    public int StudyWindowEndHour { get; set; } = 21;
    public string StudyDays { get; set; } = "0,1,2,3,4,5,6";
    public DateTime? TargetGraduationDate { get; set; } // null = desired graduation date disabled
    public string CustomTimerModes { get; set; } = ""; // JSON array of custom timer modes, see the CustomTimerModes helper
    public int WeeklyGoalMinHours { get; set; } = 25; // Weekly study workload, lower bound (default 25h)
    public int WeeklyGoalMaxHours { get; set; } = 30; // Weekly study workload, upper bound (default 30h)
    public int MonthlyGoalMinHours { get; set; } = 100; // Monthly study workload, lower bound (default 100h), independent of the weekly goal
    public int MonthlyGoalMaxHours { get; set; } = 130; // Monthly study workload, upper bound (default 130h), independent of the weekly goal
    public bool SessionRemindersEnabled { get; set; } = true;
    public bool CourseGoalRemindersEnabled { get; set; } = true;
    public bool InactivityRemindersEnabled { get; set; } = true;
    public bool AchievementNotificationsEnabled { get; set; } = true;
    public bool WeeklyReportEnabled { get; set; } = true;
    public bool DailyMotivationEnabled { get; set; } // Daily motivation push, default false (opt-in, new category)
    public bool PerCourseInactivityRemindersEnabled { get; set; } // Nudge for a single neglected course, default false (opt-in, new category)
    public DateTime? LastBackupDownloadAt { get; set; } // Timestamp of the last manual backup download, null = never
    public int? ActiveStudyProgramId { get; set; } // Id of the active custom study program, null = built-in study program
    public bool ProgressShareEnabled { get; set; } // Read-only progress link active? NOT set via SaveSettingsAsync, see SetupProgressShareCard
    public string? ProgressShareToken { get; set; } // Token for /shared/{token}, null = never activated
    public bool StreakRiskRemindersEnabled { get; set; } // Warns about a streak break threatening today, default false (opt-in, new category)
    public bool WeeklyGoalNudgeEnabled { get; set; } // Midweek nudge when falling behind the weekly goal, default false (opt-in, new category)
    public bool CourseAlmostDoneRemindersEnabled { get; set; } // "Almost done" nudge at >=85% topic progress, default false (opt-in, new category)
    public bool BestStudyTimeRemindersEnabled { get; set; } // Reminder before the most productive time of day, default false (opt-in, new category)
    public bool ComebackNudgeEnabled { get; set; } // Gentle comeback hint after exactly 1 day off, default false (opt-in, new category)
    public bool NewRecordNotificationsEnabled { get; set; } // Instant feedback on a new personal record, default false (opt-in, new category)
    public bool MonthlyReportEnabled { get; set; } = true; // Monthly recap push, analogous to WeeklyReportEnabled, default true
}

public class TimerMode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int FocusMinutes { get; set; }
    public int BreakMinutes { get; set; }
    public int Rounds { get; set; }
    public string Style { get; set; } = ""; // pomodoro, flow, ultradian, claude, sprint
    public string Emoji { get; set; } = "⏱";
    public string GradientFrom { get; set; } = "#6C5CE7";
    public string GradientTo { get; set; } = "#A29BFE";
}

/// <summary>
/// Parses/serializes UserSettings.CustomTimerModes (user-defined timer modes).
/// Deliberately JSON instead of the otherwise usual comma-separated strings: mode names are
/// free text and may contain commas/semicolons without breaking the format.
/// IDs start at 100 and therefore never collide with the built-in modes (IDs 1-5).
/// </summary>
public static class CustomTimerModes
{
    public const int FirstCustomId = 100;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Compact storage format: only the 5 user-defined fields, camelCase.</summary>
    private class Entry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int FocusMinutes { get; set; }
        public int BreakMinutes { get; set; }
        public int Rounds { get; set; }
    }

    /// <summary>Tolerant: empty/invalid JSON results in an empty list (fallback style used throughout the codebase).</summary>
    public static List<TimerMode> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<TimerMode>();
        try
        {
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<Entry>>(json, JsonOptions) ?? new();
            return entries
                .Where(e => e.Id >= FirstCustomId && !string.IsNullOrWhiteSpace(e.Name))
                .Select(e => new TimerMode
                {
                    Id = e.Id,
                    Name = e.Name,
                    Description = $"{Math.Clamp(e.FocusMinutes, 5, 180)} minutes focus, {Math.Clamp(e.BreakMinutes, 0, 60)} minute break. Your mode, your rhythm.",
                    FocusMinutes = Math.Clamp(e.FocusMinutes, 5, 180),
                    BreakMinutes = Math.Clamp(e.BreakMinutes, 0, 60),
                    Rounds = Math.Clamp(e.Rounds, 1, 10),
                    Style = "custom",
                    Emoji = "⚙",
                    GradientFrom = "#00B894",
                    GradientTo = "#55EFC4",
                })
                .ToList();
        }
        catch
        {
            return new List<TimerMode>();
        }
    }

    public static string Serialize(IEnumerable<TimerMode> modes) =>
        System.Text.Json.JsonSerializer.Serialize(
            modes.Select(m => new Entry
            {
                Id = m.Id,
                Name = m.Name,
                FocusMinutes = m.FocusMinutes,
                BreakMinutes = m.BreakMinutes,
                Rounds = m.Rounds,
            }),
            JsonOptions);

    /// <summary>Next free id: max(existing custom ids, 99) + 1 - collision-free against the built-ins.</summary>
    public static int NextId(IEnumerable<TimerMode> existing) =>
        Math.Max(FirstCustomId - 1, existing.Select(m => m.Id).DefaultIfEmpty(0).Max()) + 1;

    /// <summary>Built-in modes + parsed custom modes, e.g. for the focus page.</summary>
    public static List<TimerMode> Combined(string? json) =>
        DefaultData.TimerModes.Concat(Parse(json)).ToList();
}

public static class DefaultData
{
    public static List<TimerMode> TimerModes => new()
    {
        new() { Id = 1, Name = "Pomodoro Classic", Description = "25 minutes focus, 5 minute break. The original. Builds rhythm through repetition.", FocusMinutes = 25, BreakMinutes = 5, Rounds = 4, Style = "pomodoro", Emoji = "🍅", GradientFrom = "#E17055", GradientTo = "#D63031" },
        new() { Id = 2, Name = "Flow State", Description = "52 minutes deep work, 17 minute recovery. Based on research into natural attention rhythms.", FocusMinutes = 52, BreakMinutes = 17, Rounds = 3, Style = "flow", Emoji = "🌊", GradientFrom = "#0984E3", GradientTo = "#74B9FF" },
        new() { Id = 3, Name = "Ultradian Rhythm", Description = "90 minute cycles matching your brain's natural peaks. One session, one deep dive.", FocusMinutes = 90, BreakMinutes = 20, Rounds = 2, Style = "ultradian", Emoji = "🌙", GradientFrom = "#6C5CE7", GradientTo = "#A29BFE" },
        new() { Id = 4, Name = "Claude Mode", Description = "Adaptive focus blocks with AI-generated motivation. Your session, your energy.", FocusMinutes = 40, BreakMinutes = 10, Rounds = 3, Style = "claude", Emoji = "✦", GradientFrom = "#CC785C", GradientTo = "#E8A87C" },
        new() { Id = 5, Name = "Sprint Bursts", Description = "10 minute intense micro-sprints with 3 minute resets. For when you need momentum fast.", FocusMinutes = 10, BreakMinutes = 3, Rounds = 6, Style = "sprint", Emoji = "⚡", GradientFrom = "#FDCB6E", GradientTo = "#E17055" },
        new() { Id = 6, Name = "Micro Focus", Description = "5 minutes focus, 1 minute break. Tiny steps for low-energy days or just getting unstuck.", FocusMinutes = 5, BreakMinutes = 1, Rounds = 8, Style = "micro", Emoji = "🌱", GradientFrom = "#00B894", GradientTo = "#55EFC4" },
        new() { Id = 7, Name = "Quick Burst", Description = "15 minutes focus, 3 minute break. More room than a sprint, still fast-paced.", FocusMinutes = 15, BreakMinutes = 3, Rounds = 5, Style = "burst", Emoji = "🔥", GradientFrom = "#FF7675", GradientTo = "#FAB1A0" },
        new() { Id = 8, Name = "Deep Dive", Description = "120 minutes focus, 20 minute break. For when you need to disappear into a problem.", FocusMinutes = 120, BreakMinutes = 20, Rounds = 2, Style = "deepdive", Emoji = "🏔", GradientFrom = "#00CEC9", GradientTo = "#81ECEC" },
        new() { Id = 9, Name = "Marathon Session", Description = "180 minutes focus, 30 minute break. One long, uninterrupted push - exam-prep territory.", FocusMinutes = 180, BreakMinutes = 30, Rounds = 1, Style = "marathon", Emoji = "🎯", GradientFrom = "#E84393", GradientTo = "#FD79A8" },
    };
}
