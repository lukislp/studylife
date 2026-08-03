using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// These tests do NOT check business logic (that's covered elsewhere), but the exact
/// JSON WIRE FORM of the API: property names, casing, presence/absence of fields. The reason
/// was a real, documented bug in BackupController.Export(): the outer wrapper is
/// serialized camelCase, but the nested DTOs in the arrays come out PascalCase,
/// because this one code path manually calls JsonSerializer.Serialize() without a naming
/// policy and thereby bypasses the ASP.NET Core convention that otherwise applies everywhere.
/// No normal assertion test ever noticed this, because nobody checked the exact JSON shape -
/// only that values survived the roundtrip. This file is meant to catch exactly this class of
/// bug and prevent it going forward.
/// </summary>
public class ApiContractCasingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ApiContractCasingTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    /// <summary>
    /// Recursive casing audit: every property name in an object (including nested, including
    /// in arrays) must start with a lowercase letter - that's what ASP.NET Core's
    /// default System.Text.Json configuration produces (CamelCase naming policy), which is
    /// never overridden anywhere in Program.cs, and that's exactly what the Blazor client
    /// expects everywhere except the one known-broken Export() endpoint (see <see cref="BackupExportCasingTests"/>).
    /// </summary>
    private static void AssertAllPropertiesCamelCase(JsonElement element, string path = "$")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    Assert.True(
                        prop.Name.Length > 0 && char.IsLower(prop.Name[0]),
                        $"Property '{prop.Name}' at {path} is not camelCase (first character must be lowercase).");
                    AssertAllPropertiesCamelCase(prop.Value, $"{path}.{prop.Name}");
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    AssertAllPropertiesCamelCase(item, $"{path}[{index}]");
                    index++;
                }
                break;
        }
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        // Deliberately parsed as a raw JsonDocument instead of deserialized into a typed DTO -
        // a typed deserialize would silently hide PascalCase/camelCase mismatches
        // behind System.Text.Json's (case-insensitive) property-matching rules.
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task Sessions_AllPropertiesAreCamelCase()
    {
        var session = new StudySessionDto
        {
            CourseId = 1,
            CourseName = $"CasingAuditCourse-{Guid.NewGuid():N}",
            StartTime = new DateTime(2026, 8, 1, 10, 0, 0),
            EndTime = new DateTime(2026, 8, 1, 11, 0, 0),
            Topic = "Casing-Audit",
            IsCompleted = false,
            TimerModeId = 1,
        };
        var createResponse = await _client.PostAsJsonAsync("/api/sessions", session);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var root = await GetJsonAsync(_client, "/api/sessions");
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() > 0, "Expected at least one real record.");
        AssertAllPropertiesCamelCase(root);
    }

    [Fact]
    public async Task CourseGoals_AllPropertiesAreCamelCase()
    {
        var courseId = Random.Shared.Next(100_000, 999_999);
        var goal = new CourseGoalDto
        {
            CourseId = courseId,
            CourseName = "Casing-Audit-Course",
            TargetDate = new DateTime(2026, 12, 1),
            Grade = 2.0m,
            CompletedTopics = "Topic A,Topic B",
            Tag = "wichtig",
        };
        var putResponse = await _client.PutAsJsonAsync($"/api/coursegoals/{courseId}", goal);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var root = await GetJsonAsync(_client, "/api/coursegoals");
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() > 0, "Expected at least one real record.");
        AssertAllPropertiesCamelCase(root);
    }

    [Fact]
    public async Task Notes_AllPropertiesAreCamelCase()
    {
        var note = new NoteDto { Title = "Casing-Audit-Note", Content = "Inhalt" };
        var createResponse = await _client.PostAsJsonAsync("/api/notes", note);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var root = await GetJsonAsync(_client, "/api/notes");
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() > 0, "Expected at least one real record.");
        AssertAllPropertiesCamelCase(root);
    }

    [Fact]
    public async Task Settings_AllPropertiesAreCamelCase()
    {
        var settings = new UserSettingsDto
        {
            SelectedCourseIds = new List<int> { 1, 2 },
            CompletedCourseIds = new List<int> { 3 },
            Theme = "light",
            WeeklyGoalMinHours = 10,
            WeeklyGoalMaxHours = 20,
            MonthlyGoalMinHours = 40,
            MonthlyGoalMaxHours = 80,
            StudyWindowStartHour = 9,
            StudyWindowEndHour = 18,
        };
        var putResponse = await _client.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var root = await GetJsonAsync(_client, "/api/settings");
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        AssertAllPropertiesCamelCase(root);
    }

    [Fact]
    public async Task Courses_AllPropertiesAreCamelCase()
    {
        // No seeding needed - GET /api/courses always returns the built-in, non-empty
        // catalog (CourseCatalog.AppliedAICourses) when no custom study program is active.
        var root = await GetJsonAsync(_client, "/api/courses");
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() > 0, "Expected at least one real record.");
        AssertAllPropertiesCamelCase(root);
    }

    [Fact]
    public async Task StudyPrograms_AllPropertiesAreCamelCase()
    {
        var request = new CreateStudyProgramRequestDto
        {
            Name = $"CasingAuditProgram-{Guid.NewGuid():N}"[..40],
            Groups = new List<CreateStudyProgramGroupDto>(),
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Semester = 1, Name = "Kurs A", Code = "KA1", Ects = 5 },
            },
        };
        var createResponse = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var root = await GetJsonAsync(_client, "/api/studyprograms");
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        // At least the synthetic built-in entry plus the one just created.
        Assert.True(root.GetArrayLength() >= 2, "Erwarte den eingebauten Eintrag plus den neu angelegten.");
        AssertAllPropertiesCamelCase(root);
    }

    [Fact]
    public async Task TimerState_AllPropertiesAreCamelCase()
    {
        var state = new TimerStateDto
        {
            SessionId = null,
            IsRunning = true,
            IsBreak = false,
            CurrentRound = 1,
            TimerModeId = 1,
            PhaseEndsAt = DateTime.UtcNow.AddMinutes(25),
        };
        var putResponse = await _client.PutAsJsonAsync("/api/timerstate", state);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var root = await GetJsonAsync(_client, "/api/timerstate");
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        AssertAllPropertiesCamelCase(root);
    }
}

