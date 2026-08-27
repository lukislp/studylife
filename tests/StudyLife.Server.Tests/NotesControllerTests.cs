using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Own class for search edge cases that rely on an untouched DB
/// (empty search index without noise from other tests in the same class).
/// </summary>
public class NotesControllerFreshDbTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public NotesControllerFreshDbTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task Search_OnEmptyDatabase_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/notes/search?q=anything");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var notes = await response.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.NotNull(notes);
        Assert.Empty(notes!);
    }

    [Fact]
    public async Task Search_MissingQuery_ReturnsEmptyListNotError()
    {
        var response = await _client.GetAsync("/api/notes/search");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var notes = await response.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.NotNull(notes);
        Assert.Empty(notes!);
    }

    [Fact]
    public async Task Search_WhitespaceQuery_ReturnsEmptyListNotError()
    {
        var response = await _client.GetAsync($"/api/notes/search?q={Uri.EscapeDataString("   ")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var notes = await response.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.NotNull(notes);
        Assert.Empty(notes!);
    }
}

public class NotesControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public NotesControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static NoteDto ValidNote() => new()
    {
        Title = "Analysis Zusammenfassung",
        Content = "Grenzwerte und Ableitungen im Detail besprochen.",
        CourseId = 3,
    };

    [Fact]
    public async Task Create_ValidNote_ReturnsCreatedNoteWithTimestamps()
    {
        var response = await _client.PostAsJsonAsync("/api/notes", ValidNote());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(dto);
        Assert.True(dto!.Id > 0);
        Assert.Equal("Analysis Zusammenfassung", dto.Title);
        Assert.Equal("Grenzwerte und Ableitungen im Detail besprochen.", dto.Content);
        Assert.Equal(3, dto.CourseId);
        Assert.Null(dto.SessionId);
        Assert.True(dto.CreatedAt != default);
        Assert.True(dto.UpdatedAt != default);
    }

    [Fact]
    public async Task Create_WithoutCourseId_PersistsNullCourseId()
    {
        var note = ValidNote();
        note.CourseId = null;

        var response = await _client.PostAsJsonAsync("/api/notes", note);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(dto);
        Assert.Null(dto!.CourseId);
    }

    [Fact]
    public async Task GetAll_AfterCreate_IncludesNoteOrderedByUpdatedAtDescending()
    {
        var first = await (await _client.PostAsJsonAsync("/api/notes", new NoteDto { Title = "Erste", Content = "eins" }))
            .Content.ReadFromJsonAsync<NoteDto>();
        var second = await (await _client.PostAsJsonAsync("/api/notes", new NoteDto { Title = "Zweite", Content = "zwei" }))
            .Content.ReadFromJsonAsync<NoteDto>();

        var response = await _client.GetAsync("/api/notes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var notes = await response.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.NotNull(notes);
        var ids = notes!.Select(n => n.Id).ToList();
        Assert.Contains(first!.Id, ids);
        Assert.Contains(second!.Id, ids);
        // Most recently updated first: the second note was created after the first.
        Assert.True(ids.IndexOf(second.Id) < ids.IndexOf(first.Id));
    }

    [Fact]
    public async Task Update_ExistingNote_PersistsChangesAndBumpsUpdatedAt()
    {
        var created = await (await _client.PostAsJsonAsync("/api/notes", ValidNote()))
            .Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(created);

        await Task.Delay(15); // ensure UpdatedAt can measurably differ from CreatedAt

        var updated = created!;
        updated.Title = "Aktualisierter Titel";
        updated.Content = "Neuer Inhalt";
        updated.CourseId = 5;

        var response = await _client.PutAsJsonAsync($"/api/notes/{created.Id}", updated);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(dto);
        Assert.Equal("Aktualisierter Titel", dto!.Title);
        Assert.Equal("Neuer Inhalt", dto.Content);
        Assert.Equal(5, dto.CourseId);
        Assert.Equal(created.CreatedAt, dto.CreatedAt);
        Assert.True(dto.UpdatedAt >= created.UpdatedAt);
    }

    [Fact]
    public async Task Update_NonExistentNote_ReturnsNotFound()
    {
        var response = await _client.PutAsJsonAsync("/api/notes/999999", ValidNote());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── CourseId/SessionId validation (audit finding M2 follow-up: the two write paths PR #86 missed) ──

    [Fact]
    public async Task Create_UnknownCourseId_ReturnsBadRequestWithStableMessage()
    {
        var note = ValidNote();
        note.CourseId = 987654;

        var response = await _client.PostAsJsonAsync("/api/notes", note);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("987654", body);
    }

    [Fact]
    public async Task Create_UnknownSessionId_ReturnsBadRequestWithStableMessage()
    {
        var note = ValidNote();
        note.SessionId = 987654;

        var response = await _client.PostAsJsonAsync("/api/notes", note);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("987654", body);
    }

    [Fact]
    public async Task Create_WithoutSessionId_PersistsNullSessionId()
    {
        var note = ValidNote();
        note.SessionId = null;

        var response = await _client.PostAsJsonAsync("/api/notes", note);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(dto);
        Assert.Null(dto!.SessionId);
    }

    [Fact]
    public async Task Create_ValidSessionId_PersistsIt()
    {
        var sessionId = await SeedSessionDirectlyAsync();
        var note = ValidNote();
        note.SessionId = sessionId;

        var response = await _client.PostAsJsonAsync("/api/notes", note);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(dto);
        Assert.Equal(sessionId, dto!.SessionId);
    }

    [Fact]
    public async Task Update_CourseIdChangedToUnknown_ReturnsBadRequest()
    {
        var created = await (await _client.PostAsJsonAsync("/api/notes", ValidNote()))
            .Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(created);

        var updated = created!;
        updated.CourseId = 987654;

        var response = await _client.PutAsJsonAsync($"/api/notes/{created.Id}", updated);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("987654", body);
    }

    [Fact]
    public async Task Update_SessionIdChangedToUnknown_ReturnsBadRequest()
    {
        var created = await (await _client.PostAsJsonAsync("/api/notes", ValidNote()))
            .Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(created);

        var updated = created!;
        updated.SessionId = 987654;

        var response = await _client.PutAsJsonAsync($"/api/notes/{created.Id}", updated);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("987654", body);
    }

    /// <summary>
    /// Frozen-at-creation exemption (mirrors SessionsController's
    /// Update_CourseIdUnchanged_OrphanedCustomCourse_StillSucceeds): editing an old note still
    /// bound to a since-deleted custom course must keep working - only a CHANGED CourseId is
    /// validated. Simulates the orphan by inserting the note directly with a custom-range CourseId
    /// that was never seeded as a real CustomCourseEntity.
    /// </summary>
    [Fact]
    public async Task Update_CourseIdUnchanged_OrphanedCustomCourse_StillSucceeds()
    {
        var orphanCourseId = StudyProgramCatalog.CustomCourseIdOffset + 555_555;
        var created = await SeedNoteDirectlyAsync(courseId: orphanCourseId);

        var updated = ValidNote();
        updated.Id = created.Id;
        updated.CourseId = orphanCourseId;
        updated.Title = "Aktualisiert trotz gelöschtem Kurs";

        var response = await _client.PutAsJsonAsync($"/api/notes/{created.Id}", updated);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(dto);
        Assert.Equal(orphanCourseId, dto!.CourseId);
        Assert.Equal("Aktualisiert trotz gelöschtem Kurs", dto.Title);
    }

    /// <summary>
    /// Unlike CourseId (no FK possible - the catalog is code), SessionId has a real
    /// ON DELETE SET NULL foreign key since the referential-integrity migration: deleting the
    /// session nulls the note's reference IN THE DATABASE immediately, so the "unchanged
    /// dangling SessionId" state the frozen-at-creation exemption covers for courses cannot
    /// exist for sessions anymore. A client re-sending the stale id after the delete is now a
    /// null -> id CHANGE and is correctly rejected; re-sending null (what a freshly polled
    /// client does) keeps working. Both halves pinned here.
    /// </summary>
    [Fact]
    public async Task Update_AfterSessionDelete_FkNullsReference_StaleIdRejected_NullAccepted()
    {
        var sessionId = await SeedSessionDirectlyAsync();
        var created = await SeedNoteDirectlyAsync(sessionId: sessionId);
        await _factory.WithDbAsync(async db =>
        {
            var session = await db.Sessions.FirstAsync(s => s.Id == sessionId);
            db.Sessions.Remove(session);
            await db.SaveChangesAsync();
        });

        // The FK already nulled the stored reference.
        await _factory.WithDbAsync(async db =>
            Assert.Null((await db.Notes.AsNoTracking().FirstAsync(n => n.Id == created.Id)).SessionId));

        var stale = ValidNote();
        stale.Id = created.Id;
        stale.SessionId = sessionId;
        stale.Title = "Stale Session-Referenz";
        var staleResponse = await _client.PutAsJsonAsync($"/api/notes/{created.Id}", stale);
        Assert.Equal(HttpStatusCode.BadRequest, staleResponse.StatusCode);

        var detached = ValidNote();
        detached.Id = created.Id;
        detached.SessionId = null;
        detached.Title = "Aktualisiert nach Session-Löschung";
        var response = await _client.PutAsJsonAsync($"/api/notes/{created.Id}", detached);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(dto);
        Assert.Null(dto!.SessionId);
        Assert.Equal("Aktualisiert nach Session-Löschung", dto.Title);
    }

    /// <summary>Inserts a session directly via EF (bypassing SessionsController's own CourseId
    /// validation, irrelevant here) so tests have a real SessionId to reference.</summary>
    private Task<int> SeedSessionDirectlyAsync() => _factory.WithDbAsync(async db =>
    {
        var session = new StudySessionEntity
        {
            CourseId = 1,
            CourseName = "Seeded Session Course",
            CourseColor = "#6C5CE7",
            StartTime = DateTime.Now,
            EndTime = DateTime.Now.AddHours(1),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    });

    /// <summary>Inserts a note directly via EF, bypassing NotesController's own Create validation -
    /// used to set up rows with an orphaned CourseId/SessionId that could never be created through
    /// the normal write path in the first place.</summary>
    private Task<NoteDto> SeedNoteDirectlyAsync(int? courseId = null, int? sessionId = null) => _factory.WithDbAsync(async db =>
    {
        var entity = new NoteEntity
        {
            Title = "Seeded note",
            Content = "Seeded content",
            CourseId = courseId,
            SessionId = sessionId,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        db.Notes.Add(entity);
        await db.SaveChangesAsync();
        return new NoteDto { Id = entity.Id, Title = entity.Title, Content = entity.Content, CourseId = entity.CourseId, SessionId = entity.SessionId };
    });

    [Fact]
    public async Task Delete_ExistingNote_RemovesItFromGetAll()
    {
        var created = await (await _client.PostAsJsonAsync("/api/notes", ValidNote()))
            .Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(created);

        var deleteResponse = await _client.DeleteAsync($"/api/notes/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listResponse = await _client.GetAsync("/api/notes");
        var notes = await listResponse.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.DoesNotContain(notes!, n => n.Id == created.Id);
    }

    [Fact]
    public async Task Delete_NonExistentNote_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/api/notes/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Search_MatchesTitleAndContentAndIsCaseInsensitive()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = $"Photosynthese {unique}",
            Content = "Grundlagen der Zellbiologie und Energiegewinnung.",
        });
        await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = "Unrelated",
            Content = $"Enthält das Wort {unique} mittendrin im Fließtext.",
        });
        await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = "Ganz anderes Thema",
            Content = "Hat mit dem Suchbegriff überhaupt nichts zu tun.",
        });

        // Title match, capitalized differently from the query.
        var byTitle = await _client.GetAsync($"/api/notes/search?q={unique.ToUpperInvariant()}");
        var titleResults = await byTitle.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.Equal(HttpStatusCode.OK, byTitle.StatusCode);
        Assert.Equal(2, titleResults!.Count);
        Assert.Contains(titleResults, n => n.Title.Contains("Photosynthese"));
        Assert.Contains(titleResults, n => n.Content.Contains(unique));
    }

    [Fact]
    public async Task Search_PrefixMatch_FindsPartialWord()
    {
        var unique = "Thermodynamikprinzipien" + Guid.NewGuid().ToString("N")[..6];
        await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = "Physiknotizen",
            Content = unique,
        });

        // Query is a prefix of the full word - BuildFtsMatchQuery appends "*".
        var response = await _client.GetAsync($"/api/notes/search?q={unique[..12]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.Contains(results!, n => n.Content == unique);
    }

    [Fact]
    public async Task Search_TermThatDoesNotExist_ReturnsEmptyResultNotError()
    {
        await _client.PostAsJsonAsync("/api/notes", ValidNote());

        var response = await _client.GetAsync("/api/notes/search?q=xyzzynonexistentterm12345");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.NotNull(results);
        Assert.Empty(results!);
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"\"\"")]
    [InlineData("-")]
    [InlineData("--foo")]
    [InlineData("*")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("AND")]
    [InlineData("OR NOT")]
    [InlineData("col:foo")]
    [InlineData("a\"b\"c")]
    public async Task Search_FtsSpecialCharacters_DoesNotReturnServerError(string query)
    {
        var response = await _client.GetAsync($"/api/notes/search?q={Uri.EscapeDataString(query)}");

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Must return valid JSON (a list), no crash.
        var results = await response.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.NotNull(results);
    }

    [Fact]
    public async Task Search_QuoteInsideRealTerm_StillFindsMatchViaEscaping()
    {
        var unique = "Quantenmechanik" + Guid.NewGuid().ToString("N")[..6];
        await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = unique,
            Content = "Schrödingers Katze",
        });

        // An embedded quotation mark must not break the rest of the query.
        var response = await _client.GetAsync($"/api/notes/search?q={Uri.EscapeDataString($"\"{unique}")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.NotNull(results);
    }

    [Fact]
    public async Task Delete_RemovesNoteFromSearchIndex()
    {
        var unique = "Bioinformatik" + Guid.NewGuid().ToString("N")[..6];
        var created = await (await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = unique,
            Content = "wird gleich wieder gelöscht",
        })).Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(created);

        var preDelete = await _client.GetAsync($"/api/notes/search?q={unique}");
        var preResults = await preDelete.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.Contains(preResults!, n => n.Id == created!.Id);

        await _client.DeleteAsync($"/api/notes/{created!.Id}");

        var postDelete = await _client.GetAsync($"/api/notes/search?q={unique}");
        var postResults = await postDelete.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.DoesNotContain(postResults!, n => n.Id == created.Id);
    }

    [Fact]
    public async Task Update_RefreshesSearchIndexWithNewContent()
    {
        var oldUnique = "Geologie" + Guid.NewGuid().ToString("N")[..6];
        var newUnique = "Ozeanographie" + Guid.NewGuid().ToString("N")[..6];
        var created = await (await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = oldUnique,
            Content = "ursprünglicher Inhalt",
        })).Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(created);

        var updated = created!;
        updated.Title = newUnique;
        await _client.PutAsJsonAsync($"/api/notes/{created.Id}", updated);

        var oldSearch = await _client.GetAsync($"/api/notes/search?q={oldUnique}");
        var oldResults = await oldSearch.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.DoesNotContain(oldResults!, n => n.Id == created.Id);

        var newSearch = await _client.GetAsync($"/api/notes/search?q={newUnique}");
        var newResults = await newSearch.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.Contains(newResults!, n => n.Id == created.Id);
    }
}

