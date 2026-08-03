using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Migration backfill for the multi-tenant foundation: a legacy DB (schema of the predecessor
/// migration, migrated with IMigrator only up to that point) is seeded with "existing data" and
/// then migrated to the current state. Expectation: AddMultiTenantAuthUserFoundation creates
/// exactly ONE AuthUser ("Mein Studium") and assigns all existing rows to them - existing data
/// stays exactly as visible for today's single user.
/// </summary>
public class MultiTenantMigrationBackfillTests : IDisposable
{
    private const string PreviousMigration = "20260715181211_AddNotificationBatch2Toggles";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"studylife-tenantmig-{Guid.NewGuid():N}.db");

    private StudyLifeDb NewContext()
    {
        var options = new DbContextOptionsBuilder<StudyLifeDb>().UseSqlite($"Data Source={_dbPath}").Options;
        return new StudyLifeDb(options, new TestCurrentUserAccessor());
    }

    public void Dispose()
    {
        using (var poolProbe = new SqliteConnection($"Data Source={_dbPath}"))
            SqliteConnection.ClearPool(poolProbe);
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Migration_CreatesSingleAuthUser_AndBackfillsExistingRows()
    {
        using (var db = NewContext())
        {
            // Set up the legacy schema (state BEFORE the multi-tenant rework) and seed existing
            // data - via raw SQL, because the current EF model already knows about the
            // AuthUserId columns, but the legacy tables don't have them yet.
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Sessions (CourseId, CourseName, CourseColor, StartTime, EndTime, IsCompleted, TimerModeId) "
                + "VALUES (7, 'Legacy Course', '#123456', '2026-01-05 10:00:00', '2026-01-05 11:00:00', 1, 1);");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Notes (Title, Content, CreatedAt, UpdatedAt) "
                + "VALUES ('Legacy Note', 'Inhalt', '2026-01-05 10:00:00', '2026-01-05 10:00:00');");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO SentReminders (Key, SentAt) VALUES ('legacy:reminder', '2026-01-05 10:00:00');");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO PushSubscriptions (Endpoint, P256dh, Auth) "
                + "VALUES ('https://push.example.com/legacy', 'p256dh', 'auth');");

            await migrator.MigrateAsync();
        }

        using (var db = NewContext())
        {
            var user = await db.AuthUsers.SingleAsync();
            Assert.Equal("Mein Studium", user.DisplayName);

            // Backfill: every existing row now carries the Id of the single created user ...
            var session = await db.Sessions.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(user.Id, session.AuthUserId);
            var note = await db.Notes.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(user.Id, note.AuthUserId);
            var reminder = await db.SentReminders.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(user.Id, reminder.AuthUserId);
            var subscription = await db.PushSubscriptions.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(user.Id, subscription.AuthUserId);

            // ... and is therefore visible through the global query filters for exactly this
            // user (TestCurrentUserAccessor points to Id 1 = the freshly seeded user).
            Assert.Equal(1, user.Id);
            Assert.Equal("Legacy Course", (await db.Sessions.SingleAsync()).CourseName);
            Assert.Equal("Legacy Note", (await db.Notes.SingleAsync()).Title);
        }
    }
}

/// <summary>
/// Global query filters + HTTP user resolution of the multi-tenant foundation, against the real
/// HTTP stack: data is created via the API as in real operation (the middleware in Program.cs
/// resolves the single AuthUser AFTER the API-key check, NO client header) and must then
/// (1) be stamped with that user's AuthUserId and (2) be invisible in a DIFFERENT user context.
/// </summary>
public class MultiTenantQueryFilterTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MultiTenantQueryFilterTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Both tests share a factory/DB (IClassFixture) - the marker makes the
    /// seeded rows uniquely identifiable per test, instead of relying on a "pristine"
    /// DB (see usage comment in CustomWebApplicationFactory).</summary>
    private async Task SeedViaApiAsync(string marker)
    {
        var session = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 11,
            CourseName = $"Filter Course {marker}",
            CourseColor = "#6C5CE7",
            StartTime = new DateTime(2026, 3, 2, 9, 0, 0),
            EndTime = new DateTime(2026, 3, 2, 10, 0, 0),
            IsCompleted = true,
            TimerModeId = 1,
        });
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        var note = await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = $"Filter Note {marker}",
            Content = "Inhalt",
        });
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);

        var settings = await _client.PutAsJsonAsync("/api/settings", new UserSettingsDto());
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

        var goal = await _client.PutAsJsonAsync("/api/coursegoals/11", new CourseGoalDto
        {
            CourseId = 11,
            CourseName = "Filter Course",
            TargetDate = new DateTime(2026, 6, 1),
        });
        Assert.Equal(HttpStatusCode.OK, goal.StatusCode);
    }

    [Fact]
    public async Task ApiInserts_AreStampedWithTheSingleAuthUsersId()
    {
        await SeedViaApiAsync("stamp");

        await _factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.SingleAsync();
            var session = await db.Sessions.IgnoreQueryFilters()
                .SingleAsync(s => s.CourseName == "Filter Course stamp");
            Assert.Equal(user.Id, session.AuthUserId);
            var note = await db.Notes.IgnoreQueryFilters()
                .SingleAsync(n => n.Title == "Filter Note stamp");
            Assert.Equal(user.Id, note.AuthUserId);
            var settings = await db.Settings.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(user.Id, settings.AuthUserId);
            var goal = await db.CourseGoals.IgnoreQueryFilters().SingleAsync(g => g.CourseId == 11);
            Assert.Equal(user.Id, goal.AuthUserId);
        });
    }

    [Fact]
    public async Task QueryFilters_ReturnNothingForAForeignAuthUserId()
    {
        await SeedViaApiAsync("filter");

        await _factory.WithDbAsync(async db =>
        {
            // Control: in the real user's context, the seeded data is visible.
            Assert.True(await db.Sessions.AnyAsync());
            Assert.True(await db.Notes.AnyAsync());
            Assert.True(await db.Settings.AnyAsync());
            Assert.True(await db.CourseGoals.AnyAsync());

            // In the context of a different/nonexistent user, the global query filters
            // filter out EVERYTHING - same DbContext, just a different AsyncLocal user
            // scope (the filter parameter is re-evaluated on every query execution).
            using (CurrentUserAccessor.BeginBackgroundScope(999))
            {
                Assert.False(await db.Sessions.AnyAsync());
                Assert.False(await db.Notes.AnyAsync());
                Assert.False(await db.Settings.AnyAsync());
                Assert.False(await db.CourseGoals.AnyAsync());
                Assert.False(await db.SessionTemplates.AnyAsync());
            }

            // After the scope ends, the real user applies again.
            Assert.True(await db.Sessions.AnyAsync());
        });
    }
}
