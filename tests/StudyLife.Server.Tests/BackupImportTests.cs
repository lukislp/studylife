using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Controllers;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// POST /api/backup/import-json (audit finding M4): full-replace import of the calling user's
/// own data, with id remapping for custom study programs/courses/groups and every cross-
/// reference (Sessions/CourseGoals/CourseResources/SessionTemplates' CourseId, Notes'
/// SessionId/RelatedNoteIds, Settings' ActiveStudyProgramId/Selected-/CompletedCourseIds). See
/// BackupController.ImportJson for the exact remap/drop design. Demo-mode blocking is covered
/// separately in AuthControllerEdgeTests.cs (AuthControllerDemoModeTests.
/// ImportJson_IsBlockedOnDemoInstance) - /api/backup is blocked unconditionally there, so that's
/// a regression test, not new blocking logic tied to this endpoint specifically.
///
/// Own dedicated class (own factory/DB): mutates a LOT of the seeded default user's (AuthUserId
/// 1) data via full-replace imports - sharing a factory with tests that assume that user's data
/// survives untouched would be fragile.
/// </summary>
public class BackupImportRoundtripTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackupImportRoundtripTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Seeds one of everything (a custom study program with an elective group and two courses,
    /// a session and a note linked to it, a second note related to the first, a course goal and
    /// a resource on the elective course, a session template, and settings referencing all of
    /// the above), exports, then imports that SAME file back into the SAME user - a full wipe +
    /// restore. Asserts every table's import count and, more importantly, that every cross-
    /// reference resolves correctly against the FRESHLY assigned ids after the round trip (not
    /// against the original ids - SQLite may legitimately reuse low ids once a table is fully
    /// emptied by ExecuteDeleteAsync, so numeric equality with the pre-import ids isn't a
    /// meaningful assertion; referential consistency after the round trip is).
    /// </summary>
    [Fact]
    public async Task RoundtripIntoSameUser_ReplacesDataAndRemapsAllReferences()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const int offset = StudyProgramCatalog.CustomCourseIdOffset;

        var programRequest = new CreateStudyProgramRequestDto
        {
            Name = $"RoundtripProgram-{suffix}",
            Groups = new List<CreateStudyProgramGroupDto> { new() { Name = "Electives", EctsQuota = 10 } },
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Semester = 1, Name = "Mandatory Course", Code = "MC1", Ects = 5 },
                new() { Semester = 1, Name = "Elective Course", Code = "EC1", Ects = 5, Group = "Electives" },
            },
        };
        var programResponse = await _client.PostAsJsonAsync("/api/studyprograms", programRequest);
        Assert.Equal(HttpStatusCode.OK, programResponse.StatusCode);
        var program = await programResponse.Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(program?.Id);

        // No API exposes the raw CustomCourseEntity ids directly (only the externally shifted
        // CourseDto.Id once the program is active, via GET /api/courses) - read them straight
        // from the DB instead, ordered by name for a deterministic mapping below.
        var customCourseIds = await _factory.WithDbAsync(db => db.CustomCourses
            .Where(c => c.StudyProgramId == program!.Id!.Value)
            .OrderBy(c => c.Name)
            .Select(c => c.Id)
            .ToListAsync());
        Assert.Equal(2, customCourseIds.Count);
        var electiveCourseExternalId = offset + customCourseIds[0]; // "Elective Course" sorts first
        var mandatoryCourseExternalId = offset + customCourseIds[1];

        var settingsGet = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.NotNull(settingsGet);
        settingsGet!.ActiveStudyProgramId = program!.Id;
        settingsGet.SelectedCourseIds = new List<int> { mandatoryCourseExternalId, electiveCourseExternalId, 1 };
        settingsGet.CompletedCourseIds = new List<int> { electiveCourseExternalId };
        var settingsPut = await _client.PutAsJsonAsync("/api/settings", settingsGet);
        Assert.Equal(HttpStatusCode.OK, settingsPut.StatusCode);

        var sessionResponse = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = mandatoryCourseExternalId,
            CourseName = "Mandatory Course",
            StartTime = DateTime.Today.AddHours(9),
            EndTime = DateTime.Today.AddHours(10),
            TimerModeId = 1,
        });
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        var session = await sessionResponse.Content.ReadFromJsonAsync<StudySessionDto>();
        Assert.NotNull(session);

        var noteAResponse = await _client.PostAsJsonAsync("/api/notes", new NoteDto { Title = "Note A", Content = "a" });
        Assert.Equal(HttpStatusCode.OK, noteAResponse.StatusCode);
        var noteA = await noteAResponse.Content.ReadFromJsonAsync<NoteDto>();
        var noteBResponse = await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = "Note B",
            Content = "b",
            CourseId = electiveCourseExternalId,
            SessionId = session!.Id,
        });
        Assert.Equal(HttpStatusCode.OK, noteBResponse.StatusCode);
        var noteB = await noteBResponse.Content.ReadFromJsonAsync<NoteDto>();

        // RelatedNoteIds has no public write path (NotesController.Create/Update deliberately
        // ignore it - server/AI-assigned only, see NoteDto.RelatedNoteIds doc comment) - set
        // directly, same as NotesControllerTests' own poisoned-data tests do.
        await _factory.WithDbAsync(async db =>
        {
            var entity = await db.Notes.SingleAsync(n => n.Id == noteB!.Id);
            entity.RelatedNoteIds = noteA!.Id.ToString();
            await db.SaveChangesAsync();
        });

        var goalPut = await _client.PutAsJsonAsync($"/api/coursegoals/{electiveCourseExternalId}", new CourseGoalDto
        {
            CourseId = electiveCourseExternalId,
            CourseName = "Elective Course",
            Grade = 1.3m,
        });
        Assert.Equal(HttpStatusCode.OK, goalPut.StatusCode);

        var resourcePost = await _client.PostAsJsonAsync("/api/courseresources", new CourseResourceDto
        {
            CourseId = mandatoryCourseExternalId,
            Title = "Slides",
            Url = "https://example.com/slides",
        });
        Assert.Equal(HttpStatusCode.OK, resourcePost.StatusCode);

        var templatePost = await _client.PostAsJsonAsync("/api/sessiontemplates", new SessionTemplateDto
        {
            Name = "Weekly Mandatory",
            CourseId = mandatoryCourseExternalId,
            CourseName = "Mandatory Course",
            DurationMinutes = 90,
        });
        Assert.Equal(HttpStatusCode.OK, templatePost.StatusCode);

        // ── Export, then import the SAME file back into the SAME user (full wipe + restore) ──
        var exportResponse = await _client.GetAsync("/api/backup/export");
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        var export = await exportResponse.Content.ReadFromJsonAsync<BackupExportDto>();
        Assert.NotNull(export);
        Assert.Equal(2, export!.FormatVersion);

        var importResponse = await _client.PostAsJsonAsync("/api/backup/import-json", export);
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        var result = await importResponse.Content.ReadFromJsonAsync<BackupImportResponseDto>();
        Assert.NotNull(result);

        Assert.Equal(1, result!.Imported["studyPrograms"]);
        Assert.Equal(1, result.Imported["courseGroups"]);
        Assert.Equal(2, result.Imported["customCourses"]);
        Assert.Equal(1, result.Imported["sessionTemplates"]);
        Assert.Equal(1, result.Imported["sessions"]);
        Assert.Equal(2, result.Imported["notes"]);
        Assert.Equal(1, result.Imported["courseGoals"]);
        Assert.Equal(1, result.Imported["courseResources"]);
        Assert.Equal(1, result.Imported["settings"]);
        // A fully self-consistent file (every reference resolves within it) - nothing dropped.
        Assert.All(result.Dropped.Values, count => Assert.Equal(0, count));

        // ── Every cross-reference now resolves to the freshly assigned ids ──────────────────
        await _factory.WithDbAsync(async db =>
        {
            var newProgram = await db.StudyPrograms.SingleAsync();
            Assert.Equal($"RoundtripProgram-{suffix}", newProgram.Name);

            var newGroup = await db.CourseGroups.SingleAsync();
            Assert.Equal(newProgram.Id, newGroup.StudyProgramId);

            var newCourses = await db.CustomCourses.OrderBy(c => c.Name).ToListAsync();
            Assert.Equal(2, newCourses.Count);
            var newElective = newCourses.Single(c => c.Name == "Elective Course");
            var newMandatory = newCourses.Single(c => c.Name == "Mandatory Course");
            Assert.Equal(newProgram.Id, newElective.StudyProgramId);
            Assert.Equal(newProgram.Id, newMandatory.StudyProgramId);
            Assert.Equal(newGroup.Id, newElective.CourseGroupId);
            Assert.Null(newMandatory.CourseGroupId);

            var newElectiveExternalId = offset + newElective.Id;
            var newMandatoryExternalId = offset + newMandatory.Id;

            var newSession = await db.Sessions.SingleAsync();
            Assert.Equal(newMandatoryExternalId, newSession.CourseId);

            var newTemplate = await db.SessionTemplates.SingleAsync();
            Assert.Equal(newMandatoryExternalId, newTemplate.CourseId);

            var newGoal = await db.CourseGoals.SingleAsync();
            Assert.Equal(newElectiveExternalId, newGoal.CourseId);

            var newResource = await db.CourseResources.SingleAsync();
            Assert.Equal(newMandatoryExternalId, newResource.CourseId);

            var newNoteA = await db.Notes.SingleAsync(n => n.Title == "Note A");
            var newNoteB = await db.Notes.SingleAsync(n => n.Title == "Note B");
            Assert.Equal(newElectiveExternalId, newNoteB.CourseId);
            Assert.Equal(newSession.Id, newNoteB.SessionId);
            Assert.Equal(newNoteA.Id.ToString(), newNoteB.RelatedNoteIds);

            var newSettings = await db.Settings.SingleAsync();
            Assert.Equal(newProgram.Id, newSettings.ActiveStudyProgramId);
            var selected = CommaSeparatedIds.Parse(newSettings.SelectedCourseIds);
            Assert.Contains(newMandatoryExternalId, selected);
            Assert.Contains(newElectiveExternalId, selected);
            Assert.Contains(1, selected); // built-in id passes through unchanged, unvalidated
            var completed = CommaSeparatedIds.Parse(newSettings.CompletedCourseIds);
            Assert.Equal(new[] { newElectiveExternalId }, completed);
        });
    }
}

