// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
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
