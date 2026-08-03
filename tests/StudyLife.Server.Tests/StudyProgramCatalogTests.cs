using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// StudyProgramCatalog only needs a StudyLifeDb, not the full web host, so each test builds a
/// standalone context against its own temp SQLite file rather than spinning up a
/// CustomWebApplicationFactory.
/// </summary>
public class StudyProgramCatalogTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"studylife-catalog-{Guid.NewGuid():N}.db");

    private StudyLifeDb NewContext()
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
    public async Task LoadCoursesAsync_AppliesTheCustomCourseIdOffset()
    {
        using var db = NewContext();
        var program = new StudyProgramEntity { Name = "Test Program", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        await db.SaveChangesAsync();

        db.CustomCourses.Add(new CustomCourseEntity
        {
            StudyProgramId = program.Id,
            Semester = 1,
            Name = "Intro",
            Code = "INT-101",
            Ects = 5,
        });
        await db.SaveChangesAsync();
        var courseId = db.CustomCourses.Single().Id;

        var courses = await StudyProgramCatalog.LoadCoursesAsync(db, program.Id);

        Assert.Single(courses);
        Assert.Equal(StudyProgramCatalog.CustomCourseIdOffset + courseId, courses[0].Id);
    }

    [Fact]
    public async Task LoadCoursesAsync_ResolvesCourseGroupIdToGroupName()
    {
        using var db = NewContext();
        var program = new StudyProgramEntity { Name = "Test Program", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        await db.SaveChangesAsync();

        var group = new CourseGroupEntity { StudyProgramId = program.Id, Name = "Electives A", EctsQuota = 10 };
        db.CourseGroups.Add(group);
        await db.SaveChangesAsync();

        db.CustomCourses.Add(new CustomCourseEntity
        {
            StudyProgramId = program.Id,
            Semester = 2,
            Name = "Elective Course",
            Code = "EL-201",
            Ects = 5,
            CourseGroupId = group.Id,
        });
        await db.SaveChangesAsync();

        var courses = await StudyProgramCatalog.LoadCoursesAsync(db, program.Id);

        Assert.Equal("Electives A", courses.Single().Group);
    }

    [Fact]
    public async Task LoadCoursesAsync_CourseWithNoGroup_HasNullGroup()
    {
        using var db = NewContext();
        var program = new StudyProgramEntity { Name = "Test Program", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        await db.SaveChangesAsync();

        db.CustomCourses.Add(new CustomCourseEntity
        {
            StudyProgramId = program.Id,
            Semester = 1,
            Name = "Mandatory Course",
            Code = "MC-101",
            Ects = 5,
            CourseGroupId = null,
        });
        await db.SaveChangesAsync();

        var courses = await StudyProgramCatalog.LoadCoursesAsync(db, program.Id);

        Assert.Null(courses.Single().Group);
    }

    [Fact]
    public async Task LoadCoursesAsync_CourseGroupIdReferencingAnotherProgramsGroup_ResolvesToNull()
    {
        // groupNames is scoped to `programId` only (WHERE g.StudyProgramId == programId), so a
        // CourseGroupId that happens to point at a group belonging to a different program is a
        // dictionary miss - the course still comes back, just with Group == null instead of
        // throwing or leaking the other program's group name.
        using var db = NewContext();
        var programA = new StudyProgramEntity { Name = "Program A", CreatedAt = DateTime.UtcNow };
        var programB = new StudyProgramEntity { Name = "Program B", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.AddRange(programA, programB);
        await db.SaveChangesAsync();

        var groupInB = new CourseGroupEntity { StudyProgramId = programB.Id, Name = "B's Group", EctsQuota = 10 };
        db.CourseGroups.Add(groupInB);
        await db.SaveChangesAsync();

        db.CustomCourses.Add(new CustomCourseEntity
        {
            StudyProgramId = programA.Id,
            Semester = 1,
            Name = "Cross-linked Course",
            Code = "XL-101",
            Ects = 5,
            CourseGroupId = groupInB.Id, // dangling relative to program A's own group scope
        });
        await db.SaveChangesAsync();

        var courses = await StudyProgramCatalog.LoadCoursesAsync(db, programA.Id);

        Assert.Null(courses.Single().Group);
    }

    [Fact]
    public async Task LoadCoursesAsync_OnlyReturnsCoursesForTheRequestedProgram()
    {
        using var db = NewContext();
        var programA = new StudyProgramEntity { Name = "Program A", CreatedAt = DateTime.UtcNow };
        var programB = new StudyProgramEntity { Name = "Program B", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.AddRange(programA, programB);
        await db.SaveChangesAsync();

        db.CustomCourses.Add(new CustomCourseEntity { StudyProgramId = programA.Id, Semester = 1, Name = "A1", Code = "A1", Ects = 5 });
        db.CustomCourses.Add(new CustomCourseEntity { StudyProgramId = programB.Id, Semester = 1, Name = "B1", Code = "B1", Ects = 5 });
        await db.SaveChangesAsync();

        var coursesA = await StudyProgramCatalog.LoadCoursesAsync(db, programA.Id);

        Assert.Single(coursesA);
        Assert.Equal("A1", coursesA[0].Name);
    }

    [Fact]
    public async Task LoadCoursesAsync_OrdersBySemesterThenId()
    {
        using var db = NewContext();
        var program = new StudyProgramEntity { Name = "Test Program", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        await db.SaveChangesAsync();

        db.CustomCourses.Add(new CustomCourseEntity { StudyProgramId = program.Id, Semester = 2, Name = "Sem2First", Code = "S2A", Ects = 5 });
        db.CustomCourses.Add(new CustomCourseEntity { StudyProgramId = program.Id, Semester = 1, Name = "Sem1First", Code = "S1A", Ects = 5 });
        db.CustomCourses.Add(new CustomCourseEntity { StudyProgramId = program.Id, Semester = 1, Name = "Sem1Second", Code = "S1B", Ects = 5 });
        await db.SaveChangesAsync();

        var courses = await StudyProgramCatalog.LoadCoursesAsync(db, program.Id);

        Assert.Equal(new[] { "Sem1First", "Sem1Second", "Sem2First" }, courses.Select(c => c.Name));
    }

    [Fact]
    public async Task LoadCoursesAsync_ParsesTopicsFromCommaSeparatedString_TrimmedAndFiltered()
    {
        using var db = NewContext();
        var program = new StudyProgramEntity { Name = "Test Program", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        await db.SaveChangesAsync();

        db.CustomCourses.Add(new CustomCourseEntity
        {
            StudyProgramId = program.Id,
            Semester = 1,
            Name = "Topics Course",
            Code = "TC-101",
            Ects = 5,
            Topics = " Topic One , Topic Two,,Topic Three ",
        });
        await db.SaveChangesAsync();

        var courses = await StudyProgramCatalog.LoadCoursesAsync(db, program.Id);

        Assert.Equal(new[] { "Topic One", "Topic Two", "Topic Three" }, courses.Single().Topics);
    }

    [Fact]
    public async Task LoadGroupQuotasAsync_ReturnsGroupNameToQuotaMapping_ForTheRequestedProgramOnly()
    {
        using var db = NewContext();
        var programA = new StudyProgramEntity { Name = "Program A", CreatedAt = DateTime.UtcNow };
        var programB = new StudyProgramEntity { Name = "Program B", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.AddRange(programA, programB);
        await db.SaveChangesAsync();

        db.CourseGroups.Add(new CourseGroupEntity { StudyProgramId = programA.Id, Name = "Electives A", EctsQuota = 10 });
        db.CourseGroups.Add(new CourseGroupEntity { StudyProgramId = programA.Id, Name = "Electives B", EctsQuota = 20 });
        db.CourseGroups.Add(new CourseGroupEntity { StudyProgramId = programB.Id, Name = "Other Program's Group", EctsQuota = 99 });
        await db.SaveChangesAsync();

        var quotas = await StudyProgramCatalog.LoadGroupQuotasAsync(db, programA.Id);

        Assert.Equal(2, quotas.Count);
        Assert.Equal(10, quotas["Electives A"]);
        Assert.Equal(20, quotas["Electives B"]);
        Assert.False(quotas.ContainsKey("Other Program's Group"));
    }

    [Fact]
    public async Task LoadGroupQuotasAsync_NoGroups_ReturnsEmptyDictionary()
    {
        using var db = NewContext();
        var program = new StudyProgramEntity { Name = "Empty Program", CreatedAt = DateTime.UtcNow };
        db.StudyPrograms.Add(program);
        await db.SaveChangesAsync();

        var quotas = await StudyProgramCatalog.LoadGroupQuotasAsync(db, program.Id);

        Assert.Empty(quotas);
    }
}
