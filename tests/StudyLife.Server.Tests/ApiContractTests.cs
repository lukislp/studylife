using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// These tests do NOT check business logic (that's covered elsewhere), but the exact
/// JSON WIRE FORM of the API: property names, casing, presence/absence of fields. The reason
/// was a real, documented bug in BackupController.Export() (audit finding M4(a)): the outer
/// wrapper was serialized camelCase, but the nested DTOs in the arrays came out PascalCase,
/// because that one code path manually called JsonSerializer.Serialize() with a hand-rolled
/// JsonSerializerOptions (no naming policy) instead of reusing the app's shared MVC JsonOptions,
/// bypassing the ASP.NET Core convention that otherwise applies everywhere. No normal assertion
/// test ever noticed this, because nobody checked the exact JSON shape - only that values
/// survived the roundtrip. Now fixed (Export() serializes with the same JsonSerializerOptions
/// instance the framework itself uses, see BackupController._jsonOptions) - see
/// <see cref="BackupExportCasingTests"/> for the export-specific regression test. This file is
/// meant to catch exactly this class of bug and prevent it going forward.
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
    /// expects everywhere, INCLUDING the export endpoint now (see <see cref="BackupExportCasingTests"/>).
    /// Internal instead of private: reused by BackupExportCasingTests below.
    /// </summary>
    internal static void AssertAllPropertiesCamelCase(JsonElement element, string path = "$")
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

    // Internal instead of private: reused by BackupExportCasingTests below.
    internal static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
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
            StartTime = DateTime.Today.AddDays(-1).AddHours(10),
            EndTime = DateTime.Today.AddDays(-1).AddHours(11),
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
        var courseId = 1; // audit finding M2: must be a real catalog id now (CourseId is validated)
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
/// Regression test for a FIXED bug (audit finding M4(a), formerly a deliberate tripwire pinning
/// the broken behavior): BackupController.Export() used to build the outer wrapper as an
/// anonymous object with fields already named camelCase (exportedAt, sessions, notes,
/// courseGoals, settings) and serialize it manually via JsonSerializer.Serialize(export, new
/// JsonSerializerOptions { WriteIndented = true }) - WITHOUT a PropertyNamingPolicy. The outer
/// level therefore looked "accidentally" camelCase (the C# field names in the anonymous object
/// were already written camelCase), but the nested DTO instances (StudySessionDto, NoteDto,
/// CourseGoalDto, UserSettingsDto) have PascalCase C# properties and were serialized exactly as
/// such: PascalCase - a wire-format divergence from every other endpoint in the app.
///
/// Fixed by reusing the exact JsonSerializerOptions instance the MVC pipeline itself uses for
/// every other controller (BackupController._jsonOptions, injected via IOptions&lt;JsonOptions&gt;)
/// instead of constructing a divergent one. This test now pins the FIXED behavior: every
/// property, at every nesting level, must be camelCase - if this regresses, the test fails.
/// </summary>
public class BackupExportCasingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public BackupExportCasingTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task Export_AllPropertiesAreCamelCase()
    {
        // CourseName is irrelevant here - audit finding M2: POST /api/sessions now derives it
        // server-side from the resolved catalog course, so the marker for finding this session
        // again below lives in Topic instead.
        var uniqueTopic = $"Export-Casing-Test-{Guid.NewGuid():N}";
        var session = new StudySessionDto
        {
            CourseId = 2,
            CourseName = "irrelevant",
            StartTime = DateTime.Today.AddDays(-1).AddHours(10),
            EndTime = DateTime.Today.AddDays(-1).AddHours(11),
            Topic = uniqueTopic,
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

        var root = await ApiContractCasingTests.GetJsonAsync(_client, "/api/backup/export");

        // Outer wrapper: camelCase, like everywhere else in the API.
        foreach (var expectedTopLevelKey in new[]
        {
            "formatVersion", "exportedAt", "appVersion", "sessions", "notes", "courseGoals",
            "courseResources", "settings", "studyPrograms", "courseGroups", "customCourses",
            "sessionTemplates",
        })
        {
            Assert.True(
                root.TryGetProperty(expectedTopLevelKey, out _),
                $"Expected camelCase top-level key '{expectedTopLevelKey}'.");
        }

        // FIXED: the session DTOs in the "sessions" array are camelCase now, not PascalCase.
        var matchingSession = root.GetProperty("sessions").EnumerateArray()
            .First(s => s.GetProperty("topic").GetString() == uniqueTopic);
        Assert.True(matchingSession.TryGetProperty("courseName", out _));
        Assert.True(matchingSession.TryGetProperty("topic", out _));
        Assert.True(matchingSession.TryGetProperty("isCompleted", out _));
        Assert.True(matchingSession.TryGetProperty("timerModeId", out _));
        Assert.False(
            matchingSession.TryGetProperty("CourseName", out _),
            "CourseName (PascalCase) is unexpectedly present - the casing bug is back.");

        // FIXED: applies the same way to the nested "settings" object.
        var settingsElement = root.GetProperty("settings");
        Assert.True(settingsElement.TryGetProperty("theme", out _));
        Assert.False(
            settingsElement.TryGetProperty("Theme", out _),
            "Theme (PascalCase) is unexpectedly present - the casing bug is back.");

        // Full recursive audit, including the newly added tables (studyPrograms/courseGroups/
        // customCourses/sessionTemplates) - not just the two spot-checked objects above.
        ApiContractCasingTests.AssertAllPropertiesCamelCase(root);
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
            StartTime = DateTime.Today.AddDays(-1).AddHours(10),
            EndTime = DateTime.Today.AddDays(-1).AddHours(11),
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

        var courseId = 1; // audit finding M2: must be a real catalog id now (CourseId is validated)
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
            "version",
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
            "telemetryConsent",
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
            "id", "title", "content", "createdAt", "updatedAt", "courseId", "sessionId", "isMarkdown", "sourceUrl",
            "tags", "summary", "relatedNoteIds",
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
        // remote timer banner on the focus page). clientSequence (audit S6): optional
        // client-assigned send order for out-of-order rejection - see TimerStateController.Save.
        var expected = new HashSet<string>
        {
            "sessionId", "isRunning", "isBreak", "currentRound", "timerModeId", "phaseEndsAt", "updatedAt",
            "serverNow", "clientSequence",
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
/// Pins the split the blanket /api "no-store" middleware (Program.cs) has to respect: an endpoint
/// that sets its own Cache-Control through CacheHelper keeps it (revalidatable "private,
/// no-cache", or "private, max-age" for the immutable course catalog) so the browser can hold
/// the ETag and get 304s, while everything without an explicit directive falls back to
/// no-store. The previous version of this class asserted the opposite - no-store overriding
/// CacheHelper - which was the bug the 2026-09 audit found (L1): it silently disabled every 304.
/// The heuristic-caching incident that motivated the middleware only ever concerned responses
/// WITHOUT an explicit directive; those still get no-store (see ApiCacheHeaderTests).
/// </summary>
public class CacheControlHeaderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CacheControlHeaderTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task SessionsHistory_KeepsCacheHelpersRevalidatableHeader()
    {
        var response = await _client.GetAsync("/api/sessions/history?days=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoCache);
        Assert.False(response.Headers.CacheControl.NoStore, "no-store must not override CacheHelper's 'private, no-cache' - it kills ETag/304.");
    }

    [Fact]
    public async Task Courses_KeepsCacheHelpersMaxAge()
    {
        var response = await _client.GetAsync("/api/courses");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.NotNull(response.Headers.CacheControl.MaxAge);
        Assert.False(response.Headers.CacheControl.NoStore);
    }

    [Fact]
    public async Task Settings_KeepsCacheHelpersRevalidatableHeader()
    {
        var response = await _client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.False(response.Headers.CacheControl.NoStore);
    }

    [Fact]
    public async Task UnmatchedApiPath_StillGetsNoStore()
    {
        var response = await _client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
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
        var courseId = 1; // audit finding M2: must be a real catalog id now (CourseId is validated)
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
            StartTime = DateTime.Today.AddDays(-1).AddHours(10),
            EndTime = DateTime.Today.AddDays(-1).AddHours(11),
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