/// <summary>
/// Documents an existing bug, not a design decision: BackupController.Export()
/// builds the outer wrapper as an anonymous object with fields already named camelCase
/// (exportedAt, sessions, notes, courseGoals, settings) and serializes it manually via
/// JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }) - WITHOUT
/// a PropertyNamingPolicy. The outer level therefore looks "accidentally" camelCase (because the
/// C# field names in the anonymous object were already written camelCase), but the nested
/// DTO instances (StudySessionDto, NoteDto, CourseGoalDto, UserSettingsDto) have PascalCase
/// C# properties and are serialized exactly as such WITHOUT a naming policy: PascalCase.
///
/// This test deliberately pins the CURRENT (broken) behavior as a tripwire: if the
/// bug is ever fixed (e.g. by passing the same JsonSerializerOptions used elsewhere in the app
/// into this Serialize() call), this test MUST fail and be updated -
/// it should not silently "just turn green again".
///
/// Deliberately NOT touched: BackupController.cs itself (see the task description - another
/// agent is working in parallel on backup/restore code in this area).
/// </summary>
public class BackupExportCasingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public BackupExportCasingTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task Export_KnownBug_NestedDtosAreNotCamelCase()
    {
        var uniqueCourseName = $"ExportCasingBugCourse-{Guid.NewGuid():N}";
        var session = new StudySessionDto
        {
            CourseId = 999,
            CourseName = uniqueCourseName,
            StartTime = new DateTime(2026, 8, 1, 10, 0, 0),
            EndTime = new DateTime(2026, 8, 1, 11, 0, 0),
            Topic = "Export-Casing-Bug-Test",
            IsCompleted = false,
            TimerModeId = 1,
        };
        var createResponse = await _client.PostAsJsonAsync("/api/sessions", session);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var settings = new UserSettingsDto
        {
            SelectedCourseIds = new List<int> { 1 },
            CompletedCourseIds = new List<int>(),
            Theme = "light",
            WeeklyGoalMinHours = 10,
            WeeklyGoalMaxHours = 20,
            MonthlyGoalMinHours = 40,
            MonthlyGoalMaxHours = 80,
            StudyWindowStartHour = 9,
            StudyWindowEndHour = 18,
        };
        var settingsPutResponse = await _client.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, settingsPutResponse.StatusCode);

        var exportResponse = await _client.GetAsync("/api/backup/export");
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);

        var json = await exportResponse.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Outer wrapper: camelCase, like everywhere else in the API.
        foreach (var expectedTopLevelKey in new[] { "exportedAt", "sessions", "notes", "courseGoals", "settings" })
        {
            Assert.True(
                root.TryGetProperty(expectedTopLevelKey, out _),
                $"Erwarte camelCase Top-Level-Key '{expectedTopLevelKey}'.");
        }

        // KNOWN BUG: the session DTOs in the "sessions" array are PascalCase, not camelCase.
        var matchingSession = root.GetProperty("sessions").EnumerateArray()
            .First(s => s.GetProperty("CourseName").GetString() == uniqueCourseName);
        Assert.True(matchingSession.TryGetProperty("CourseName", out _));
        Assert.True(matchingSession.TryGetProperty("Topic", out _));
        Assert.True(matchingSession.TryGetProperty("IsCompleted", out _));
        Assert.True(matchingSession.TryGetProperty("TimerModeId", out _));
        Assert.False(
            matchingSession.TryGetProperty("courseName", out _),
            "courseName (camelCase) is unexpectedly present - looks like the casing bug " +
            "got fixed. This tripwire test then needs to be updated, not just deleted.");

        // KNOWN BUG: applies the same way to the nested "settings" object.
        var settingsElement = root.GetProperty("settings");
        Assert.True(settingsElement.TryGetProperty("Theme", out _));
        Assert.False(
            settingsElement.TryGetProperty("theme", out _),
            "theme (camelCase) is unexpectedly present - looks like the casing bug " +
            "got fixed. This tripwire test then needs to be updated, not just deleted.");
    }
}

