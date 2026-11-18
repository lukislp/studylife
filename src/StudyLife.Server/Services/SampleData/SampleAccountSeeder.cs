// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services.SignIn;

namespace StudyLife.Server.Services.SampleData;

/// <summary>What a sample-data seeding attempt ended in.</summary>
public enum SampleAccountSeedOutcome
{
    /// <summary>The sample dataset was written into the account.</summary>
    Seeded,
    /// <summary>No account signs in with that username (yet) - nothing was written.</summary>
    AccountNotFound,
    /// <summary>The account already holds study data - nothing was written, so a repeated call
    /// (every start while the option stays set) never duplicates or overwrites anything.</summary>
    AccountHasData,
}

/// <summary>
/// Operator feature <c>SampleData:SeedUsername</c> (docs/ARCHITECTURE.md "Sample data for an
/// existing account"): writes <see cref="SampleDataset"/> into ONE existing, normal account,
/// found by its password sign-in username - e.g. to show the app with realistic data on a
/// staging or presentation installation. Unlike demo mode nothing is wiped, no account is
/// created and the instance stays fully writable.
///
/// Idempotent by design: an account that already has any study data (sessions, notes, course
/// goals, own courses or programs) is left alone, so the option can stay configured across
/// restarts. Program.cs never calls this on a demo instance (DemoSeeder owns the data there).
/// <c>SampleData:Language</c> picks German (default, the demo's data) or English.
/// </summary>
public static class SampleAccountSeeder
{
    public static async Task<SampleAccountSeedOutcome> SeedAsync(StudyLifeDb db, string username, DateTime now,
        SampleDataLanguage language = SampleDataLanguage.German)
    {
        var key = UsernamePolicy.Key(UsernamePolicy.Normalize(username));
        if (key.Length == 0) return SampleAccountSeedOutcome.AccountNotFound;

        var userId = await db.PasswordCredentials.AsNoTracking()
            .Where(c => c.NormalizedUsername == key)
            .Select(c => (int?)c.AuthUserId)
            .FirstOrDefaultAsync();
        if (userId is not { } id) return SampleAccountSeedOutcome.AccountNotFound;

        if (await HasStudyDataAsync(db, id)) return SampleAccountSeedOutcome.AccountHasData;

        await SampleDataset.PopulateAsync(db, id, now, language);
        return SampleAccountSeedOutcome.Seeded;
    }

    // IgnoreQueryFilters: this runs at startup without a request user (see DemoSeeder for why the
    // global AuthUserId filters would otherwise hide every row).
    private static async Task<bool> HasStudyDataAsync(StudyLifeDb db, int userId) =>
        await db.Sessions.IgnoreQueryFilters().AnyAsync(s => s.AuthUserId == userId)
        || await db.Notes.IgnoreQueryFilters().AnyAsync(n => n.AuthUserId == userId)
        || await db.CourseGoals.IgnoreQueryFilters().AnyAsync(g => g.AuthUserId == userId)
        || await db.CustomCourses.IgnoreQueryFilters().AnyAsync(c => c.AuthUserId == userId)
        || await db.StudyPrograms.IgnoreQueryFilters().AnyAsync(p => p.AuthUserId == userId);
}
