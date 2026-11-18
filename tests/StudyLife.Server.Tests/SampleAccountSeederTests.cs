// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Server.Services.SampleData;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// SampleData:SeedUsername (SampleAccountSeeder + SampleDataset): the sample dataset goes into
/// exactly one existing password account, only while it has no study data, and touches nothing
/// else. Each fact creates its own account on the shared factory's temp DB.
/// </summary>
public class SampleAccountSeederTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SampleAccountSeederTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.CreateClient(); // boots the host: migrations run against the temp DB
    }

    private static List<CourseDto> ActiveCourses =>
        CourseCatalog.AppliedAICourses.Where(c => c.Semester == 2 && !c.Code.EndsWith("P")).ToList();

    private Task<int> CreatePasswordAccountAsync(string username, bool withSettings = false) =>
        _factory.WithDbAsync(async db =>
        {
            var user = new AuthUserEntity { DisplayName = "Sample " + username, CreatedAt = DateTime.UtcNow };
            db.AuthUsers.Add(user);
            await db.SaveChangesAsync();
            db.PasswordCredentials.Add(new PasswordCredentialEntity
            {
                AuthUserId = user.Id,
                Username = username,
                NormalizedUsername = username.ToLowerInvariant(),
                PasswordHash = "not-a-real-hash",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            if (withSettings)
                db.Settings.Add(new UserSettingsEntity { AuthUserId = user.Id, Theme = "light" });
            await db.SaveChangesAsync();
            return user.Id;
        });

    private Task<SampleAccountSeedOutcome> SeedAsync(string username, DateTime now) =>
        _factory.WithDbAsync(db => SampleAccountSeeder.SeedAsync(db, username, now));

    private Task<(int Sessions, int Notes, int Goals, int Settings)> CountAsync(int userId) =>
        _factory.WithDbAsync(async db => (
            await db.Sessions.IgnoreQueryFilters().CountAsync(s => s.AuthUserId == userId),
            await db.Notes.IgnoreQueryFilters().CountAsync(n => n.AuthUserId == userId),
            await db.CourseGoals.IgnoreQueryFilters().CountAsync(g => g.AuthUserId == userId),
            await db.Settings.IgnoreQueryFilters().CountAsync(s => s.AuthUserId == userId)));

    [Fact]
    public async Task Seed_FillsTheAccount_WithDatesRelativeToNow()
    {
        var userId = await CreatePasswordAccountAsync("sample.fill");
        var now = DateTime.Now;

        Assert.Equal(SampleAccountSeedOutcome.Seeded, await SeedAsync("sample.fill", now));

        var (sessions, notes, goals, settings) = await CountAsync(userId);
        Assert.True(sessions > 20, $"expected weeks of sessions, got {sessions}");
        Assert.Equal(3, notes);
        Assert.Equal(7, goals);
        Assert.Equal(1, settings);

        var planned = await _factory.WithDbAsync(db => db.Sessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.AuthUserId == userId && !s.IsCompleted).ToListAsync());
        Assert.Equal(4, planned.Count);
        Assert.All(planned, s => Assert.True(s.StartTime > now, "planned sessions lie in the future"));

        var completed = await _factory.WithDbAsync(db => db.Sessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.AuthUserId == userId && s.IsCompleted).ToListAsync());
        Assert.All(completed, s => Assert.True(s.EndTime <= now, "completed sessions lie in the past"));
        // Live streak: every one of the last 12 days before today has a completed session.
        for (var d = 1; d <= 12; d++)
            Assert.Contains(completed, s => s.StartTime.Date == now.Date.AddDays(-d));

        var upcomingGoals = await _factory.WithDbAsync(db => db.CourseGoals.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.AuthUserId == userId && g.CompletedAt == null).ToListAsync());
        Assert.Equal(2, upcomingGoals.Count);
        Assert.All(upcomingGoals, g => Assert.True(g.TargetDate > now));
    }

    [Fact]
    public async Task Seed_UpdatesAnExistingSettingsRow_InsteadOfAddingASecond()
    {
        var userId = await CreatePasswordAccountAsync("sample.settings", withSettings: true);

        Assert.Equal(SampleAccountSeedOutcome.Seeded, await SeedAsync("sample.settings", DateTime.Now));

        var settings = Assert.Single(await _factory.WithDbAsync(db => db.Settings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.AuthUserId == userId).ToListAsync()));
        Assert.Equal(string.Join(",", ActiveCourses.Select(c => c.Id)), settings.SelectedCourseIds);
        Assert.Equal("light", settings.Theme); // the account's own choices stay
    }

    [Fact]
    public async Task Seed_IsIdempotent_SecondCallWritesNothing()
    {
        var userId = await CreatePasswordAccountAsync("sample.twice");
        Assert.Equal(SampleAccountSeedOutcome.Seeded, await SeedAsync("sample.twice", DateTime.Now));
        var first = await CountAsync(userId);

        Assert.Equal(SampleAccountSeedOutcome.AccountHasData, await SeedAsync("sample.twice", DateTime.Now));

        Assert.Equal(first, await CountAsync(userId));
    }

    [Fact]
    public async Task Seed_LeavesAnAccountWithOwnData_Untouched()
    {
        var userId = await CreatePasswordAccountAsync("sample.own");
        await _factory.WithDbAsync(async db =>
        {
            db.Notes.Add(new NoteEntity
            {
                AuthUserId = userId,
                CourseId = 1,
                Title = "mine",
                Content = "own note",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            });
            await db.SaveChangesAsync();
        });

        Assert.Equal(SampleAccountSeedOutcome.AccountHasData, await SeedAsync("sample.own", DateTime.Now));

        Assert.Equal((0, 1, 0, 0), await CountAsync(userId));
    }

    [Fact]
    public async Task Seed_MatchesTheUsernameCaseInsensitively_AfterTrimming()
    {
        var userId = await CreatePasswordAccountAsync("sample.case");

        Assert.Equal(SampleAccountSeedOutcome.Seeded, await SeedAsync("  Sample.CASE ", DateTime.Now));

        Assert.Equal(3, (await CountAsync(userId)).Notes);
    }

    [Theory]
    [InlineData("nobody.here")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Seed_UnknownOrBlankUsername_WritesNothing(string username)
    {
        var before = await _factory.WithDbAsync(db => db.Notes.IgnoreQueryFilters().CountAsync());

        Assert.Equal(SampleAccountSeedOutcome.AccountNotFound, await SeedAsync(username, DateTime.Now));

        Assert.Equal(before, await _factory.WithDbAsync(db => db.Notes.IgnoreQueryFilters().CountAsync()));
    }

    [Fact]
    public async Task Seed_German_IsTheDefault_AndUsesTheBuiltInProgram()
    {
        var userId = await CreatePasswordAccountAsync("sample.german");

        Assert.Equal(SampleAccountSeedOutcome.Seeded, await SeedAsync("sample.german", DateTime.Now));

        var settings = await _factory.WithDbAsync(db => db.Settings.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(s => s.AuthUserId == userId));
        Assert.Null(settings.ActiveStudyProgramId);
        Assert.False(settings.BuiltInProgramDismissed);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.StudyPrograms.IgnoreQueryFilters().CountAsync(p => p.AuthUserId == userId)));
        var notes = await _factory.WithDbAsync(db => db.Notes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.AuthUserId == userId).Select(n => n.Title).ToListAsync());
        Assert.Contains("Merkzettel Eigenwerte", notes);
    }

    [Fact]
    public async Task Seed_English_CreatesTheEnglishProgram_AndEverythingPointsAtIt()
    {
        var userId = await CreatePasswordAccountAsync("sample.english");
        var now = DateTime.Now;

        Assert.Equal(SampleAccountSeedOutcome.Seeded,
            await _factory.WithDbAsync(db => SampleAccountSeeder.SeedAsync(db, "sample.english", now, SampleDataLanguage.English)));

        var (program, courses, settings, sessions, goals, notes) = await _factory.WithDbAsync(async db => (
            await db.StudyPrograms.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.AuthUserId == userId),
            await db.CustomCourses.IgnoreQueryFilters().AsNoTracking().Where(c => c.AuthUserId == userId).ToListAsync(),
            await db.Settings.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.AuthUserId == userId),
            await db.Sessions.IgnoreQueryFilters().AsNoTracking().Where(s => s.AuthUserId == userId).ToListAsync(),
            await db.CourseGoals.IgnoreQueryFilters().AsNoTracking().Where(g => g.AuthUserId == userId).ToListAsync(),
            await db.Notes.IgnoreQueryFilters().AsNoTracking().Where(n => n.AuthUserId == userId).ToListAsync()));

        // The account's own English program is active; the German built-in one is hidden.
        Assert.Equal(EnglishSampleProgram.Name, program.Name);
        Assert.Equal(program.Id, settings.ActiveStudyProgramId);
        Assert.True(settings.BuiltInProgramDismissed);
        Assert.Equal(EnglishSampleProgram.Program.Courses.Count, courses.Count);
        Assert.Equal(180, courses.Sum(c => c.Ects));

        // Every reference uses the program's (offset) course ids and English names and topics.
        var byApiId = courses.ToDictionary(c => StudyProgramCatalog.CustomCourseIdOffset + c.Id);
        var englishNames = EnglishSampleProgram.Program.Courses.Select(c => c.Name).ToHashSet();
        var englishTopics = EnglishSampleProgram.Program.Courses.SelectMany(c => c.Topics).ToHashSet();
        Assert.All(settings.SelectedCourseIds.Split(','), id => Assert.Contains(int.Parse(id), byApiId.Keys));
        Assert.All(settings.CompletedCourseIds.Split(','), id => Assert.Equal(1, byApiId[int.Parse(id)].Semester));
        Assert.Equal(4, settings.SelectedCourseIds.Split(',').Length);
        Assert.All(sessions, s =>
        {
            Assert.Equal(byApiId[s.CourseId].Name, s.CourseName);
            Assert.Contains(s.CourseName, englishNames);
            Assert.Contains(s.Topic!, englishTopics);
        });
        Assert.All(goals, g => Assert.Equal(byApiId[g.CourseId].Name, g.CourseName));
        Assert.All(notes, n => Assert.Contains(n.CourseId!.Value, byApiId.Keys));

        // Same structure as the German dataset, English texts.
        Assert.Equal(7, goals.Count);
        Assert.Equal(4, sessions.Count(s => !s.IsCompleted && s.StartTime > now));
        Assert.Equal(
            SampleDataContent.English.Notes.Select(n => n.Title).OrderBy(t => t),
            notes.Select(n => n.Title).OrderBy(t => t));
        Assert.Contains(goals, g => g.CompletionNote == SampleDataContent.English.CompletionNote);
        Assert.DoesNotContain(goals, g => g.CompletionNote == SampleDataContent.German.CompletionNote);
    }

    [Fact]
    public async Task Seed_English_TheApiShowsEnglishCourses()
    {
        var userId = await CreatePasswordAccountAsync("sample.english.api");
        await _factory.WithDbAsync(db => SampleAccountSeeder.SeedAsync(db, "sample.english.api", DateTime.Now, SampleDataLanguage.English));
        var token = await _factory.WithDbAsync(async db =>
        {
            var t = AuthSessionService.IssueSession(db, userId, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return t;
        });
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(AuthSessionService.TokenHeaderName);
        client.DefaultRequestHeaders.Add(AuthSessionService.TokenHeaderName, token);

        var courses = await client.GetStringAsync("/api/courses");

        Assert.Contains("Mathematics: Linear Algebra", courses);
        Assert.DoesNotContain("Mathematik", courses);
    }

    [Fact]
    public async Task Seed_English_IsIdempotent()
    {
        var userId = await CreatePasswordAccountAsync("sample.english.twice");
        await _factory.WithDbAsync(db => SampleAccountSeeder.SeedAsync(db, "sample.english.twice", DateTime.Now, SampleDataLanguage.English));

        Assert.Equal(SampleAccountSeedOutcome.AccountHasData,
            await _factory.WithDbAsync(db => SampleAccountSeeder.SeedAsync(db, "sample.english.twice", DateTime.Now, SampleDataLanguage.English)));

        Assert.Equal(1, await _factory.WithDbAsync(db => db.StudyPrograms.IgnoreQueryFilters().CountAsync(p => p.AuthUserId == userId)));
    }

    [Theory]
    [InlineData(null, SampleDataLanguage.German)]
    [InlineData("", SampleDataLanguage.German)]
    [InlineData("de", SampleDataLanguage.German)]
    [InlineData(" DE ", SampleDataLanguage.German)]
    [InlineData("en", SampleDataLanguage.English)]
    [InlineData("EN", SampleDataLanguage.English)]
    public void ParseLanguage_AcceptsDeAndEn(string? value, SampleDataLanguage expected) =>
        Assert.Equal(expected, SampleDataContent.ParseLanguage(value));

    [Theory]
    [InlineData("fr")]
    [InlineData("english")]
    public void ParseLanguage_RefusesAnythingElse(string value) =>
        Assert.Throws<InvalidOperationException>(() => SampleDataContent.ParseLanguage(value));

    [Fact]
    public void EnglishProgram_MirrorsTheBuiltInStructure_ForTheSeededSemesters()
    {
        // SampleDataset derives completed (semester 1) and active (semester 2 minus projects)
        // courses from the program - the English one must have the same shape there.
        foreach (var semester in new[] { 1, 2 })
        {
            var builtIn = CourseCatalog.AppliedAICourses.Where(c => c.Semester == semester).ToList();
            var english = EnglishSampleProgram.Program.Courses.Where(c => c.Semester == semester).ToList();
            Assert.Equal(builtIn.Select(c => c.Code), english.Select(c => c.Code));
        }
        Assert.All(EnglishSampleProgram.Program.Courses, c => Assert.All(c.Topics, t => Assert.DoesNotContain(",", t)));
    }

    [Fact]
    public async Task Seed_TouchesNoOtherAccount()
    {
        var target = await CreatePasswordAccountAsync("sample.target");
        var bystander = await CreatePasswordAccountAsync("sample.bystander");

        Assert.Equal(SampleAccountSeedOutcome.Seeded, await SeedAsync("sample.target", DateTime.Now));

        Assert.Equal((0, 0, 0, 0), await CountAsync(bystander));
        Assert.True((await CountAsync(target)).Sessions > 0);
    }
}

/// <summary>Startup wiring: with the option set to an account that does not exist yet, the host
/// still starts and writes nothing (the usual first boot, before the account is registered).</summary>
public class SampleAccountSeederStartupTests : IClassFixture<SampleAccountSeederStartupTests.Factory>
{
    public sealed class Factory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("SampleData:SeedUsername", "not.registered.yet");
            builder.UseSetting("SampleData:Language", "en");
        }
    }

    private readonly Factory _factory;

    public SampleAccountSeederStartupTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Startup_WithUnknownAccount_StartsAndWritesNothing()
    {
        var client = _factory.CreateClient();

        var health = await client.GetAsync("/healthz/ready");
        Assert.True(health.IsSuccessStatusCode, $"host did not start cleanly: {health.StatusCode}");
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Notes.IgnoreQueryFilters().CountAsync()));
    }
}
