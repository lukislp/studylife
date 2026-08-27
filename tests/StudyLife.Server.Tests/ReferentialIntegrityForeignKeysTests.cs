using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using StudyLife.Server.Data;

namespace StudyLife.Server.Tests;

/// <summary>
/// Direct DbContext tests for the real FK constraints added in the referential-integrity pass
/// (see StudyLifeDb.OnModelCreating and migration AddReferentialIntegrityForeignKeys) - pure
/// DB-level cascade/set-null/restrict behavior, independent of any controller. Uses
/// EnsureCreated() (not the migration history) like StudyLifeDbEdgeTests - the schema it builds
/// is generated straight from the current model, so it carries the exact same FK constraints
/// the migration adds, without needing to replay the whole migration history.
/// </summary>
public class ReferentialIntegrityForeignKeysTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"studylife-fk-{Guid.NewGuid():N}.db");

    private StudyLifeDb NewSqliteContext()
    {
        var options = new DbContextOptionsBuilder<StudyLifeDb>().UseSqlite($"Data Source={_dbPath}").Options;
        var db = new StudyLifeDb(options, new TestCurrentUserAccessor());
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

    [Fact]
    public void DeletingStudyProgram_CascadesToItsCourseGroupsAndCustomCourses_ButLeavesUnrelatedSessionsAndGoalsAlone()
    {
        using var db = NewSqliteContext();

        var program = new StudyProgramEntity { Name = "Program", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        db.SaveChanges();

        var group = new CourseGroupEntity { StudyProgramId = program.Id, Name = "Electives", EctsQuota = 10 };
        db.CourseGroups.Add(group);
        db.SaveChanges();

        var course = new CustomCourseEntity { StudyProgramId = program.Id, CourseGroupId = group.Id, Name = "Course" };
        db.CustomCourses.Add(course);
        db.SaveChanges();

        // Unrelated rows that only share the loose, FK-less CourseId convention - deleting the
        // program must NOT touch these (see StudyProgramsController.Delete's doc comment on why
        // CourseId deliberately has no FK: the frozen-history design depends on it surviving).
        var session = new StudySessionEntity { CourseId = 1, CourseName = "X", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1) };
        var goal = new CourseGoalEntity { CourseId = 1, CourseName = "X" };
        db.AddRange(session, goal);
        db.SaveChanges();

        db.StudyPrograms.Remove(program);
        db.SaveChanges();

        Assert.Empty(db.CourseGroups.IgnoreQueryFilters().Where(g => g.Id == group.Id));
        Assert.Empty(db.CustomCourses.IgnoreQueryFilters().Where(c => c.Id == course.Id));
        Assert.NotEmpty(db.Sessions.IgnoreQueryFilters().Where(s => s.Id == session.Id));
        Assert.NotEmpty(db.CourseGoals.IgnoreQueryFilters().Where(g => g.Id == goal.Id));
    }

    [Fact]
    public void DeletingCourseGroup_NullsCourseGroupIdOnItsCustomCourses_ButLeavesTheCourseItself()
    {
        using var db = NewSqliteContext();

        var program = new StudyProgramEntity { Name = "Program", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        db.SaveChanges();

        var group = new CourseGroupEntity { StudyProgramId = program.Id, Name = "Electives", EctsQuota = 10 };
        db.CourseGroups.Add(group);
        db.SaveChanges();

        var course = new CustomCourseEntity { StudyProgramId = program.Id, CourseGroupId = group.Id, Name = "Course" };
        db.CustomCourses.Add(course);
        db.SaveChanges();

        db.CourseGroups.Remove(group);
        db.SaveChanges();

        var reloaded = db.CustomCourses.AsNoTracking().Single(c => c.Id == course.Id);
        Assert.Null(reloaded.CourseGroupId);
    }

    [Fact]
    public void DeletingSession_NullsSessionIdOnItsNoteAndTimerState()
    {
        using var db = NewSqliteContext();

        var session = new StudySessionEntity { CourseId = 1, CourseName = "X", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1) };
        db.Sessions.Add(session);
        db.SaveChanges();

        var note = new NoteEntity { Title = "T", Content = "C", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, SessionId = session.Id };
        var timer = new TimerStateEntity { SessionId = session.Id, UpdatedAt = DateTime.UtcNow };
        db.AddRange(note, timer);
        db.SaveChanges();

        db.Sessions.Remove(session);
        db.SaveChanges();

        Assert.Null(db.Notes.AsNoTracking().Single(n => n.Id == note.Id).SessionId);
        Assert.Null(db.TimerState.AsNoTracking().Single(t => t.Id == timer.Id).SessionId);
    }

    [Fact]
    public void DeletingAuthUser_UsedAsInviteCreator_IsBlockedByRestrict()
    {
        // No user-deletion feature exists yet (see the comment on this FK in
        // StudyLifeDb.OnModelCreating) - this pins the constraint's behavior directly at the DB
        // level for the day a deletion feature does land.
        int creatorId;
        using (var db = NewSqliteContext())
        {
            var creator = new AuthUserEntity { DisplayName = "Creator", CreatedAt = DateTime.UtcNow };
            db.AuthUsers.Add(creator);
            db.SaveChanges();
            creatorId = creator.Id;

            db.AuthInvites.Add(new AuthInviteEntity
            {
                TokenHash = "restrict-hash",
                CreatedByUserId = creatorId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
            });
            db.SaveChanges();
        }

        // Fresh, untracked context for the deletion attempt: this tests the REAL, DB-level
        // RESTRICT constraint (a raw FK violation surfacing as DbUpdateException). Reusing the
        // context that already tracks the invite would instead trip EF's own client-side
        // "required relationship severed" guard first (InvalidOperationException) - a real,
        // separate EF safety net, but not what this test is pinning.
        using var deleteDb = NewSqliteContext();
        deleteDb.AuthUsers.Remove(deleteDb.AuthUsers.Single(u => u.Id == creatorId));
        Assert.Throws<DbUpdateException>(() => deleteDb.SaveChanges());
    }

    [Fact]
    public void DeletingAuthUser_UsedAsInviteConsumer_NullsUsedByUserId()
    {
        using var db = NewSqliteContext();

        var creator = new AuthUserEntity { DisplayName = "Creator", CreatedAt = DateTime.UtcNow };
        var consumer = new AuthUserEntity { DisplayName = "Consumer", CreatedAt = DateTime.UtcNow };
        db.AddRange(creator, consumer);
        db.SaveChanges();

        var invite = new AuthInviteEntity
        {
            TokenHash = "setnull-hash",
            CreatedByUserId = creator.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            UsedAt = DateTime.UtcNow,
            UsedByUserId = consumer.Id,
        };
        db.AuthInvites.Add(invite);
        db.SaveChanges();

        db.AuthUsers.Remove(consumer);
        db.SaveChanges();

        Assert.Null(db.AuthInvites.AsNoTracking().Single(i => i.Id == invite.Id).UsedByUserId);
    }
}