/// <summary>
/// Snapshot tests for the exact property NAME SET ("wire contract") of the most important DTOs.
/// Unlike a plain ReadFromJsonAsync&lt;T&gt; (which also silently deserializes successfully with
/// renamed/missing fields, as long as no required fields are affected), explicitly comparing the
/// property name set catches exactly the class of bug "someone renamed/removed/added a
/// DTO property and the wire shape changed unnoticed".
/// </summary>
public class DtoContractSnapshotTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public DtoContractSnapshotTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    private static HashSet<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToHashSet();

    [Fact]
    public async Task StudySessionDto_PropertyNames_MatchExpectedContract()
    {
        // Mirrors StudyLife.Shared.Dtos.StudySessionDto (src/StudyLife.Shared/Dtos.cs).
        var expected = new HashSet<string>
        {
            "id", "courseId", "courseName", "courseColor", "startTime", "endTime",
            "topic", "notes", "isCompleted", "timerModeId", "recurrenceGroupId",
        };

        var session = new StudySessionDto
        {
            CourseId = 1,
            CourseName = "Snapshot-Test-Course",
            StartTime = new DateTime(2026, 9, 1, 10, 0, 0),
            EndTime = new DateTime(2026, 9, 1, 11, 0, 0),
            Topic = "Snapshot-Test",
            Notes = "Notiz",
            IsCompleted = true,
            TimerModeId = 1,
            RecurrenceGroupId = "group-1",
        };
        var createResponse = await _client.PostAsJsonAsync("/api/sessions", session);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()).RootElement;
        var createdId = created.GetProperty("id").GetInt32();

        // Additionally fetch it back via the regular list endpoint, not just
        // checking the POST response - both go through the same ToDto() path, but this way
        // the actual GET serialization is also verified.
        var listResponse = await _client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()).RootElement;
        var fetched = list.EnumerateArray().First(s => s.GetProperty("id").GetInt32() == createdId);

        Assert.Equal(expected, PropertyNames(fetched));
    }

    [Fact]
    public async Task CourseGoalDto_PropertyNames_MatchExpectedContract()
    {
        // Mirrors StudyLife.Shared.Dtos.CourseGoalDto (src/StudyLife.Shared/Dtos.cs).
        var expected = new HashSet<string>
        {
            "courseId", "courseName", "targetDate", "completionNote",
            "completedAt", "grade", "completedTopics", "tag",
        };

        var courseId = Random.Shared.Next(100_000, 999_999);
        var goal = new CourseGoalDto
        {
            CourseId = courseId,
            CourseName = "Snapshot-Test-Course",
            TargetDate = new DateTime(2026, 12, 1),
            CompletionNote = "Fast fertig",
            CompletedAt = new DateTime(2026, 11, 1),
            Grade = 1.7m,
            CompletedTopics = "Thema A",
            Tag = "wichtig",
        };
        var putResponse = await _client.PutAsJsonAsync($"/api/coursegoals/{courseId}", goal);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var listResponse = await _client.GetAsync("/api/coursegoals");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()).RootElement;
        var fetched = list.EnumerateArray().First(g => g.GetProperty("courseId").GetInt32() == courseId);

        Assert.Equal(expected, PropertyNames(fetched));
    }

    [Fact]
    public async Task UserSettingsDto_PropertyNames_MatchExpectedContract()
    {
        // Mirrors StudyLife.Shared.Dtos.UserSettingsDto (src/StudyLife.Shared/Dtos.cs).
        var expected = new HashSet<string>
        {
            "selectedCourseIds", "completedCourseIds", "theme", "accentColor", "autoSwitchFocus",
            "autoSwitchMinutesBefore", "motivationalStyle", "sessionReminderMinutes",
            "courseGoalReminderDays", "inactivityThresholdDays", "studyWindowStartHour",
            "studyWindowEndHour", "studyDays", "targetGraduationDate", "customTimerModes",
            "weeklyGoalMinHours", "weeklyGoalMaxHours", "monthlyGoalMinHours", "monthlyGoalMaxHours",
            "sessionRemindersEnabled", "courseGoalRemindersEnabled", "inactivityRemindersEnabled",
            "achievementNotificationsEnabled", "weeklyReportEnabled", "dailyMotivationEnabled",
            "perCourseInactivityRemindersEnabled", "lastBackupDownloadAt", "activeStudyProgramId",
            "progressShareEnabled", "progressShareToken", "streakRiskRemindersEnabled",
            "weeklyGoalNudgeEnabled", "courseAlmostDoneRemindersEnabled", "bestStudyTimeRemindersEnabled",
            "comebackNudgeEnabled", "newRecordNotificationsEnabled", "monthlyReportEnabled",
        };

        var settings = new UserSettingsDto
        {
            SelectedCourseIds = new List<int> { 1, 2 },
            CompletedCourseIds = new List<int> { 3 },
            Theme = "light",
            WeeklyGoalMinHours = 10,
            WeeklyGoalMaxHours = 20,
            MonthlyGoalMinHours = 40,
            MonthlyGoalMaxHours = 80,
            StudyWindowStartHour = 9,
            StudyWindowEndHour = 18,
        };
        var putResponse = await _client.PutAsJsonAsync("/api/settings", settings);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var getResponse = await _client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(expected, PropertyNames(fetched));
    }

    [Fact]
    public async Task StudyProgramSummaryDto_PropertyNames_MatchExpectedContract()
    {
        // Mirrors StudyLife.Shared.Dtos.StudyProgramSummaryDto (src/StudyLife.Shared/Dtos.cs).
        var expected = new HashSet<string> { "id", "name", "isBuiltIn", "isCompleted" };

        var request = new CreateStudyProgramRequestDto
        {
            Name = $"SnapshotProgram-{Guid.NewGuid():N}"[..40],
            Groups = new List<CreateStudyProgramGroupDto>(),
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Semester = 1, Name = "Kurs A", Code = "KA1", Ects = 5 },
            },
        };
        var createResponse = await _client.PostAsJsonAsync("/api/studyprograms", request);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()).RootElement;
        var createdId = created.GetProperty("id").GetInt32();

        var listResponse = await _client.GetAsync("/api/studyprograms");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()).RootElement;
        var fetched = list.EnumerateArray()
            .First(p => p.GetProperty("id").ValueKind == JsonValueKind.Number && p.GetProperty("id").GetInt32() == createdId);

        Assert.Equal(expected, PropertyNames(fetched));
    }

    [Fact]
    public async Task NoteDto_PropertyNames_MatchExpectedContract()
    {
        // Mirrors StudyLife.Shared.Dtos.NoteDto (src/StudyLife.Shared/Dtos.cs).
        var expected = new HashSet<string>
        {
            "id", "title", "content", "createdAt", "updatedAt", "courseId", "sessionId",
        };

        var note = new NoteDto { Title = "Snapshot-Test-Note", Content = "Inhalt", CourseId = 1 };
        var createResponse = await _client.PostAsJsonAsync("/api/notes", note);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()).RootElement;
        var createdId = created.GetProperty("id").GetInt32();

        var listResponse = await _client.GetAsync("/api/notes");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()).RootElement;
        var fetched = list.EnumerateArray().First(n => n.GetProperty("id").GetInt32() == createdId);

        Assert.Equal(expected, PropertyNames(fetched));
    }

    [Fact]
    public async Task TimerStateDto_PropertyNames_MatchExpectedContract()
    {
        // Mirrors StudyLife.Shared.Dtos.TimerStateDto (src/StudyLife.Shared/Dtos.cs).
        // serverNow is only set in the GET response (server clock anchor for the
        // remote timer banner on the focus page).
        var expected = new HashSet<string>
        {
            "sessionId", "isRunning", "isBreak", "currentRound", "timerModeId", "phaseEndsAt", "updatedAt",
            "serverNow",
        };

        var state = new TimerStateDto
        {
            SessionId = 1,
            IsRunning = true,
            IsBreak = false,
            CurrentRound = 2,
            TimerModeId = 1,
            PhaseEndsAt = DateTime.UtcNow.AddMinutes(10),
        };
        var putResponse = await _client.PutAsJsonAsync("/api/timerstate", state);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var getResponse = await _client.GetAsync("/api/timerstate");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(expected, PropertyNames(fetched));
        // The anchor must be close to "now" - rough plausibility instead of an exact clock.
        var serverNow = fetched.GetProperty("serverNow").GetDateTime();
        Assert.True(Math.Abs((DateTime.Now - serverNow).TotalMinutes) < 5);
    }
}