/// <summary>
/// Own dedicated class/factory: registers two real passkey users (like
/// BackupControllerOwnerRestrictionTests), so the very first registration claims the pre-seeded
/// legacy AuthUserId 1 (see PasskeyCredentialEntity's doc comment) - sharing a factory with a
/// test that assumes AuthUserId 1's default session/data would collide with that claim.
/// </summary>
public class BackupImportMultiUserIsolationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BackupImportMultiUserIsolationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Session-Token", token);
        if (body != null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    /// <summary>
    /// User A's import-json (a full replace for the CALLER's own data - see BackupController.
    /// ImportJson) must never touch user B's rows: the query-filtered ExecuteDeleteAsync calls
    /// in ImportJson already scope to AuthUserId, but this proves it end-to-end over real HTTP
    /// with two distinct accounts, not just by reading the code.
    /// </summary>
    [Fact]
    public async Task Import_ReplacesOnlyCallingUsersData_OtherUserUntouched()
    {
        using var keyA = new FakePasskey();
        var tokenA = await PasskeyHttp.RegisterAsync(_factory, _client, "UserA", keyA);
        using var keyB = new FakePasskey();
        var tokenB = await PasskeyHttp.RegisterAsync(_factory, _client, "UserB", keyB);

        // CourseName is irrelevant here (audit finding M2: POST /api/sessions now derives it
        // server-side from the resolved catalog course) - user B's session is distinguished by
        // its CourseId instead, see the assertion below.
        var sessionB = await SendAsync(HttpMethod.Post, "/api/sessions", tokenB, new StudySessionDto
        {
            CourseId = 1,
            CourseName = "irrelevant",
            StartTime = DateTime.Today.AddHours(8),
            EndTime = DateTime.Today.AddHours(9),
            TimerModeId = 1,
        });
        Assert.Equal(HttpStatusCode.OK, sessionB.StatusCode);

        var beforeName = $"UserA-Before-{Guid.NewGuid():N}";
        var sessionA = await SendAsync(HttpMethod.Post, "/api/sessions", tokenA, new StudySessionDto
        {
            CourseId = 2,
            CourseName = beforeName,
            StartTime = DateTime.Today.AddHours(10),
            EndTime = DateTime.Today.AddHours(11),
            TimerModeId = 1,
        });
        Assert.Equal(HttpStatusCode.OK, sessionA.StatusCode);

        var importedName = $"UserA-Imported-{Guid.NewGuid():N}";
        var importEnvelope = new BackupExportDto
        {
            FormatVersion = 2,
            ExportedAt = DateTime.UtcNow,
            Sessions = new List<StudySessionDto>
            {
                new()
                {
                    CourseId = 3,
                    CourseName = importedName,
                    StartTime = DateTime.Today.AddHours(14),
                    EndTime = DateTime.Today.AddHours(15),
                    TimerModeId = 1,
                },
            },
        };
        var importResponse = await SendAsync(HttpMethod.Post, "/api/backup/import-json", tokenA, importEnvelope);
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);

        // User A: the pre-existing session is GONE, only the imported one remains.
        var sessionsAResponse = await SendAsync(HttpMethod.Get, "/api/sessions", tokenA);
        var sessionsA = await sessionsAResponse.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.Single(sessionsA!);
        Assert.Equal(importedName, sessionsA![0].CourseName);

        // User B: completely untouched by user A's import.
        var sessionsBResponse = await SendAsync(HttpMethod.Get, "/api/sessions", tokenB);
        var sessionsB = await sessionsBResponse.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.Single(sessionsB!);
        Assert.Equal(1, sessionsB![0].CourseId);
    }
}

