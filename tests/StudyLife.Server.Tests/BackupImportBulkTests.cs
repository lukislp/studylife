using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// The bulk/transaction properties of POST /api/backup/import-json, separate from the
/// reference-remapping round trip in BackupImportTests.cs: many rows across many tables in ONE
/// import (crossing BackupController's insert batch size, so the multi-batch path is actually
/// exercised), and a file whose last table blows up, which must leave nothing behind.
///
/// Own dedicated class/factory for the same reason as BackupImportRoundtripTests: every test
/// here full-replaces AuthUserId 1's data.
/// </summary>
public class BackupImportBulkTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackupImportBulkTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // Deliberately more than BackupController's insert batch size (500), so the sessions pass
    // spans two batches - a wrong batch boundary (a dropped last partial batch, or an id map
    // built against the wrong rows) shows up as a count/reference mismatch below.
    private const int SessionCount = 550;

    [Fact]
    public async Task Import_MultiRowMultiTableFile_ImportsEveryRowAndKeepsReferences()
    {
        const int offset = StudyProgramCatalog.CustomCourseIdOffset;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var day = DateTime.Today.AddDays(-30);

        var envelope = new BackupExportDto
        {
            FormatVersion = 2,
            ExportedAt = DateTime.UtcNow,
            StudyPrograms = new List<StudyProgramExportDto>
            {
                new() { Id = 1, Name = "BulkProgramA-" + suffix, CreatedAt = day },
                new() { Id = 2, Name = "BulkProgramB-" + suffix, CreatedAt = day },
            },
            CourseGroups = new List<CourseGroupExportDto>
            {
                new() { Id = 11, StudyProgramId = 1, Name = "GroupOne", EctsQuota = 10 },
                new() { Id = 12, StudyProgramId = 2, Name = "GroupTwo", EctsQuota = 20 },
            },
            CustomCourses = new List<CustomCourseExportDto>
            {
                new() { Id = 101, StudyProgramId = 1, Semester = 1, Name = "BulkCourseOne", Code = "B1", Ects = 5, CourseGroupId = 11 },
                new() { Id = 102, StudyProgramId = 1, Semester = 2, Name = "BulkCourseTwo", Code = "B2", Ects = 5 },
                new() { Id = 103, StudyProgramId = 2, Semester = 1, Name = "BulkCourseThree", Code = "B3", Ects = 5, CourseGroupId = 12 },
            },
            SessionTemplates = new List<SessionTemplateDto>
            {
                new() { Name = "TemplateOne", CourseId = offset + 101, DurationMinutes = 45, CreatedAt = day },
                new() { Name = "TemplateTwo", CourseId = offset + 102, DurationMinutes = 90, CreatedAt = day },
            },
            CourseGoals = new List<CourseGoalDto>
            {
                new() { CourseId = offset + 101, CourseName = "BulkCourseOne", TargetDate = day.AddDays(90) },
                new() { CourseId = offset + 103, CourseName = "BulkCourseThree", Grade = 1.7m },
            },
            CourseResources = new List<CourseResourceDto>
            {
                new() { CourseId = offset + 101, Title = "Script", Url = "https://example.com/script", CreatedAt = day },
                new() { CourseId = offset + 102, Title = "Slides", Url = "https://example.com/slides", CreatedAt = day },
            },
        };

        for (var i = 0; i < SessionCount; i++)
        {
            envelope.Sessions.Add(new StudySessionDto
            {
                Id = 1000 + i,
                CourseId = offset + (i % 2 == 0 ? 101 : 102),
                CourseName = "BulkCourse",
                StartTime = day.AddMinutes(i * 90),
                EndTime = day.AddMinutes((i * 90) + 60),
                IsCompleted = true,
                TimerModeId = 1,
            });
        }

        // Two notes: the first points at a session from the SECOND insert batch (a broken batch
        // boundary would leave it unlinked), the second relates back to the first.
        envelope.Notes.Add(new NoteDto
        {
            Id = 1,
            Title = "BulkNoteA-" + suffix,
            Content = "a",
            CreatedAt = day,
            UpdatedAt = day,
            CourseId = offset + 103,
            SessionId = 1000 + SessionCount - 1,
        });
        envelope.Notes.Add(new NoteDto
        {
            Id = 2,
            Title = "BulkNoteB-" + suffix,
            Content = "b",
            CreatedAt = day,
            UpdatedAt = day,
            RelatedNoteIds = new List<int> { 1 },
        });
        envelope.Settings.SelectedCourseIds = new List<int> { offset + 101, offset + 102, 1 };
        envelope.Settings.CompletedCourseIds = new List<int> { offset + 103 };
        envelope.Settings.ActiveStudyProgramId = 2;

        var response = await _client.PostAsJsonAsync("/api/backup/import-json", envelope);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<BackupImportResponseDto>();
        Assert.NotNull(result);

        Assert.Equal(2, result!.Imported["studyPrograms"]);
        Assert.Equal(2, result.Imported["courseGroups"]);
        Assert.Equal(3, result.Imported["customCourses"]);
        Assert.Equal(2, result.Imported["sessionTemplates"]);
        Assert.Equal(SessionCount, result.Imported["sessions"]);
        Assert.Equal(2, result.Imported["notes"]);
        Assert.Equal(2, result.Imported["courseGoals"]);
        Assert.Equal(2, result.Imported["courseResources"]);
        Assert.Equal(1, result.Imported["settings"]);
        Assert.Empty(result.Dropped);

        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(SessionCount, await db.Sessions.CountAsync());
            Assert.Equal(3, await db.CustomCourses.CountAsync());
            Assert.Equal(2, await db.CourseGroups.CountAsync());
            Assert.Equal(2, await db.SessionTemplates.CountAsync());
            Assert.Equal(2, await db.CourseGoals.CountAsync());
            Assert.Equal(2, await db.CourseResources.CountAsync());

            // Every session's CourseId was remapped onto one of the two freshly inserted custom
            // courses - proof that the batched inserts fed the id map the right new ids.
            var courseOne = await db.CustomCourses.FirstAsync(c => c.Name == "BulkCourseOne");
            var courseTwo = await db.CustomCourses.FirstAsync(c => c.Name == "BulkCourseTwo");
            var expected = new[] { offset + courseOne.Id, offset + courseTwo.Id }.OrderBy(x => x).ToList();
            var actual = (await db.Sessions.Select(s => s.CourseId).Distinct().ToListAsync()).OrderBy(x => x).ToList();
            Assert.Equal(expected, actual);

            // The last session of the last batch is the one note A links to.
            var lastSession = await db.Sessions.OrderByDescending(s => s.StartTime).FirstAsync();
            var noteA = await db.Notes.FirstAsync(n => n.Title == "BulkNoteA-" + suffix);
            Assert.Equal(lastSession.Id, noteA.SessionId);
            var noteB = await db.Notes.FirstAsync(n => n.Title == "BulkNoteB-" + suffix);
            Assert.Equal(noteA.Id.ToString(), noteB.RelatedNoteIds);

            // The elective-group reference survived its own remap too.
            Assert.Equal((await db.CourseGroups.FirstAsync(g => g.Name == "GroupOne")).Id, courseOne.CourseGroupId);
        });
    }
}

