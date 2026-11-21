// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Server.Services.SampleData;

/// <summary>
/// Weekly and monthly study goals a sample dataset is seeded with. When a caller passes them,
/// <see cref="SampleDataset"/> writes them into the account's settings (unless the account set its
/// own goals, see <see cref="SampleDataset.HasDefaultStudyGoals"/>) and plans the rest of the
/// current week and month up to the minimums (see <see cref="SampleDataset.PlanUpToGoals"/>), so
/// the dashboard's quota tiles read "on track" on every day the dataset is seeded.
/// </summary>
public sealed record SampleStudyGoals(int WeeklyMinHours, int WeeklyMaxHours, int MonthlyMinHours, int MonthlyMaxHours)
{
    /// <summary>
    /// The sample dataset's goals, used by the public demo and by sample accounts
    /// (<see cref="SampleAccountSeeder"/>, e.g. the App Review account). They match the pace of
    /// the seeded history (about 11-12 h a week); the entity defaults (25-30 h a week, 100-130 h a
    /// month) are a full-time load the sample student never studies, so both quota tiles showed
    /// "below target".
    /// </summary>
    public static readonly SampleStudyGoals SamplePace = new(10, 14, 40, 55);
}
