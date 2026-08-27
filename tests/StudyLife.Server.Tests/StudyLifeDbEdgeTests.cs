using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;

namespace StudyLife.Server.Tests;

/// <summary>
/// Direct DbContext tests for the corners the HTTP-level suites never touch: the
/// Postgres-only model configuration (StudyLifeDbPostgres + the "timestamp without time zone"
/// column forcing, which only runs when the Npgsql provider is active - never in the SQLite
/// test host) and the AuthUserId stamping safety net across ALL user-scoped entity types.
/// The Postgres tests only BUILD the model (no connection is ever opened), which is exactly
/// the code path in question - OnModelCreating runs on first Model access.
/// </summary>
public class StudyLifeDbEdgeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"studylife-dbedge-{Guid.NewGuid():N}.db");

    private StudyLifeDb NewSqliteContext(int authUserId = 1)
    {
        var options = new DbContextOptionsBuilder<StudyLifeDb>().UseSqlite($"Data Source={_dbPath}").Options;
        var db = new StudyLifeDb(options, new TestCurrentUserAccessor { AuthUserId = authUserId });
        db.Database.EnsureCreated();
        return db;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    private static StudyLifeDbPostgres NewPostgresContext()
    {
        // A connection string is required to build options, but no connection is ever opened -
        // model building is purely in-memory.
        var options = new DbContextOptionsBuilder<StudyLifeDbPostgres>()
            .UseNpgsql("Host=localhost;Database=studylife-model-only;Username=x;Password=x")
            .Options;
        return new StudyLifeDbPostgres(options, new TestCurrentUserAccessor());
    }

    [Fact]
    public void PostgresModel_ForcesEveryDateTimeColumnToTimestampWithoutTimeZone()
    {
        // Regression guard for the live crash "Cannot write DateTime with Kind=Unspecified to
        // PostgreSQL type 'timestamp with time zone'" (see the comment in OnModelCreating):
        // the UnspecifiedKindConverter demands "timestamp without time zone" on EVERY
        // DateTime/DateTime? column when Npgsql is the provider.
        using var db = NewPostgresContext();

        var dateTimeProps = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))
            .ToList();

        Assert.NotEmpty(dateTimeProps); // sanity: the model actually has DateTime columns
        Assert.All(dateTimeProps, p => Assert.Equal("timestamp without time zone", p.GetColumnType()));
    }

    [Fact]
    public void PostgresModel_LeavesNonDateTimeColumnsAlone()
    {
        using var db = NewPostgresContext();

        var titleColumn = db.Model.FindEntityType(typeof(NoteEntity))!
            .FindProperty(nameof(NoteEntity.Title))!;

        Assert.NotEqual("timestamp without time zone", titleColumn.GetColumnType());
    }

    [Fact]
    public void SqliteModel_DoesNotApplyThePostgresColumnType()
    {
        // The forcing is deliberately Postgres-only, to avoid touching the migrated SQLite history.
        using var db = NewSqliteContext();

        var startTime = db.Model.FindEntityType(typeof(StudySessionEntity))!
            .FindProperty(nameof(StudySessionEntity.StartTime))!;

        Assert.NotEqual("timestamp without time zone", startTime.GetColumnType());
    }

    [Fact]
    public void SaveChanges_StampsCurrentUserOnEveryUserScopedEntityKind()
    {
        // One entity of every user-scoped kind, all added WITHOUT an AuthUserId - the
        // safety net in SaveChanges must stamp the current user's id onto each of them.
        var now = DateTime.UtcNow;
        using var db = NewSqliteContext(authUserId: 7);

        // CourseGroupEntity/CustomCourseEntity.StudyProgramId now carry a real FK (migration
        // AddReferentialIntegrityForeignKeys) - the program therefore has to exist (and its
        // real generated Id known) BEFORE group/course reference it, in its own SaveChanges,
        // rather than relying on insertion order within a single shared SaveChanges call.
        var program = new StudyProgramEntity { Name = "My Program", CreatedAt = now };
        db.StudyPrograms.Add(program);
        db.SaveChanges();

        var settings = new UserSettingsEntity();
        var reminder = new SentReminderEntity { Key = "42:reminder5", SentAt = now };
        var goal = new CourseGoalEntity { CourseId = 3, CourseName = "Analysis" };
        var timer = new TimerStateEntity { UpdatedAt = now };
        var group = new CourseGroupEntity { StudyProgramId = program.Id, Name = "Electives", EctsQuota = 10 };
        var course = new CustomCourseEntity { StudyProgramId = program.Id, Name = "Custom Course" };
        var template = new SessionTemplateEntity { Name = "Template", CourseId = 3, CreatedAt = now };
        var resource = new CourseResourceEntity { CourseId = 3, Title = "Slides", Url = "https://x", CreatedAt = now };
        db.AddRange(settings, reminder, goal, timer, group, course, template, resource);
        db.SaveChanges();

        Assert.Equal(7, settings.AuthUserId);
        Assert.Equal(7, reminder.AuthUserId);
        Assert.Equal(7, goal.AuthUserId);
        Assert.Equal(7, timer.AuthUserId);
        Assert.Equal(7, program.AuthUserId);
        Assert.Equal(7, group.AuthUserId);
        Assert.Equal(7, course.AuthUserId);
        Assert.Equal(7, template.AuthUserId);
        Assert.Equal(7, resource.AuthUserId);

        // All rows got real generated keys.
        Assert.NotEqual(0, settings.Id);
        Assert.NotEqual(0, reminder.Id);
        Assert.NotEqual(0, goal.Id);
        Assert.NotEqual(0, timer.Id);
    }

    [Fact]
    public void SaveChanges_LeavesAnExplicitlySetAuthUserIdUntouched()
    {
        // The stamping only fills in MISSING (0) ids - an explicitly chosen user must win
        // over the ambient one, or cross-user writes (e.g. migrations/tests) would be corrupted.
        using var db = NewSqliteContext(authUserId: 1);

        var goal = new CourseGoalEntity { AuthUserId = 42, CourseId = 9, CourseName = "Other user's goal" };
        db.CourseGoals.Add(goal);
        db.SaveChanges();

        Assert.Equal(42, goal.AuthUserId);

        // And the global query filter consequently hides it from user 1.
        Assert.Null(db.CourseGoals.AsNoTracking().FirstOrDefault(g => g.CourseId == 9));
    }

    [Fact]
    public void AuthUserApiKeyColumns_RoundTripThroughTheDatabase()
    {
        // ApiKeyHash/ApiKeyCreatedAt are normally only touched via SQL (the gate resolves the
        // user by hash) - this pins the plain persistence contract of the two columns.
        var createdAt = new DateTime(2026, 8, 1, 10, 0, 0);
        using (var db = NewSqliteContext())
        {
            db.AuthUsers.Add(new AuthUserEntity
            {
                DisplayName = "HA User",
                CreatedAt = createdAt,
                ApiKeyHash = "abc123hash",
                ApiKeyCreatedAt = createdAt,
            });
            db.SaveChanges();
        }

        using var verify = NewSqliteContext();
        var user = verify.AuthUsers.AsNoTracking().Single(u => u.DisplayName == "HA User");
        Assert.Equal("abc123hash", user.ApiKeyHash);
        Assert.Equal(createdAt, user.ApiKeyCreatedAt);
    }

    [Fact]
    public void RecoveryCodeAndSystemSecrets_RoundTripThroughTheDatabase()
    {
        using (var db = NewSqliteContext())
        {
            db.RecoveryCodes.Add(new RecoveryCodeEntity { AuthUserId = 1, CodeHash = "deadbeef", CreatedAt = DateTime.UtcNow });
            db.SystemSecrets.Add(new SystemSecretsEntity { Id = 1, SetupSecretCode = "ABCD-1234" });
            db.SaveChanges();
        }

        using var verify = NewSqliteContext();
        var code = verify.RecoveryCodes.AsNoTracking().Single();
        Assert.NotEqual(0, code.Id);
        Assert.Equal("deadbeef", code.CodeHash);
        Assert.Equal("ABCD-1234", verify.SystemSecrets.AsNoTracking().Single().SetupSecretCode);
    }
}
