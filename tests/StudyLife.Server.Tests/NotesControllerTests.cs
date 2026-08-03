using System.Net;
using System.Net.Http.Json;
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
    private readonly HttpClient _client;

    public NotesControllerTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

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