/// <summary>
/// Program.cs stamps a blanket Cache-Control: no-store on every /api/* response, specifically to
/// override CacheHelper.SetHeaders (SessionsController/SettingsController/CoursesController),
/// which sets its own "private, no-cache"/"private, max-age=..." from deeper in the pipeline.
/// This exact interaction broke twice in production before this test existed: a header set BEFORE
/// next() was silently overwritten by CacheHelper's later assignment, and a header set AFTER
/// next() but via a plain assignment still lost the race because a small buffered JSON body is
/// already flushed (HasStarted == true) by the time control returns from next() - only
/// Response.OnStarting() reliably wins. Both bugs shipped and were only caught by manual curl
/// testing against the live API; this test exists so the wire-level result is pinned going
/// forward instead of relying on remembering the interaction.
/// </summary>
public class CacheControlHeaderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CacheControlHeaderTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task SessionsHistory_CacheControlIsNoStore_OverridesCacheHelpersOwnHeader()
    {
        var response = await _client.GetAsync("/api/sessions/history?days=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.NoStore, "Expected Cache-Control: no-store, not CacheHelper's own 'private, no-cache'.");
        Assert.False(response.Headers.CacheControl.NoCache);
        Assert.False(response.Headers.CacheControl.Private);
    }

    [Fact]
    public async Task Courses_CacheControlIsNoStore_OverridesCacheHelpersOwnMaxAge()
    {
        var response = await _client.GetAsync("/api/courses");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.NoStore, "Expected Cache-Control: no-store, not CacheHelper's own 'private, max-age=...'.");
        Assert.Null(response.Headers.CacheControl.MaxAge);
    }

    [Fact]
    public async Task Settings_CacheControlIsNoStore_OverridesCacheHelpersOwnHeader()
    {
        var response = await _client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.NoStore, "Expected Cache-Control: no-store, not CacheHelper's own 'private, no-cache'.");
    }
}