/// <summary>
/// Production environment so an unhandled exception becomes a real 500 response instead of
/// being rethrown into the test client (same trick as ProblemDetailsExceptionHandlerTests) -
/// a rollback test wants to observe what an API caller observes.
/// </summary>
public class ProductionBackupImportFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }
}

public class BackupImportRollbackTests : IClassFixture<ProductionBackupImportFactory>
{
    private readonly ProductionBackupImportFactory _factory;
    private readonly HttpClient _client;

    public BackupImportRollbackTests(ProductionBackupImportFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// ImportJson is a full replace inside ONE transaction: it first deletes everything the
    /// caller owns and only then inserts the file's rows. A row the database rejects late in
    /// that sequence (here two course goals for the same course, which the per-user unique
    /// index on CourseGoals forbids - a corrupted or hand-edited export file) must undo BOTH
    /// the inserts that already succeeded AND the deletions, leaving the account exactly as it
    /// was. Read back from the database rather than through the read endpoints, so a cached GET
    /// can't make a failed rollback look successful.
    /// </summary>
    [Fact]
    public async Task Import_FailingRow_RollsBackInsertsAndTheFullReplaceDeletion()
    {
        // Identified by its StartTime, not by a name: POST /api/sessions derives CourseName
        // server-side from the resolved catalog course (audit finding M2).
        var seededStart = DateTime.Today.AddHours(9);
        var seed = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 1,
            CourseName = "irrelevant",
            StartTime = seededStart,
            EndTime = DateTime.Today.AddHours(10),
            TimerModeId = 1,
        });
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        var sessionsBefore = await _factory.WithDbAsync(db => db.Sessions.CountAsync());
        var notesBefore = await _factory.WithDbAsync(db => db.Notes.CountAsync());

        var envelope = new BackupExportDto
        {
            FormatVersion = 2,
            ExportedAt = DateTime.UtcNow,
            Sessions = new List<StudySessionDto>
            {
                new() { Id = 1, CourseId = 2, CourseName = "Imported", StartTime = DateTime.Today.AddHours(12), EndTime = DateTime.Today.AddHours(13), TimerModeId = 1 },
                new() { Id = 2, CourseId = 2, CourseName = "Imported", StartTime = DateTime.Today.AddHours(14), EndTime = DateTime.Today.AddHours(15), TimerModeId = 1 },
            },
            Notes = new List<NoteDto>
            {
                new() { Id = 1, Title = "ImportedNote", Content = "x", CreatedAt = DateTime.Today, UpdatedAt = DateTime.Today },
            },
            // The poison pill: a course can only ever have ONE goal per user.
            CourseGoals = new List<CourseGoalDto>
            {
                new() { CourseId = 3, CourseName = "Duplicate" },
                new() { CourseId = 3, CourseName = "Duplicate" },
            },
        };

        var response = await _client.PostAsJsonAsync("/api/backup/import-json", envelope);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(sessionsBefore, await db.Sessions.CountAsync());
            Assert.Equal(notesBefore, await db.Notes.CountAsync());
            var sessions = await db.Sessions.ToListAsync();
            Assert.Contains(sessions, s => s.StartTime == seededStart);
            Assert.DoesNotContain(sessions, s => s.CourseName == "Imported");
            Assert.Empty(await db.CourseGoals.ToListAsync());
        });
    }
}