/// <summary>
/// Edge cases that all get rejected (or accepted-but-tolerant) BEFORE any data is touched -
/// safe to share one factory/class, unlike the two classes above.
/// </summary>
public class BackupImportEdgeCaseTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public BackupImportEdgeCaseTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    /// <summary>
    /// Old (pre-M4) exports have no formatVersion property at all and PascalCase nested DTOs -
    /// both must still import successfully: case-insensitive JSON binding (ASP.NET Core's
    /// JsonSerializerDefaults.Web, unchanged in Program.cs) recovers the PascalCase fields, and
    /// FormatVersion simply deserializes to its int default (0), which BackupController.
    /// ImportJson explicitly treats as "legacy, proceed". Tables that don't exist in a v1 file
    /// (StudyPrograms/CourseGroups/CustomCourses/SessionTemplates) just import nothing instead
    /// of failing the whole request.
    /// </summary>
    [Fact]
    public async Task Import_AcceptsLegacyV1Envelope()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var legacyJson = $$"""
        {
          "exportedAt": "2025-01-01T00:00:00Z",
          "sessions": [{
            "Id": 0, "CourseId": 5, "CourseName": "LegacyCourse-{{suffix}}", "CourseColor": "#123456",
            "StartTime": "2025-01-01T10:00:00", "EndTime": "2025-01-01T11:00:00",
            "Topic": "Legacy Topic", "Notes": null, "IsCompleted": false, "TimerModeId": 1, "RecurrenceGroupId": null
          }],
          "notes": [{
            "Id": 0, "Title": "LegacyNote-{{suffix}}", "Content": "legacy content",
            "CreatedAt": "2025-01-01T00:00:00", "UpdatedAt": "2025-01-01T00:00:00",
            "CourseId": null, "SessionId": null, "IsMarkdown": false, "SourceUrl": null,
            "Tags": null, "Summary": null, "RelatedNoteIds": []
          }],
          "courseGoals": [],
          "courseResources": [],
          "settings": { "SelectedCourseIds": [1,2,3,4], "CompletedCourseIds": [], "Theme": "dark" }
        }
        """;

        using var content = new StringContent(legacyJson, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/backup/import-json", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<BackupImportResponseDto>();
        Assert.NotNull(result);
        Assert.Equal(1, result!.Imported["sessions"]);
        Assert.Equal(1, result.Imported["notes"]);
        // Newer tables have no representation in a v1 file - nothing to import for them.
        Assert.Equal(0, result.Imported["studyPrograms"]);
        Assert.Equal(0, result.Imported["customCourses"]);
        Assert.Equal(0, result.Imported["sessionTemplates"]);

        var sessions = await _client.GetFromJsonAsync<List<StudySessionDto>>("/api/sessions");
        Assert.Contains(sessions!, s => s.CourseName == $"LegacyCourse-{suffix}");

        var notes = await _client.GetFromJsonAsync<List<NoteDto>>("/api/notes");
        Assert.Contains(notes!, n => n.Title == $"LegacyNote-{suffix}");
    }

    [Fact]
    public async Task Import_RejectsGarbagePayload()
    {
        using var content = new StringContent("not valid json {{{", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/backup/import-json", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_RejectsUnsupportedFormatVersion()
    {
        var response = await _client.PostAsJsonAsync("/api/backup/import-json", new BackupExportDto { FormatVersion = 99 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A JSON import is capped far below the raw 512MB SQLite restore (BackupController.
    /// MaxImportJsonBytes = 64MB) - a single oversized string field is enough to trigger it
    /// without needing a structurally complex payload.
    /// </summary>
    [Fact]
    public async Task Import_RejectsOversizedPayload()
    {
        var filler = new string('x', (int)BackupController.MaxImportJsonBytes + 1024);
        var json = $$"""{"notes":[{"title":"t","content":"{{filler}}"}]}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/backup/import-json", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