/// <summary>
/// Verifies that nullable DTO fields APPEAR as a JSON key with value null when null, instead of
/// being omitted. Program.cs never registers JsonOptions/JsonSerializerOptions anywhere
/// (see src/StudyLife.Server/Program.cs) - so ASP.NET Core's default for
/// System.Text.Json applies: DefaultIgnoreCondition.Never, i.e. nulls are serialized. This is
/// deliberately verified here against the actually configured (non-)configuration instead of
/// assumed. A client that expects a key to always be present (even as null) would
/// otherwise break silently if this default ever changes.
/// </summary>
public class NullableFieldPresenceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public NullableFieldPresenceTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task CourseGoalDto_NullNullableFields_AreSerializedAsJsonNull()
    {
        var courseId = Random.Shared.Next(100_000, 999_999);
        var goal = new CourseGoalDto
        {
            CourseId = courseId,
            CourseName = "Nullable-Fields-Course",
            TargetDate = null,
            CompletionNote = null,
            CompletedAt = null,
            Grade = null,
            CompletedTopics = "",
            Tag = null,
        };
        var putResponse = await _client.PutAsJsonAsync($"/api/coursegoals/{courseId}", goal);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var root = JsonDocument.Parse(await putResponse.Content.ReadAsStringAsync()).RootElement;
        foreach (var nullableKey in new[] { "targetDate", "completionNote", "completedAt", "grade", "tag" })
        {
            Assert.True(root.TryGetProperty(nullableKey, out var prop), $"Key '{nullableKey}' fehlt komplett.");
            Assert.Equal(JsonValueKind.Null, prop.ValueKind);
        }
    }

    [Fact]
    public async Task NoteDto_NullNullableFields_AreSerializedAsJsonNull()
    {
        var note = new NoteDto
        {
            Title = "Nullable-Fields-Note",
            Content = "Inhalt",
            CourseId = null,
            SessionId = null,
        };
        var createResponse = await _client.PostAsJsonAsync("/api/notes", note);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var root = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()).RootElement;
        foreach (var nullableKey in new[] { "courseId", "sessionId" })
        {
            Assert.True(root.TryGetProperty(nullableKey, out var prop), $"Key '{nullableKey}' fehlt komplett.");
            Assert.Equal(JsonValueKind.Null, prop.ValueKind);
        }
    }

    [Fact]
    public async Task StudySessionDto_NullNullableFields_AreSerializedAsJsonNull()
    {
        var session = new StudySessionDto
        {
            CourseId = 1,
            CourseName = "Nullable-Fields-Session-Course",
            StartTime = new DateTime(2026, 9, 1, 10, 0, 0),
            EndTime = new DateTime(2026, 9, 1, 11, 0, 0),
            Topic = null,
            Notes = null,
            RecurrenceGroupId = null,
            IsCompleted = false,
            TimerModeId = 1,
        };
        var createResponse = await _client.PostAsJsonAsync("/api/sessions", session);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var root = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()).RootElement;
        foreach (var nullableKey in new[] { "topic", "notes", "recurrenceGroupId" })
        {
            Assert.True(root.TryGetProperty(nullableKey, out var prop), $"Key '{nullableKey}' fehlt komplett.");
            Assert.Equal(JsonValueKind.Null, prop.ValueKind);
        }
    }
}