/// <summary>
/// M1 regression: RelatedNoteIds used to be parsed with bare int.Parse in
/// NotesController.ToDto - written by the EXTERNAL studylife-ai capture-enrichment path
/// (BackgroundTaskService.CaptureEnrichment), so a single malformed suggestion from there made
/// GET /api/notes throw 500 permanently for every note, not just the poisoned one (ToDto runs
/// per-row inside the same LINQ projection). Own factory: poisons the row directly via the
/// DbContext, since the normal write paths never produce malformed data themselves.
/// </summary>
public class NotesControllerPoisonedDataTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public NotesControllerPoisonedDataTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAll_WithPoisonedRelatedNoteIds_ReturnsOkAndSkipsGarbageTokens()
    {
        var created = await (await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = "Poisoned RelatedNoteIds regression",
            Content = "content",
        })).Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(created);

        await _factory.WithDbAsync(async db =>
        {
            var entity = await db.Notes.FirstAsync(n => n.Id == created!.Id);
            entity.RelatedNoteIds = "1,corrupted,2";
            await db.SaveChangesAsync();
        });

        var response = await _client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var notes = await response.Content.ReadFromJsonAsync<List<NoteDto>>();
        Assert.NotNull(notes);
        var poisoned = notes!.Single(n => n.Id == created!.Id);
        Assert.Equal(new List<int> { 1, 2 }, poisoned.RelatedNoteIds);
    }
}
