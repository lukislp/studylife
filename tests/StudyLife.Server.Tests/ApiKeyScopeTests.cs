using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Audit finding A6 round 2: per-slot endpoint scoping (Auth/ApiKeyScopes.cs +
/// Auth/ApiKeyScopeAuthorizationHandler.cs). Pins the scope matrix at the real HTTP level - one
/// allowed-endpoint 200 and one out-of-scope 403 per slot, plus the session/missing-credential
/// carve-outs. "Whoami reachable for every slot" is already pinned by WhoamiTests
/// (Whoami_WithApiKey_ReturnsUserIdAndMatchingSlot, unmodified by this change - every slot's
/// ApiKeyScopes entry includes it) and therefore not duplicated here.
/// </summary>
public class ApiKeyScopeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _sessionClient;

    public ApiKeyScopeTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _sessionClient = factory.CreateClient();
    }

    private async Task<string> GenerateKeyAsync(string slot)
    {
        var response = await _sessionClient.PostAsync($"/api/settings/{slot}-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiKey = (await response.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("apiKey").GetString();
        Assert.False(string.IsNullOrEmpty(apiKey));
        return apiKey!;
    }

    private Task RevokeKeyAsync(string slot) => _sessionClient.PostAsync($"/api/settings/{slot}-api-key/revoke", null);

    private static StudySessionDto ValidSession() => new()
    {
        CourseId = 1,
        CourseName = "Analysis 1",
        CourseColor = "#6C5CE7",
        StartTime = DateTime.UtcNow.AddDays(1),
        EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
        Topic = "Scope test",
        IsCompleted = false,
        TimerModeId = 1,
    };

    // ---------- ha (studylife-hacs: widest slot - sessions/settings/notes/coursegoals/courses/
    // studyprograms/timerstate reads plus session/coursegoal/settings/exam-plan writes) ----------

    [Fact]
    public async Task Ha_AllowedEndpoint_GetTimerState_ReturnsOk()
    {
        var apiKey = await GenerateKeyAsync("ha");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/timerstate");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await RevokeKeyAsync("ha");
    }

    [Fact]
    public async Task Ha_AllowedEndpoint_GetStudyProgramDetail_ReturnsOk()
    {
        // Audit finding D4 (studylife-hacs fix/coordinator-week-bound-and-ects): the coordinator
        // now fetches this endpoint once per poll cycle for the currently active CUSTOM study
        // programme, to get the authoritative elective-group ECTS quotas instead of regex-parsing
        // the group's display name. Needs a real custom programme to fetch the detail of - created
        // via the (unscoped) session client, then read back with the ha key.
        var createResponse = await _sessionClient.PostAsJsonAsync("/api/studyprograms", new CreateStudyProgramRequestDto
        {
            Name = $"Scope test {Guid.NewGuid():N}",
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Semester = 1, Name = "Grundlagenkurs", Ects = 5 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(created?.Id);

        var apiKey = await GenerateKeyAsync("ha");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync($"/api/studyprograms/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await RevokeKeyAsync("ha");
    }

    [Fact]
    public async Task Ha_AllowedEndpoint_GetMetricsSummary_ReturnsOk()
    {
        // Metrics API (docs/api/metrics-contract-v1): the coordinator polls this once per cycle
        // instead of computing streak/quota/forecast/... itself - see ApiKeyScopes.Ha's
        // Metrics.GetSummary entry.
        var apiKey = await GenerateKeyAsync("ha");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/metrics/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await RevokeKeyAsync("ha");
    }

    [Fact]
    public async Task Ha_AllowedEndpoint_GetMetricsAchievements_ReturnsOk()
    {
        var apiKey = await GenerateKeyAsync("ha");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/metrics/achievements");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await RevokeKeyAsync("ha");
    }

    [Fact]
    public async Task Ha_OutOfScopeEndpoint_CreateNote_ReturnsForbidden()
    {
        // The HA integration never creates notes (that's capture's job, see api.py/services.py) -
        // this is precisely the kind of cross-slot reach the audit finding called out.
        var apiKey = await GenerateKeyAsync("ha");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.PostAsJsonAsync("/api/notes", new NoteDto { Title = "x", Content = "y" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await RevokeKeyAsync("ha");
    }

    // ---------- ai (studylife-ai ingestion worker: notes/courses/sessions/goals reads, session/
    // note writes as agent-tool side effects - NOT the same as the SessionOnly /api/ai/* proxy) ----------

    [Fact]
    public async Task Ai_AllowedEndpoint_CreateSession_ReturnsOk()
    {
        var apiKey = await GenerateKeyAsync("ai");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.PostAsJsonAsync("/api/sessions", ValidSession());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await RevokeKeyAsync("ai");
    }

    [Fact]
    public async Task Ai_OutOfScopeEndpoint_GetTimerState_ReturnsForbidden()
    {
        // studylife-ai's client never touches /api/timerstate - that's HA-only.
        var apiKey = await GenerateKeyAsync("ai");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/timerstate");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await RevokeKeyAsync("ai");
    }

    // ---------- mcp (studylife-mcp tool server: courses/notes(+search)/sessions/goals reads,
    // note/session writes) ----------

    [Fact]
    public async Task Mcp_AllowedEndpoint_SearchNotes_ReturnsOk()
    {
        var apiKey = await GenerateKeyAsync("mcp");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/notes/search?q=test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await RevokeKeyAsync("mcp");
    }

    [Fact]
    public async Task Mcp_OutOfScopeEndpoint_SaveSettings_ReturnsForbidden()
    {
        // studylife-mcp never manages account settings - that's HA's set_active_program path.
        var apiKey = await GenerateKeyAsync("mcp");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.PutAsJsonAsync("/api/settings", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await RevokeKeyAsync("mcp");
    }

    // ---------- capture (browser extension: deliberately the narrowest slot - only creating a
    // note and verifying its own credentials, see the audit finding this task fixes) ----------

    [Fact]
    public async Task Capture_AllowedEndpoint_CreateNote_ReturnsOk()
    {
        var apiKey = await GenerateKeyAsync("capture");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.PostAsJsonAsync("/api/notes", new NoteDto { Title = "Captured", Content = "Body" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await RevokeKeyAsync("capture");
    }

    [Fact]
    public async Task Capture_OutOfScopeEndpoint_ListSessions_ReturnsForbidden()
    {
        // This is exactly the finding: before this change, a leaked capture key could read
        // every session, not just create notes.
        var apiKey = await GenerateKeyAsync("capture");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await RevokeKeyAsync("capture");
    }

    [Fact]
    public async Task Capture_OutOfScopeEndpoint_GetMetricsSummary_ReturnsForbidden()
    {
        // The browser extension never needs streak/quota/forecast/etc. - only ha's ApiKeyScopes
        // entry includes the metrics endpoints (see the Ha_AllowedEndpoint_GetMetricsSummary/
        // GetMetricsAchievements tests above).
        var apiKey = await GenerateKeyAsync("capture");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/metrics/summary");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await RevokeKeyAsync("capture");
    }

    // ---------- focusguard (distraction-blocker browser extension: THE narrowest slot -
    // only polling whether a session is currently running, see ApiKeyScopes.FocusGuard) ----------

    [Fact]
    public async Task FocusGuard_AllowedEndpoint_GetTimerState_ReturnsOk()
    {
        var apiKey = await GenerateKeyAsync("focusguard");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/timerstate");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await RevokeKeyAsync("focusguard");
    }

    [Fact]
    public async Task FocusGuard_OutOfScopeEndpoint_ListNotes_ReturnsForbidden()
    {
        // The blocker extension never reads note content - it only ever needs to know whether a
        // session is currently running, nothing about what it's for.
        var apiKey = await GenerateKeyAsync("focusguard");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await RevokeKeyAsync("focusguard");
    }

    [Fact]
    public async Task FocusGuard_OutOfScopeEndpoint_ListSessions_ReturnsForbidden()
    {
        var apiKey = await GenerateKeyAsync("focusguard");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await RevokeKeyAsync("focusguard");
    }

    [Fact]
    public async Task FocusGuard_OutOfScopeEndpoint_SaveTimerState_ReturnsForbidden()
    {
        // Read-only slot: the extension must never be able to WRITE the timer state either,
        // only poll it.
        var apiKey = await GenerateKeyAsync("focusguard");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.PutAsJsonAsync("/api/timerstate", new TimerStateDto());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await RevokeKeyAsync("focusguard");
    }

    // ---------- focustunes (focus-session music companion browser extension: same narrowest
    // shape as focusguard - only polls whether a session is running, see
    // ApiKeyScopes.FocusTunes) ----------

    [Fact]
    public async Task FocusTunes_AllowedEndpoint_GetTimerState_ReturnsOk()
    {
        var apiKey = await GenerateKeyAsync("focustunes");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/timerstate");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await RevokeKeyAsync("focustunes");
    }

    [Fact]
    public async Task FocusTunes_OutOfScopeEndpoint_ListNotes_ReturnsForbidden()
    {
        var apiKey = await GenerateKeyAsync("focustunes");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await RevokeKeyAsync("focustunes");
    }

    [Fact]
    public async Task FocusTunes_OutOfScopeEndpoint_SaveTimerState_ReturnsForbidden()
    {
        var apiKey = await GenerateKeyAsync("focustunes");
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        var response = await client.PutAsJsonAsync("/api/timerstate", new TimerStateDto());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await RevokeKeyAsync("focustunes");
    }

    // ---------- credential-kind carve-outs ----------

    [Fact]
    public async Task SessionCredential_ReachesEndpointOutsideEveryApiKeySlot_RegardlessOfScopeMap()
    {
        // No slot's ApiKeyScopes entry includes GET /api/studyprograms - the browser client
        // (session-authenticated) must still reach it unconditionally, proving the scope map is
        // a pure no-op for session credentials.
        var response = await _sessionClient.GetAsync("/api/studyprograms");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MissingCredential_StaysUnauthorized_NotForbidden()
    {
        // No credential at all must still be a 401 Challenge - never the 403 an authenticated
        // but out-of-scope key gets.
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var response = await anon.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InvalidApiKey_StaysUnauthorized_NotForbidden()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, "not-a-real-key");

        var response = await client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>
/// Security:EnforceKeyScopes=false rollout escape hatch (ApiKeyScopeAuthorizationHandler) - a
/// would-be-denied request must still pass through unchanged, but log a Warning naming the slot
/// and the denied endpoint, so the matrix can be validated against real traffic before
/// enforcement is turned on. Own factory (needs a different Security:EnforceKeyScopes setting
/// and a custom logger provider), not shared via the class fixture used above.
/// </summary>
public class ApiKeyScopeLogOnlyModeTests : IDisposable
{
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Category, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose() { }

        private sealed class CapturingLogger(string category, List<(LogLevel, string, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (entries) entries.Add((logLevel, category, formatter(state, exception)));
            }
        }
    }

    private sealed class LogOnlyModeFactory : CustomWebApplicationFactory
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Security:EnforceKeyScopes", "false");
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        }
    }

    private readonly LogOnlyModeFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task OutOfScopeRequest_PassesThroughAsOk_ButLogsWarning()
    {
        var sessionClient = _factory.CreateClient();
        var generateResponse = await sessionClient.PostAsync("/api/settings/capture-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var apiKey = (await generateResponse.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("apiKey").GetString();

        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey);

        // capture has no Sessions.GetAll entry - would be a 403 under enforcement (see
        // ApiKeyScopeTests.Capture_OutOfScopeEndpoint_ListSessions_ReturnsForbidden).
        var response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.Logs.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Category.Contains("ApiKeyScopeAuthorizationHandler") &&
            e.Message.Contains("capture") &&
            e.Message.Contains("Sessions.GetAll"));
    }
}
