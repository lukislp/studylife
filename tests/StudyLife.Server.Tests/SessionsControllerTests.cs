using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// All tests share a factory/DB (see CustomWebApplicationFactory). To still stay
/// safely independent under parallelism, assertions are generally filtered by the ID
/// returned from POST (Contains/Any on Id) instead of overall list lengths, so
/// sessions from other tests in the same class don't cause false failures.
/// </summary>
public class SessionsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static StudySessionDto ValidSession(
        int courseId = 1,
        string courseName = "Analysis 1",
        DateTime? start = null,
        DateTime? end = null,
        bool isCompleted = false,
        string? recurrenceGroupId = null)
    {
        var s = start ?? DateTime.UtcNow.AddDays(1);
        var e = end ?? s.AddHours(1);
        return new StudySessionDto
        {
            CourseId = courseId,
            CourseName = courseName,
            CourseColor = "#6C5CE7",
            StartTime = s,
            EndTime = e,
            Topic = "Integrale",
            Notes = "Test-Notiz",
            IsCompleted = isCompleted,
            TimerModeId = 1,
            RecurrenceGroupId = recurrenceGroupId,
        };
    }

    private async Task<StudySessionDto> CreateAsync(StudySessionDto dto)
    {
        var response = await _client.PostAsJsonAsync("/api/sessions", dto);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<StudySessionDto>();
        Assert.NotNull(created);
        return created!;
    }

    // ---------- POST /api/sessions ----------

    [Fact]
    public async Task Create_ValidSession_ReturnsSessionWithAssignedId()
    {
        var dto = ValidSession(courseId: 11, courseName: "Lineare Algebra");

        var response = await _client.PostAsJsonAsync("/api/sessions", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<StudySessionDto>();
        Assert.NotNull(created);
        Assert.True(created!.Id > 0);
        Assert.Equal("Lineare Algebra", created.CourseName);
        Assert.Equal(dto.StartTime, created.StartTime);
        Assert.Equal(dto.EndTime, created.EndTime);
    }

    [Fact]
    public async Task Create_EndTimeEqualsStartTime_ReturnsBadRequest()
    {
        var start = DateTime.UtcNow.AddDays(1);
        var dto = ValidSession(start: start, end: start);

        var response = await _client.PostAsJsonAsync("/api/sessions", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_EndTimeBeforeStartTime_ReturnsBadRequest()
    {
        var start = DateTime.UtcNow.AddDays(1);
        var dto = ValidSession(start: start, end: start.AddHours(-1));

        var response = await _client.PostAsJsonAsync("/api/sessions", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_CourseIdZero_ReturnsBadRequest()
    {
        var dto = ValidSession(courseId: 0);

        var response = await _client.PostAsJsonAsync("/api/sessions", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_EmptyCourseName_ReturnsBadRequest()
    {
        var dto = ValidSession(courseName: "   ");

        var response = await _client.PostAsJsonAsync("/api/sessions", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- GET /api/sessions ----------

    [Fact]
    public async Task GetAll_ReturnsSessionsWithinDefaultWindow_ExcludesOutOfWindowSessions()
    {
        // Window per the controller: UtcNow-7d to UtcNow+90d.
        var inWindow = await CreateAsync(ValidSession(courseId: 21, start: DateTime.UtcNow.AddDays(5)));
        var beforeWindow = await CreateAsync(ValidSession(courseId: 22, start: DateTime.UtcNow.AddDays(-10)));
        var afterWindow = await CreateAsync(ValidSession(courseId: 23, start: DateTime.UtcNow.AddDays(120)));

        var response = await _client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = await response.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.NotNull(sessions);

        Assert.Contains(sessions!, s => s.Id == inWindow.Id);
        Assert.DoesNotContain(sessions!, s => s.Id == beforeWindow.Id);
        Assert.DoesNotContain(sessions!, s => s.Id == afterWindow.Id);
    }

    // ---------- GET /api/sessions/history ----------

    [Fact]
    public async Task GetHistory_DefaultOnlyCompleted_IncludesPastAndCompleted_ExcludesFutureUncompleted()
    {
        var now = DateTime.Now;
        var completedPast = await CreateAsync(ValidSession(
            courseId: 31, start: now.AddHours(-3), end: now.AddHours(-2), isCompleted: true));
        var endedButNotFlagged = await CreateAsync(ValidSession(
            courseId: 32, start: now.AddHours(-2), end: now.AddHours(-1), isCompleted: false));
        var futureNotCompleted = await CreateAsync(ValidSession(
            courseId: 33, start: now.AddHours(2), end: now.AddHours(3), isCompleted: false));

        // days chosen large enough that the day window (based on UtcNow) reliably covers
        // the hour-based offsets above regardless of time zone.
        var response = await _client.GetAsync("/api/sessions/history?days=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = await response.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.NotNull(sessions);

        Assert.Contains(sessions!, s => s.Id == completedPast.Id);
        Assert.Contains(sessions!, s => s.Id == endedButNotFlagged.Id);
        Assert.DoesNotContain(sessions!, s => s.Id == futureNotCompleted.Id);
    }

    [Fact]
    public async Task GetHistory_OnlyCompletedFalse_IncludesFutureUncompletedSessions()
    {
        var now = DateTime.Now;
        var futureNotCompleted = await CreateAsync(ValidSession(
            courseId: 41, start: now.AddHours(2), end: now.AddHours(3), isCompleted: false));

        var response = await _client.GetAsync("/api/sessions/history?days=30&onlyCompleted=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = await response.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.NotNull(sessions);

        Assert.Contains(sessions!, s => s.Id == futureNotCompleted.Id);
    }

    [Fact]
    public async Task GetHistory_DaysParameter_ExcludesSessionsOlderThanWindow()
    {
        var inWindow = await CreateAsync(ValidSession(
            courseId: 51, start: DateTime.UtcNow.AddDays(-1), end: DateTime.UtcNow.AddDays(-1).AddHours(1)));
        var outOfWindow = await CreateAsync(ValidSession(
            courseId: 52, start: DateTime.UtcNow.AddDays(-10), end: DateTime.UtcNow.AddDays(-10).AddHours(1)));

        var response = await _client.GetAsync("/api/sessions/history?days=2&onlyCompleted=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = await response.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.NotNull(sessions);

        Assert.Contains(sessions!, s => s.Id == inWindow.Id);
        Assert.DoesNotContain(sessions!, s => s.Id == outOfWindow.Id);
    }

    // ---------- PUT /api/sessions/{id} ----------

    [Fact]
    public async Task Update_ValidChange_PersistsAndReturnsUpdatedDto()
    {
        var created = await CreateAsync(ValidSession(courseId: 61, courseName: "Statistik"));
        var updated = ValidSession(courseId: 61, courseName: "Statistik II", isCompleted: true);
        updated.Id = created.Id;

        var response = await _client.PutAsJsonAsync($"/api/sessions/{created.Id}", updated);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<StudySessionDto>();
        Assert.NotNull(dto);
        Assert.Equal("Statistik II", dto!.CourseName);
        Assert.True(dto.IsCompleted);

        // Confirm persistence via a second request (not just the PUT response).
        var historyResponse = await _client.GetAsync("/api/sessions/history?days=400&onlyCompleted=false");
        var history = await historyResponse.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        var persisted = Assert.Single(history!, s => s.Id == created.Id);
        Assert.Equal("Statistik II", persisted.CourseName);
        Assert.True(persisted.IsCompleted);
    }

    [Fact]
    public async Task Update_NonExistentId_ReturnsNotFound()
    {
        var dto = ValidSession(courseId: 71);

        var response = await _client.PutAsJsonAsync("/api/sessions/999999", dto);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_InvalidDto_ReturnsBadRequest()
    {
        var created = await CreateAsync(ValidSession(courseId: 81));
        var invalid = ValidSession(courseId: 81, start: created.StartTime, end: created.StartTime);

        var response = await _client.PutAsJsonAsync($"/api/sessions/{created.Id}", invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_StartTimeChanged_ClearsStaleSentReminders()
    {
        var created = await CreateAsync(ValidSession(courseId: 51, start: DateTime.UtcNow.AddDays(3)));
        await SeedSentReminderAsync($"{created.Id}:reminder60");
        await SeedSentReminderAsync($"{created.Id}:reminder30");

        var moved = ValidSession(courseId: 51, start: created.StartTime.AddHours(2));
        moved.Id = created.Id;
        var response = await _client.PutAsJsonAsync($"/api/sessions/{created.Id}", moved);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await GetSentRemindersAsync($"{created.Id}:reminder"));
    }

    [Fact]
    public async Task Update_StartTimeUnchanged_KeepsSentReminders()
    {
        var created = await CreateAsync(ValidSession(courseId: 52, start: DateTime.UtcNow.AddDays(3)));
        await SeedSentReminderAsync($"{created.Id}:reminder60");

        var sameTime = ValidSession(courseId: 52, courseName: "Renamed", start: created.StartTime);
        sameTime.Id = created.Id;
        var response = await _client.PutAsJsonAsync($"/api/sessions/{created.Id}", sameTime);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await GetSentRemindersAsync($"{created.Id}:reminder"));
    }

    private async Task SeedSentReminderAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLife.Server.Data.StudyLifeDb>();
        db.SentReminders.Add(new StudyLife.Server.Data.SentReminderEntity { Key = key, SentAt = DateTime.Now });
        await db.SaveChangesAsync();
    }

    private async Task<List<StudyLife.Server.Data.SentReminderEntity>> GetSentRemindersAsync(string keyPrefix)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudyLife.Server.Data.StudyLifeDb>();
        return await db.SentReminders.AsNoTracking().Where(r => r.Key.StartsWith(keyPrefix)).ToListAsync();
    }

    // ---------- DELETE /api/sessions/{id} ----------

    [Fact]
    public async Task Delete_ExistingSession_RemovesItFromSubsequentGet()
    {
        var created = await CreateAsync(ValidSession(courseId: 91, start: DateTime.UtcNow.AddDays(2)));

        var getBefore = await _client.GetAsync("/api/sessions");
        var before = await getBefore.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.Contains(before!, s => s.Id == created.Id);

        var deleteResponse = await _client.DeleteAsync($"/api/sessions/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getAfter = await _client.GetAsync("/api/sessions");
        var after = await getAfter.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.DoesNotContain(after!, s => s.Id == created.Id);
    }

    [Fact]
    public async Task Delete_NonExistentId_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/api/sessions/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- Recurring series (DELETE api/sessions/series/{groupId}) ----------
    // The client creates a series by calling POST /api/sessions multiple times with the same
    // client-generated RecurrenceGroupId (see Calendar.SessionDialog.razor.cs) - there is
    // no dedicated "create series" endpoint on the server.

    [Fact]
    public async Task DeleteSeries_WithoutFromDate_RemovesAllOccurrences()
    {
        var groupId = Guid.NewGuid().ToString();
        var occ1 = await CreateAsync(ValidSession(courseId: 101, start: DateTime.UtcNow.AddDays(2), recurrenceGroupId: groupId));
        var occ2 = await CreateAsync(ValidSession(courseId: 101, start: DateTime.UtcNow.AddDays(9), recurrenceGroupId: groupId));
        var occ3 = await CreateAsync(ValidSession(courseId: 101, start: DateTime.UtcNow.AddDays(16), recurrenceGroupId: groupId));

        var deleteResponse = await _client.DeleteAsync($"/api/sessions/series/{groupId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var history = await (await _client.GetAsync("/api/sessions/history?days=400&onlyCompleted=false"))
            .Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.DoesNotContain(history!, s => s.Id == occ1.Id);
        Assert.DoesNotContain(history!, s => s.Id == occ2.Id);
        Assert.DoesNotContain(history!, s => s.Id == occ3.Id);
    }

    [Fact]
    public async Task DeleteSeries_WithFromDate_RemovesOnlyOccurrencesFromThatDateForward()
    {
        var groupId = Guid.NewGuid().ToString();
        var day2 = DateTime.UtcNow.Date.AddDays(2);
        var day9 = DateTime.UtcNow.Date.AddDays(9);
        var day16 = DateTime.UtcNow.Date.AddDays(16);

        var before = await CreateAsync(ValidSession(courseId: 111, start: day2.AddHours(10), recurrenceGroupId: groupId));
        var onBoundary = await CreateAsync(ValidSession(courseId: 111, start: day9.AddHours(10), recurrenceGroupId: groupId));
        var after = await CreateAsync(ValidSession(courseId: 111, start: day16.AddHours(10), recurrenceGroupId: groupId));

        var fromDateParam = day9.ToString("yyyy-MM-dd");
        var deleteResponse = await _client.DeleteAsync($"/api/sessions/series/{groupId}?fromDate={fromDateParam}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var history = await (await _client.GetAsync("/api/sessions/history?days=400&onlyCompleted=false"))
            .Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.Contains(history!, s => s.Id == before.Id);
        // fromDate is inclusive (StartTime.Date >= fromDate.Value.Date) - so the occurrence
        // exactly on the cutoff date must also be deleted.
        Assert.DoesNotContain(history!, s => s.Id == onBoundary.Id);
        Assert.DoesNotContain(history!, s => s.Id == after.Id);
    }

    [Fact]
    public async Task DeleteSeries_UnknownGroupId_ReturnsNoContentAndIsANoop()
    {
        var response = await _client.DeleteAsync($"/api/sessions/series/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---------- GET /api/sessions/ics ----------
    // Own, permanent token per user (AuthUserEntity.CalendarToken, see SystemController
    // and the middleware exception in Program.cs) instead of the rotating API key - _client
    // does already carry a valid X-Api-Key header via ConfigureClient, but it's
    // irrelevant for this route.

    [Fact]
    public async Task GetIcs_WithValidCalendarToken_ReturnsCalendarContainingSessionWithinWindow()
    {
        var created = await CreateAsync(ValidSession(courseId: 121, courseName: "ICS-Kurs", start: DateTime.UtcNow.AddDays(3)));

        var tokenResponse = await _client.GetAsync("/api/system/calendar-token");
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokenDto = await tokenResponse.Content.ReadFromJsonAsync<CalendarTokenResponseDto>();

        var response = await _client.GetAsync($"/api/sessions/ics?calendarToken={tokenDto!.CalendarToken}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/calendar", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.Contains($"UID:studylife-session-{created.Id}@studylife", body);
        Assert.Contains("SUMMARY:ICS-Kurs", body);
    }

    [Fact]
    public async Task GetIcs_WithoutCalendarToken_Returns401()
    {
        var response = await _client.GetAsync("/api/sessions/ics");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetIcs_WithWrongCalendarToken_Returns401()
    {
        var response = await _client.GetAsync("/api/sessions/ics?calendarToken=not-the-token");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetIcs_WithApiKeyQueryParam_DoesNotAuthenticate()
    {
        // Proves that the two secret spaces are separate: the perfectly normal ?apiKey= query
        // parameter (which works on every other /api route) does NOT authenticate this route -
        // only ?calendarToken= counts here. The concrete value is irrelevant for this
        // proof (the route simply never checks ?apiKey=), so a placeholder is enough instead of
        // a real, per-user generated API key.
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var response = await client.GetAsync("/api/sessions/ics?apiKey=some-arbitrary-value");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- POST /api/sessions/import-ics ----------
    // The actual parser has its own, fine-grained unit tests (see
    // IcsImportParserTests) - here just a smoke test over the real HTTP stack (multipart
    // upload, content-type header, response deserialization).

    private static MultipartFormDataContent MakeIcsUpload(string icsContent, string fileName = "lectures.ics")
    {
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(icsContent));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
        return new MultipartFormDataContent { { fileContent, "file", fileName } };
    }

    [Fact]
    public async Task ImportIcs_ValidFileWithTwoEvents_ReturnsParsedCandidatesAndCreatesNoSessions()
    {
        // Unique titles + dates WITHIN the GetAll time window (UtcNow +/-7/90 days) -
        // so the "nothing was created" check below is actually meaningful
        // (outside the window it would be trivially true, regardless of whether the endpoint wrongly creates anything).
        var tag = Guid.NewGuid().ToString("N")[..8];
        var day1 = DateTime.UtcNow.AddDays(5);
        var day2 = DateTime.UtcNow.AddDays(6);
        var ics = "BEGIN:VCALENDAR\r\n"
            + "BEGIN:VEVENT\r\n"
            + $"DTSTART:{day1:yyyyMMdd}T090000\r\n"
            + $"DTEND:{day1:yyyyMMdd}T103000\r\n"
            + $"SUMMARY:Analysis-{tag}\r\n"
            + "END:VEVENT\r\n"
            + "BEGIN:VEVENT\r\n"
            + $"DTSTART:{day2:yyyyMMdd}T130000\r\n"
            + $"DTEND:{day2:yyyyMMdd}T143000\r\n"
            + $"SUMMARY:LinAlg-{tag}\r\n"
            + "END:VEVENT\r\n"
            + "END:VCALENDAR\r\n";

        using var upload = MakeIcsUpload(ics);
        var response = await _client.PostAsync("/api/sessions/import-ics", upload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<IcsImportResultDto>();
        Assert.NotNull(result);
        Assert.Equal(2, result!.Events.Count);
        Assert.Contains(result.Events, e => e.Title == $"Analysis-{tag}");
        Assert.Contains(result.Events, e => e.Title == $"LinAlg-{tag}");

        // Import doesn't create any sessions yet - the endpoint is a pure review preview.
        var sessions = await (await _client.GetAsync("/api/sessions")).Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.DoesNotContain(sessions!, s => s.CourseName == $"Analysis-{tag}" || s.CourseName == $"LinAlg-{tag}");
    }

    [Fact]
    public async Task ImportIcs_NoFile_ReturnsBadRequest()
    {
        using var upload = new MultipartFormDataContent();
        var response = await _client.PostAsync("/api/sessions/import-ics", upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ImportIcs_EmptyFile_ReturnsBadRequest()
    {
        using var upload = MakeIcsUpload("");
        var response = await _client.PostAsync("/api/sessions/import-ics", upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>
/// Security regression test: the ICS calendar token must resolve its REAL owner
/// (AuthUserEntity.CalendarToken), not the phase 1 fallback user ambiently resolved by the gate
/// (always the first AuthUser) - the former global CalendarTokenProvider would have shown every
/// caller the same (the first) calendar, regardless of which user the token
/// actually belongs to. Own factory (fresh DB), because a real two-user situation is needed
/// via the passkey registration flow.
/// </summary>
public class SessionsControllerCalendarMultiUserTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionsControllerCalendarMultiUserTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetIcs_ForSecondRegisteredUser_ReturnsTheirOwnSessions_NotTheFirstUsers()
    {
        using var firstKey = new FakePasskey();
        var alexToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);
        using var secondKey = new FakePasskey();
        var annaToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", secondKey);

        using (var alexCreate = new HttpRequestMessage(HttpMethod.Post, "/api/sessions"))
        {
            alexCreate.Headers.Add("X-Session-Token", alexToken);
            alexCreate.Content = JsonContent.Create(new StudySessionDto
            {
                CourseId = 1,
                CourseName = "Alex-Kurs",
                StartTime = DateTime.UtcNow.AddDays(3),
                EndTime = DateTime.UtcNow.AddDays(3).AddHours(1),
                TimerModeId = 1,
            });
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(alexCreate)).StatusCode);
        }
        using (var annaCreate = new HttpRequestMessage(HttpMethod.Post, "/api/sessions"))
        {
            annaCreate.Headers.Add("X-Session-Token", annaToken);
            annaCreate.Content = JsonContent.Create(new StudySessionDto
            {
                CourseId = 2,
                CourseName = "Anna-Kurs",
                StartTime = DateTime.UtcNow.AddDays(3),
                EndTime = DateTime.UtcNow.AddDays(3).AddHours(1),
                TimerModeId = 1,
            });
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(annaCreate)).StatusCode);
        }

        using var annaTokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/system/calendar-token");
        annaTokenRequest.Headers.Add("X-Session-Token", annaToken);
        var annaCalendarTokenResponse = await _client.SendAsync(annaTokenRequest);
        Assert.Equal(HttpStatusCode.OK, annaCalendarTokenResponse.StatusCode);
        var annaCalendarToken = (await annaCalendarTokenResponse.Content.ReadFromJsonAsync<CalendarTokenResponseDto>())!.CalendarToken;

        var anonymousClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var icsResponse = await anonymousClient.GetAsync($"/api/sessions/ics?calendarToken={annaCalendarToken}");
        Assert.Equal(HttpStatusCode.OK, icsResponse.StatusCode);
        var body = await icsResponse.Content.ReadAsStringAsync();

        Assert.Contains("SUMMARY:Anna-Kurs", body);
        Assert.DoesNotContain("SUMMARY:Alex-Kurs", body);
    }
}

/// <summary>
/// Regression test for the IMemoryCache cross-user leak in GetAll/GetHistory: the cache key
/// consisted only of the global SessionHistoryCacheVersion counter, without AuthUserId - a
/// second user calling the same endpoint within the TTL window without an intervening write
/// got the first user's sessions back (same pattern as
/// SettingsControllerCacheIsolationTests).
/// </summary>
public class SessionsControllerCacheIsolationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SessionsControllerCacheIsolationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAll_ForSecondRegisteredUser_NeverSeesFirstUsersCachedSessions()
    {
        using var firstKey = new FakePasskey();
        var alexToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);
        using var secondKey = new FakePasskey();
        var annaToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", secondKey);

        using (var alexCreate = new HttpRequestMessage(HttpMethod.Post, "/api/sessions"))
        {
            alexCreate.Headers.Add("X-Session-Token", alexToken);
            alexCreate.Content = JsonContent.Create(new StudySessionDto
            {
                CourseId = 1,
                CourseName = "Alex-Cache-Kurs",
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
                TimerModeId = 1,
            });
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(alexCreate)).StatusCode);
        }

        // Populates the cache under Alex' version+user key.
        using (var alexGet = new HttpRequestMessage(HttpMethod.Get, "/api/sessions"))
        {
            alexGet.Headers.Add("X-Session-Token", alexToken);
            var alexResponse = await _client.SendAsync(alexGet);
            var alexSessions = await alexResponse.Content.ReadFromJsonAsync<List<StudySessionDto>>();
            Assert.Contains(alexSessions!, s => s.CourseName == "Alex-Cache-Kurs");
        }

        // Without an intervening write (same cache version) - before the fix, this would have
        // hit the same global cache entry and returned Alex' sessions.
        using var annaGet = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
        annaGet.Headers.Add("X-Session-Token", annaToken);
        var annaResponse = await _client.SendAsync(annaGet);
        Assert.Equal(HttpStatusCode.OK, annaResponse.StatusCode);
        var annaSessions = await annaResponse.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.DoesNotContain(annaSessions!, s => s.CourseName == "Alex-Cache-Kurs");
    }
}