/// <summary>
/// Verifies that migration AddReferentialIntegrityForeignKeys itself applies cleanly on a
/// database that already carries orphaned foreign-key-shaped values - exactly the state a
/// real, long-lived production DB is in (nothing ever cleaned up Note.SessionId/
/// TimerState.SessionId when their Session was deleted; the other five relations are expected
/// no-ops but get the same defensive cleanup). Migrates a fresh temp DB up to the migration
/// immediately BEFORE the FK migration (at that schema, no FK constraints exist yet, so
/// orphaned values can be inserted freely via plain EF Add/SaveChanges - the model in code
/// already has the FK config, but SaveChanges only ever issues a plain INSERT; SQLite doesn't
/// enforce a constraint the physical table doesn't have), then applies the rest of the
/// migration history and asserts it does NOT throw, and that the orphans ended up cleaned
/// exactly as the migration's cleanup SQL promises.
/// </summary>
public class ReferentialIntegrityForeignKeysMigrationTests : IDisposable
{
    private const string LastMigrationBeforeFks = "20260826184226_AddAuthInvites";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"studylife-fkmigration-{Guid.NewGuid():N}.db");

    private StudyLifeDb NewContext()
    {
        var options = new DbContextOptionsBuilder<StudyLifeDb>().UseSqlite($"Data Source={_dbPath}").Options;
        return new StudyLifeDb(options, new TestCurrentUserAccessor());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    [Fact]
    public void Migration_AppliesCleanlyAndCleansUpOrphanedForeignKeyValues()
    {
        const int nonExistentId = 999_999;

        AuthUserEntity user;
        StudyProgramEntity program;
        CustomCourseEntity groupOrphanCourse;
        AuthInviteEntity usedByOrphanInvite;
        NoteEntity orphanNote;
        TimerStateEntity orphanTimer;

        using (var db = NewContext())
        {
            db.GetService<IMigrator>().Migrate(LastMigrationBeforeFks);

            // Two real anchors that the "still valid" rows below point at - AuthUsers already
            // has exactly one seeded row (AddMultiTenantAuthUserFoundation) at this schema
            // version, so start a second one to keep creator/consumer distinct from it.
            user = new AuthUserEntity { DisplayName = "Owner", CreatedAt = DateTime.UtcNow };
            program = new StudyProgramEntity { AuthUserId = 1, Name = "Real Program", CreatedAt = DateTime.UtcNow };
            db.AddRange(user, program);
            db.SaveChanges();

            // Orphaned rows: every FK-shaped column below points at an id that doesn't exist.
            // The physical schema at this migration has no FK constraints yet, so these plain
            // inserts succeed exactly like on a real, never-cleaned-up production DB.
            db.CourseGroups.Add(new CourseGroupEntity { AuthUserId = 1, StudyProgramId = nonExistentId, Name = "Orphaned Group", EctsQuota = 5 });
            db.CustomCourses.Add(new CustomCourseEntity { AuthUserId = 1, StudyProgramId = nonExistentId, Name = "Orphaned Course (program)" });
            groupOrphanCourse = new CustomCourseEntity { AuthUserId = 1, StudyProgramId = program.Id, CourseGroupId = nonExistentId, Name = "Orphaned Course (group)" };
            db.CustomCourses.Add(groupOrphanCourse);
            db.AuthInvites.Add(new AuthInviteEntity
            {
                TokenHash = "orphan-creator",
                CreatedByUserId = nonExistentId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
            });
            usedByOrphanInvite = new AuthInviteEntity
            {
                TokenHash = "orphan-usedby",
                CreatedByUserId = user.Id,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                UsedAt = DateTime.UtcNow,
                UsedByUserId = nonExistentId,
            };
            db.AuthInvites.Add(usedByOrphanInvite);
            orphanNote = new NoteEntity { AuthUserId = 1, Title = "Orphaned note", Content = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, SessionId = nonExistentId };
            db.Notes.Add(orphanNote);
            orphanTimer = new TimerStateEntity { AuthUserId = 1, SessionId = nonExistentId, UpdatedAt = DateTime.UtcNow };
            db.TimerState.Add(orphanTimer);
            db.SaveChanges();
        }

        // Apply the remaining migrations, including AddReferentialIntegrityForeignKeys - must
        // NOT throw despite the orphaned rows seeded above (the migration's own cleanup SQL
        // must handle them before the FK constraints are created/validated).
        using (var db = NewContext())
        {
            var exception = Record.Exception(() => db.GetService<IMigrator>().Migrate());
            Assert.Null(exception);
        }

        using (var db = NewContext())
        {
            // Required (NOT NULL) FK columns: the orphan row itself is gone.
            Assert.Empty(db.CourseGroups.IgnoreQueryFilters().Where(g => g.Name == "Orphaned Group"));
            Assert.Empty(db.CustomCourses.IgnoreQueryFilters().Where(c => c.Name == "Orphaned Course (program)"));
            Assert.Empty(db.AuthInvites.Where(i => i.TokenHash == "orphan-creator"));

            // Nullable FK columns: only the dangling reference is cleared, the row survives.
            var reloadedGroupOrphanCourse = db.CustomCourses.IgnoreQueryFilters().Single(c => c.Id == groupOrphanCourse.Id);
            Assert.Null(reloadedGroupOrphanCourse.CourseGroupId);

            var reloadedInvite = db.AuthInvites.Single(i => i.Id == usedByOrphanInvite.Id);
            Assert.Null(reloadedInvite.UsedByUserId);

            var reloadedNote = db.Notes.IgnoreQueryFilters().Single(n => n.Id == orphanNote.Id);
            Assert.Null(reloadedNote.SessionId);

            var reloadedTimer = db.TimerState.IgnoreQueryFilters().Single(t => t.Id == orphanTimer.Id);
            Assert.Null(reloadedTimer.SessionId);
        }
    }
}
