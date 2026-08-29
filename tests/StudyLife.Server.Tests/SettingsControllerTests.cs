using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Own class (= own factory/DB thanks to IClassFixture), because this test requires a completely
/// untouched DB - the mutating PUT tests below would otherwise overwrite the defaults
/// depending on xUnit execution order.
/// </summary>
public class SettingsControllerFreshDbTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SettingsControllerFreshDbTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task Get_OnFreshDatabase_ReturnsOkWithDefaults()
    {
        var response = await _client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.NotNull(dto);
        Assert.Equal("dark", dto!.Theme);
        Assert.Equal(new List<int> { 1, 2, 3, 4 }, dto.SelectedCourseIds);
        Assert.Empty(dto.CompletedCourseIds);
        Assert.Equal(25, dto.WeeklyGoalMinHours);
        Assert.Equal(30, dto.WeeklyGoalMaxHours);
        Assert.Equal(100, dto.MonthlyGoalMinHours);
        Assert.Equal(130, dto.MonthlyGoalMaxHours);
        Assert.Equal(8, dto.StudyWindowStartHour);
        Assert.Equal(21, dto.StudyWindowEndHour);
    }
}

public class SettingsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>The client always sends the complete settings object on PUT - same here.</summary>
    private static UserSettingsDto ValidSettings() => new()
    {
        SelectedCourseIds = new List<int> { 2, 3, 5 },
        CompletedCourseIds = new List<int> { 1 },
        Theme = "light",
        WeeklyGoalMinHours = 10,
        WeeklyGoalMaxHours = 20,
        MonthlyGoalMinHours = 40,
        MonthlyGoalMaxHours = 80,
        StudyWindowStartHour = 9,
        StudyWindowEndHour = 18,
    };

    [Fact]
    public async Task Put_ValidSettings_PersistsAndIsReflectedInGet()
    {
        var putResponse = await _client.PutAsJsonAsync("/api/settings", ValidSettings());
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        // The PUT invalidates the 15s settings cache via SettingsCacheVersion,
        // so the following GET must see the new values immediately.
        var getResponse = await _client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var dto = await getResponse.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.NotNull(dto);
        Assert.Equal("light", dto!.Theme);
        Assert.Equal(new List<int> { 2, 3, 5 }, dto.SelectedCourseIds);
        Assert.Equal(new List<int> { 1 }, dto.CompletedCourseIds);
        Assert.Equal(10, dto.WeeklyGoalMinHours);
        Assert.Equal(20, dto.WeeklyGoalMaxHours);
        Assert.Equal(40, dto.MonthlyGoalMinHours);
        Assert.Equal(80, dto.MonthlyGoalMaxHours);
    }

    [Fact]
    public async Task Put_WeeklyGoalOutOfRange_ReturnsBadRequest()
    {
        var dto = ValidSettings();
        dto.WeeklyGoalMinHours = 0; // allowed range is 1-100

        var response = await _client.PutAsJsonAsync("/api/settings", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_MonthlyGoalMaxNotGreaterThanMin_ReturnsBadRequest()
    {
        var dto = ValidSettings();
        dto.MonthlyGoalMinHours = 80;
        dto.MonthlyGoalMaxHours = 80; // Max must be strictly greater than Min

        var response = await _client.PutAsJsonAsync("/api/settings", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_StudyWindowEndBeforeStart_ReturnsBadRequest()
    {
        var dto = ValidSettings();
        dto.StudyWindowStartHour = 18;
        dto.StudyWindowEndHour = 9;

        var response = await _client.PutAsJsonAsync("/api/settings", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_CustomTimerModesOver4000Chars_ReturnsBadRequest()
    {
        var dto = ValidSettings();
        dto.CustomTimerModes = new string('x', 4001); // length guard for the singleton row

        var response = await _client.PutAsJsonAsync("/api/settings", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_UnknownActiveStudyProgramId_ReturnsBadRequest()
    {
        var dto = ValidSettings();
        dto.ActiveStudyProgramId = 999_999; // null = built-in program; otherwise must exist

        var response = await _client.PutAsJsonAsync("/api/settings", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_NewlySetPastTargetGraduationDate_ReturnsBadRequest()
    {
        // Only *newly set* past dates are rejected - an already stored date may elapse and
        // keep being sent back by the client (full-object PUT), see the controller comment.
        var dto = ValidSettings();
        dto.TargetGraduationDate = DateTime.Today.AddDays(-1);

        var response = await _client.PutAsJsonAsync("/api/settings", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TestDatabase_IsIsolatedFromRealAppDataDatabase()
    {
        // The factory must point to a temp DB, not to app_data/studylife.db in the repo.
        var env = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        var realDbPath = Path.Combine(env.ContentRootPath, "app_data", "studylife.db");
        Assert.NotEqual(Path.GetFullPath(realDbPath), Path.GetFullPath(_factory.DbPath));

        var realDbTimestampBefore = File.Exists(realDbPath)
            ? File.GetLastWriteTimeUtc(realDbPath)
            : (DateTime?)null;

        // A real write via the API ...
        var response = await _client.PutAsJsonAsync("/api/settings", ValidSettings());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ... lands in the temp DB (whose existence also proves that Program.cs's
        // Migrate() block ran against the temp DB at host startup) ...
        Assert.True(File.Exists(_factory.DbPath));

        // ... while the real dev DB stays untouched (if it exists at all).
        if (realDbTimestampBefore.HasValue)
            Assert.Equal(realDbTimestampBefore.Value, File.GetLastWriteTimeUtc(realDbPath));
    }
}

/// <summary>
/// Regression test for the IMemoryCache cross-user leak: the GET cache key consisted only of
/// the global SettingsCacheVersion counter, without AuthUserId. Two users calling the same
/// endpoint within the 15s TTL window without an intervening write got the same cache row - the
/// second caller saw the first one's settings (including ProgressShareToken). Own
/// class/factory, so no other test writes in between and bumps the version counter.
/// </summary>
public class SettingsControllerCacheIsolationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerCacheIsolationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_ForSecondRegisteredUser_NeverSeesFirstUsersCachedSettings()
    {
        using var firstKey = new FakePasskey();
        var alexToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);
        using var secondKey = new FakePasskey();
        var annaToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", secondKey);

        using (var alexPut = new HttpRequestMessage(HttpMethod.Put, "/api/settings"))
        {
            alexPut.Headers.Add("X-Session-Token", alexToken);
            var dto = new UserSettingsDto { WeeklyGoalMinHours = 42, WeeklyGoalMaxHours = 50 };
            alexPut.Content = JsonContent.Create(dto);
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(alexPut)).StatusCode);
        }

        // Populates the cache under Alex' version+user key.
        using (var alexGet = new HttpRequestMessage(HttpMethod.Get, "/api/settings"))
        {
            alexGet.Headers.Add("X-Session-Token", alexToken);
            var alexResponse = await _client.SendAsync(alexGet);
            var alexDto = await alexResponse.Content.ReadFromJsonAsync<UserSettingsDto>();
            Assert.Equal(42, alexDto!.WeeklyGoalMinHours);
        }

        // Without an intervening write (same cache version) - before the fix, this would have
        // hit the same global cache entry and returned Alex' data.
        using var annaGet = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        annaGet.Headers.Add("X-Session-Token", annaToken);
        var annaResponse = await _client.SendAsync(annaGet);
        Assert.Equal(HttpStatusCode.OK, annaResponse.StatusCode);
        var annaDto = await annaResponse.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.Equal(25, annaDto!.WeeklyGoalMinHours); // Anna's own, unchanged default - not Alex' 42.
        Assert.Equal(30, annaDto.WeeklyGoalMaxHours);
    }
}

/// <summary>
/// Per-user Home Assistant API key (GET/generate/revoke /api/settings/ha-api-key): full
/// lifecycle through the real gate. All three endpoints require a REAL passkey/test session
/// (SessionItemKey) - a caller authenticated ONLY via the API key itself must get 401,
/// otherwise a leaked key could reissue or revoke itself. One scenario fact: the steps
/// (no key -> generate -> key works at the gate -> revoke -> key dead) causally build on
/// each other and xUnit guarantees no order between separate facts. Own class/factory,
/// because generate/revoke mutate AuthUser 1's ApiKeyHash.
/// </summary>
public class SettingsControllerHaApiKeyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerHaApiKeyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // carries the seeded test user's session token
    }

    [Fact]
    public async Task HaApiKeyLifecycle_StatusGenerateGateRevoke()
    {
        // ── Fresh state: no key ─────────────────────────────────────────────────────────────
        var status = await _client.GetFromJsonAsync<HaApiKeyStatusDto>("/api/settings/ha-api-key");
        Assert.NotNull(status);
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        // ── Generate: plaintext exactly once, only the hash is stored ───────────────────────
        var generateResponse = await _client.PostAsync("/api/settings/ha-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var generated = await generateResponse.Content.ReadFromJsonAsync<HaApiKeyGenerateResponseDto>();
        Assert.NotNull(generated);
        Assert.NotEmpty(generated!.ApiKey);
        Assert.True(generated.CreatedAt >= DateTime.UtcNow.AddMinutes(-2));

        status = await _client.GetFromJsonAsync<HaApiKeyStatusDto>("/api/settings/ha-api-key");
        Assert.True(status!.HasKey);
        Assert.NotNull(status.CreatedAt);

        // ── The generated key passes the /api gate as X-Api-Key (that's its whole purpose) ──
        using (var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/notes")).StatusCode);

            // ... but the key must NOT be able to manage itself: all three ha-api-key
            // endpoints reject gate-only (API key) authentication with 401.
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/api/settings/ha-api-key")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/ha-api-key/generate", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/ha-api-key/revoke", null)).StatusCode);
        }

        // ── Revoke (with a real session): key hash deleted, old key gets 401 at the gate ────
        var revokeResponse = await _client.PostAsync("/api/settings/ha-api-key/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        status = await _client.GetFromJsonAsync<HaApiKeyStatusDto>("/api/settings/ha-api-key");
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        using (var revokedClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await revokedClient.GetAsync("/api/notes")).StatusCode);
        }
    }
}

/// <summary>
/// Same lifecycle as SettingsControllerHaApiKeyTests, mirrored for the separate studylife-ai
/// key slot (AuthUserEntity.AiApiKeyHash / api/settings/ai-api-key). Own class/factory for the
/// same reason as the HA test - generate/revoke mutate AuthUser 1.
/// </summary>
public class SettingsControllerAiApiKeyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerAiApiKeyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // carries the seeded test user's session token
    }

    [Fact]
    public async Task AiApiKeyLifecycle_StatusGenerateGateRevoke()
    {
        // ── Fresh state: no key ─────────────────────────────────────────────────────────────
        var status = await _client.GetFromJsonAsync<AiApiKeyStatusDto>("/api/settings/ai-api-key");
        Assert.NotNull(status);
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        // ── Generate: plaintext exactly once, only the hash is stored ───────────────────────
        var generateResponse = await _client.PostAsync("/api/settings/ai-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var generated = await generateResponse.Content.ReadFromJsonAsync<AiApiKeyGenerateResponseDto>();
        Assert.NotNull(generated);
        Assert.NotEmpty(generated!.ApiKey);
        Assert.True(generated.CreatedAt >= DateTime.UtcNow.AddMinutes(-2));

        status = await _client.GetFromJsonAsync<AiApiKeyStatusDto>("/api/settings/ai-api-key");
        Assert.True(status!.HasKey);
        Assert.NotNull(status.CreatedAt);

        // ── The generated key passes the /api gate as X-Api-Key, same as the HA key ─────────
        using (var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/notes")).StatusCode);

            // ... but the key must NOT be able to manage itself: all three ai-api-key
            // endpoints reject gate-only (API key) authentication with 401.
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/api/settings/ai-api-key")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/ai-api-key/generate", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/ai-api-key/revoke", null)).StatusCode);
        }

        // ── Revoke (with a real session): key hash deleted, old key gets 401 at the gate ────
        var revokeResponse = await _client.PostAsync("/api/settings/ai-api-key/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        status = await _client.GetFromJsonAsync<AiApiKeyStatusDto>("/api/settings/ai-api-key");
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        using (var revokedClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await revokedClient.GetAsync("/api/notes")).StatusCode);
        }
    }
}

/// <summary>
/// Same lifecycle as SettingsControllerHaApiKeyTests, mirrored for the separate studylife-mcp
/// key slot (AuthUserEntity.McpApiKeyHash / api/settings/mcp-api-key). Own class/factory for the
/// same reason as the HA test - generate/revoke mutate AuthUser 1.
/// </summary>
public class SettingsControllerMcpApiKeyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerMcpApiKeyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // carries the seeded test user's session token
    }

    [Fact]
    public async Task McpApiKeyLifecycle_StatusGenerateGateRevoke()
    {
        // ── Fresh state: no key ─────────────────────────────────────────────────────────────
        var status = await _client.GetFromJsonAsync<McpApiKeyStatusDto>("/api/settings/mcp-api-key");
        Assert.NotNull(status);
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        // ── Generate: plaintext exactly once, only the hash is stored ───────────────────────
        var generateResponse = await _client.PostAsync("/api/settings/mcp-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var generated = await generateResponse.Content.ReadFromJsonAsync<McpApiKeyGenerateResponseDto>();
        Assert.NotNull(generated);
        Assert.NotEmpty(generated!.ApiKey);
        Assert.True(generated.CreatedAt >= DateTime.UtcNow.AddMinutes(-2));

        status = await _client.GetFromJsonAsync<McpApiKeyStatusDto>("/api/settings/mcp-api-key");
        Assert.True(status!.HasKey);
        Assert.NotNull(status.CreatedAt);

        // ── The generated key passes the /api gate as X-Api-Key, same as the HA/AI keys ─────
        using (var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/notes")).StatusCode);

            // ... but the key must NOT be able to manage itself: all three mcp-api-key
            // endpoints reject gate-only (API key) authentication with 401.
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/api/settings/mcp-api-key")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/mcp-api-key/generate", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/mcp-api-key/revoke", null)).StatusCode);
        }

        // ── Revoke (with a real session): key hash deleted, old key gets 401 at the gate ────
        var revokeResponse = await _client.PostAsync("/api/settings/mcp-api-key/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        status = await _client.GetFromJsonAsync<McpApiKeyStatusDto>("/api/settings/mcp-api-key");
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        using (var revokedClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await revokedClient.GetAsync("/api/notes")).StatusCode);
        }
    }
}

/// <summary>
/// Same lifecycle as SettingsControllerHaApiKeyTests, mirrored for the separate studylife-capture
/// key slot (AuthUserEntity.CaptureApiKeyHash / api/settings/capture-api-key). Own class/factory
/// for the same reason as the HA test - generate/revoke mutate AuthUser 1.
/// </summary>
public class SettingsControllerCaptureApiKeyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerCaptureApiKeyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // carries the seeded test user's session token
    }

    [Fact]
    public async Task CaptureApiKeyLifecycle_StatusGenerateGateRevoke()
    {
        // ── Fresh state: no key ─────────────────────────────────────────────────────────────
        var status = await _client.GetFromJsonAsync<CaptureApiKeyStatusDto>("/api/settings/capture-api-key");
        Assert.NotNull(status);
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        // ── Generate: plaintext exactly once, only the hash is stored ───────────────────────
        var generateResponse = await _client.PostAsync("/api/settings/capture-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var generated = await generateResponse.Content.ReadFromJsonAsync<CaptureApiKeyGenerateResponseDto>();
        Assert.NotNull(generated);
        Assert.NotEmpty(generated!.ApiKey);
        Assert.True(generated.CreatedAt >= DateTime.UtcNow.AddMinutes(-2));

        status = await _client.GetFromJsonAsync<CaptureApiKeyStatusDto>("/api/settings/capture-api-key");
        Assert.True(status!.HasKey);
        Assert.NotNull(status.CreatedAt);

        // ── The generated key passes the /api gate as X-Api-Key, same as the HA/AI/MCP keys ─
        using (var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/notes")).StatusCode);

            // ... but the key must NOT be able to manage itself: all three capture-api-key
            // endpoints reject gate-only (API key) authentication with 401.
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/api/settings/capture-api-key")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/capture-api-key/generate", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/capture-api-key/revoke", null)).StatusCode);
        }

        // ── Revoke (with a real session): key hash deleted, old key gets 401 at the gate ────
        var revokeResponse = await _client.PostAsync("/api/settings/capture-api-key/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        status = await _client.GetFromJsonAsync<CaptureApiKeyStatusDto>("/api/settings/capture-api-key");
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        using (var revokedClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await revokedClient.GetAsync("/api/notes")).StatusCode);
        }
    }
}

/// <summary>
/// Same lifecycle as SettingsControllerCaptureApiKeyTests, mirrored for the separate
/// studylife-focusguard key slot (AuthUserEntity.FocusGuardApiKeyHash / api/settings/focusguard-api-key).
/// Own class/factory for the same reason as the other slot tests - generate/revoke mutate AuthUser 1.
/// Uses /api/timerstate (not /api/notes) as the reachable-endpoint check: it's the ONLY endpoint
/// this slot's ApiKeyScopes entry actually grants (see ApiKeyScopeTests.FocusGuard_* for the
/// narrowest-slot scope assertions).
/// </summary>
public class SettingsControllerFocusGuardApiKeyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerFocusGuardApiKeyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // carries the seeded test user's session token
    }

    [Fact]
    public async Task FocusGuardApiKeyLifecycle_StatusGenerateGateRevoke()
    {
        // ── Fresh state: no key ─────────────────────────────────────────────────────────────
        var status = await _client.GetFromJsonAsync<FocusGuardApiKeyStatusDto>("/api/settings/focusguard-api-key");
        Assert.NotNull(status);
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        // ── Generate: plaintext exactly once, only the hash is stored ───────────────────────
        var generateResponse = await _client.PostAsync("/api/settings/focusguard-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var generated = await generateResponse.Content.ReadFromJsonAsync<FocusGuardApiKeyGenerateResponseDto>();
        Assert.NotNull(generated);
        Assert.NotEmpty(generated!.ApiKey);
        Assert.True(generated.CreatedAt >= DateTime.UtcNow.AddMinutes(-2));

        status = await _client.GetFromJsonAsync<FocusGuardApiKeyStatusDto>("/api/settings/focusguard-api-key");
        Assert.True(status!.HasKey);
        Assert.NotNull(status.CreatedAt);

        // ── The generated key passes the /api gate as X-Api-Key, same as the other five keys ─
        using (var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/timerstate")).StatusCode);

            // ... but the key must NOT be able to manage itself: all three focusguard-api-key
            // endpoints reject gate-only (API key) authentication with 401.
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/api/settings/focusguard-api-key")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/focusguard-api-key/generate", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/focusguard-api-key/revoke", null)).StatusCode);
        }

        // ── Revoke (with a real session): key hash deleted, old key gets 401 at the gate ────
        var revokeResponse = await _client.PostAsync("/api/settings/focusguard-api-key/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        status = await _client.GetFromJsonAsync<FocusGuardApiKeyStatusDto>("/api/settings/focusguard-api-key");
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        using (var revokedClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await revokedClient.GetAsync("/api/timerstate")).StatusCode);
        }
    }
}

/// <summary>
/// Same lifecycle as SettingsControllerCaptureApiKeyTests, mirrored for the separate
/// studylife-focustunes key slot (AuthUserEntity.FocusTunesApiKeyHash / api/settings/focustunes-api-key).
/// Own class/factory for the same reason as the other slot tests - generate/revoke mutate AuthUser 1.
/// Uses /api/timerstate (not /api/notes) as the reachable-endpoint check: it's the ONLY endpoint
/// this slot's ApiKeyScopes entry actually grants (see ApiKeyScopeTests.FocusTunes_* for the
/// narrowest-slot scope assertions).
/// </summary>
public class SettingsControllerFocusTunesApiKeyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerFocusTunesApiKeyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // carries the seeded test user's session token
    }

    [Fact]
    public async Task FocusTunesApiKeyLifecycle_StatusGenerateGateRevoke()
    {
        // ── Fresh state: no key ─────────────────────────────────────────────────────────────
        var status = await _client.GetFromJsonAsync<FocusTunesApiKeyStatusDto>("/api/settings/focustunes-api-key");
        Assert.NotNull(status);
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        // ── Generate: plaintext exactly once, only the hash is stored ───────────────────────
        var generateResponse = await _client.PostAsync("/api/settings/focustunes-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var generated = await generateResponse.Content.ReadFromJsonAsync<FocusTunesApiKeyGenerateResponseDto>();
        Assert.NotNull(generated);
        Assert.NotEmpty(generated!.ApiKey);
        Assert.True(generated.CreatedAt >= DateTime.UtcNow.AddMinutes(-2));

        status = await _client.GetFromJsonAsync<FocusTunesApiKeyStatusDto>("/api/settings/focustunes-api-key");
        Assert.True(status!.HasKey);
        Assert.NotNull(status.CreatedAt);

        // ── The generated key passes the /api gate as X-Api-Key, same as the other six keys ──
        using (var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/timerstate")).StatusCode);

            // ... but the key must NOT be able to manage itself: all three focustunes-api-key
            // endpoints reject gate-only (API key) authentication with 401.
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/api/settings/focustunes-api-key")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/focustunes-api-key/generate", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/focustunes-api-key/revoke", null)).StatusCode);
        }

        // ── Revoke (with a real session): key hash deleted, old key gets 401 at the gate ────
        var revokeResponse = await _client.PostAsync("/api/settings/focustunes-api-key/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        status = await _client.GetFromJsonAsync<FocusTunesApiKeyStatusDto>("/api/settings/focustunes-api-key");
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        using (var revokedClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await revokedClient.GetAsync("/api/timerstate")).StatusCode);
        }
    }
}

/// <summary>
/// Same lifecycle as SettingsControllerFocusTunesApiKeyTests, mirrored for the separate
/// studylife-tray key slot (AuthUserEntity.TrayApiKeyHash / api/settings/tray-api-key).
/// Own class/factory for the same reason as the other slot tests - generate/revoke mutate AuthUser 1.
/// </summary>
public class SettingsControllerTrayApiKeyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerTrayApiKeyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // carries the seeded test user's session token
    }

    [Fact]
    public async Task TrayApiKeyLifecycle_StatusGenerateGateRevoke()
    {
        // ── Fresh state: no key ─────────────────────────────────────────────────────────────
        var status = await _client.GetFromJsonAsync<TrayApiKeyStatusDto>("/api/settings/tray-api-key");
        Assert.NotNull(status);
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        // ── Generate: plaintext exactly once, only the hash is stored ───────────────────────
        var generateResponse = await _client.PostAsync("/api/settings/tray-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var generated = await generateResponse.Content.ReadFromJsonAsync<TrayApiKeyGenerateResponseDto>();
        Assert.NotNull(generated);
        Assert.NotEmpty(generated!.ApiKey);
        Assert.True(generated.CreatedAt >= DateTime.UtcNow.AddMinutes(-2));

        status = await _client.GetFromJsonAsync<TrayApiKeyStatusDto>("/api/settings/tray-api-key");
        Assert.True(status!.HasKey);
        Assert.NotNull(status.CreatedAt);

        // ── The generated key passes the /api gate as X-Api-Key, same as the other six keys ──
        using (var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/timerstate")).StatusCode);

            // ... but the key must NOT be able to manage itself: all three tray-api-key
            // endpoints reject gate-only (API key) authentication with 401.
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/api/settings/tray-api-key")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/tray-api-key/generate", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/settings/tray-api-key/revoke", null)).StatusCode);
        }

        // ── Revoke (with a real session): key hash deleted, old key gets 401 at the gate ────
        var revokeResponse = await _client.PostAsync("/api/settings/tray-api-key/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        status = await _client.GetFromJsonAsync<TrayApiKeyStatusDto>("/api/settings/tray-api-key");
        Assert.False(status!.HasKey);
        Assert.Null(status.CreatedAt);

        using (var revokedClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await revokedClient.GetAsync("/api/timerstate")).StatusCode);
        }
    }
}

/// <summary>
/// The whole point of three separate key slots: generating/revoking one must never affect the
/// others. Own class/factory, same isolation reasoning as the lifecycle tests above.
/// </summary>
public class SettingsControllerApiKeySlotsAreIndependentTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerApiKeySlotsAreIndependentTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GeneratingOrRevokingOneKeySlot_DoesNotAffectTheOthers()
    {
        var haGenerated = (await (await _client.PostAsync("/api/settings/ha-api-key/generate", null))
            .Content.ReadFromJsonAsync<HaApiKeyGenerateResponseDto>())!;
        var aiGenerated = (await (await _client.PostAsync("/api/settings/ai-api-key/generate", null))
            .Content.ReadFromJsonAsync<AiApiKeyGenerateResponseDto>())!;
        var mcpGenerated = (await (await _client.PostAsync("/api/settings/mcp-api-key/generate", null))
            .Content.ReadFromJsonAsync<McpApiKeyGenerateResponseDto>())!;

        // All three keys independently authenticate at the gate right after generation.
        using (var haClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, haGenerated.ApiKey))
        using (var aiClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, aiGenerated.ApiKey))
        using (var mcpClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, mcpGenerated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.OK, (await haClient.GetAsync("/api/notes")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await aiClient.GetAsync("/api/notes")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await mcpClient.GetAsync("/api/notes")).StatusCode);
        }

        // Revoking the AI key must not invalidate the HA or MCP keys.
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync("/api/settings/ai-api-key/revoke", null)).StatusCode);
        using (var haClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, haGenerated.ApiKey))
        using (var mcpClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, mcpGenerated.ApiKey))
        {
            Assert.Equal(HttpStatusCode.OK, (await haClient.GetAsync("/api/notes")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await mcpClient.GetAsync("/api/notes")).StatusCode);
        }
        var aiStatus = await _client.GetFromJsonAsync<AiApiKeyStatusDto>("/api/settings/ai-api-key");
        Assert.False(aiStatus!.HasKey);
        var haStatus = await _client.GetFromJsonAsync<HaApiKeyStatusDto>("/api/settings/ha-api-key");
        Assert.True(haStatus!.HasKey);
        var mcpStatus = await _client.GetFromJsonAsync<McpApiKeyStatusDto>("/api/settings/mcp-api-key");
        Assert.True(mcpStatus!.HasKey);

        // Regenerating the HA key must not resurrect the (now revoked) AI key, nor touch MCP's.
        await _client.PostAsync("/api/settings/ha-api-key/generate", null);
        aiStatus = await _client.GetFromJsonAsync<AiApiKeyStatusDto>("/api/settings/ai-api-key");
        Assert.False(aiStatus!.HasKey);
        mcpStatus = await _client.GetFromJsonAsync<McpApiKeyStatusDto>("/api/settings/mcp-api-key");
        Assert.True(mcpStatus!.HasKey);

        // Revoking the MCP key must not invalidate the (regenerated) HA key.
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync("/api/settings/mcp-api-key/revoke", null)).StatusCode);
        haStatus = await _client.GetFromJsonAsync<HaApiKeyStatusDto>("/api/settings/ha-api-key");
        Assert.True(haStatus!.HasKey);
    }
}

/// <summary>
/// Audit finding A12b: ProgressShareToken is a bearer credential for the public
/// GET /api/progress/shared/{token} link - GET /api/settings must only hand it to the browser's
/// own real passkey session, never to any API-key holder (Settings.Get is in the "ha" slot's
/// ApiKeyScopes, since Home Assistant polls it every cycle). Own class/factory: mutates the
/// singleton settings row (enables progress-share) and generates a real HA key, neither of which
/// should leak into other test classes.
/// </summary>
public class SettingsControllerProgressShareTokenLeakTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerProgressShareTokenLeakTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // carries the seeded test user's session token
    }

    [Fact]
    public async Task Get_SessionSeesRealToken_ApiKeySeesNull()
    {
        var enableResponse = await _client.PostAsync("/api/settings/progress-share/enable", null);
        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);
        var enabled = await enableResponse.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.True(enabled!.ProgressShareEnabled);
        Assert.False(string.IsNullOrEmpty(enabled.ProgressShareToken));

        // The browser's own session sees the real token on a plain GET too (not just on the
        // dedicated enable/disable/regenerate responses) - this also populates the 15s GET
        // cache with the real token under the current settings-cache-version key.
        var sessionGet = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.Equal(enabled.ProgressShareToken, sessionGet!.ProgressShareToken);

        // Generating the HA key does NOT bump the settings cache version, so this next GET
        // deliberately lands on the SAME cache entry the session request above just populated -
        // exactly the shared-cache leak vector the fix in SettingsController.Get addresses.
        var generateResponse = await _client.PostAsync("/api/settings/ha-api-key/generate", null);
        var generated = await generateResponse.Content.ReadFromJsonAsync<HaApiKeyGenerateResponseDto>();

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated!.ApiKey);
        var apiKeyGet = await keyClient.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.NotNull(apiKeyGet);
        Assert.Null(apiKeyGet!.ProgressShareToken);
        // Only the secret field is masked - the rest of the response is unaffected.
        Assert.True(apiKeyGet.ProgressShareEnabled);
    }
}

/// <summary>
/// Audit finding F1: LastBackupDownloadAt is documented on UserSettingsEntity as "set directly
/// in BackupController, not via the normal settings PUT" - but until this fix, SettingsController.
/// Save quietly wrote whatever the client's DTO carried for it anyway, so a stale/offline client
/// could silently revert (or forge) the backup-reminder state on its next save. Own class/factory:
/// seeds LastBackupDownloadAt directly via the DbContext (mirrors BackupController.
/// TouchLastBackupDownloadAt without needing a real backup round-trip), then proves a PUT can't
/// move it in either direction.
/// </summary>
public class SettingsControllerLastBackupDownloadAtTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerLastBackupDownloadAtTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Put_CannotClobberOrForgeLastBackupDownloadAt()
    {
        var seeded = new DateTime(2026, 1, 1, 12, 0, 0);
        await _factory.WithDbAsync(async db =>
        {
            var entity = await db.Settings.GetOrCreateAsync(db);
            entity.LastBackupDownloadAt = seeded;
            await db.SaveChangesAsync();
        });

        // A stale/offline client sends its own (older or null) idea of the field, alongside an
        // otherwise valid full settings object - as every real client always does.
        var dto = new UserSettingsDto { LastBackupDownloadAt = null, WeeklyGoalMinHours = 40, WeeklyGoalMaxHours = 60 };
        var putResponse = await _client.PutAsJsonAsync("/api/settings", dto);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var putDto = await putResponse.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.Equal(seeded, putDto!.LastBackupDownloadAt); // untouched by the client's null

        // Forging an arbitrary (future/fabricated) value must be equally impossible.
        var forged = new UserSettingsDto { LastBackupDownloadAt = DateTime.UtcNow.AddDays(30) };
        var forgeResponse = await _client.PutAsJsonAsync("/api/settings", forged);
        Assert.Equal(HttpStatusCode.OK, forgeResponse.StatusCode);
        var forgeDto = await forgeResponse.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.Equal(seeded, forgeDto!.LastBackupDownloadAt); // still the value BackupController set, not the forged one

        var getResponse = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.Equal(seeded, getResponse!.LastBackupDownloadAt);
    }
}

/// <summary>
/// Audit findings S4/S5: optimistic concurrency for the settings PUT. GET always returns the
/// row's current Version; PUT rejects a stale one with 409 Conflict (and bumps on success), but
/// - compatibility requirement - a PUT that omits Version entirely must behave exactly like
/// before this fix (unconditional last-writer-wins), so older clients/Home Assistant/scripts
/// against the API keep working unchanged. Each scenario below gets its OWN class/factory
/// (same isolation rule as SettingsControllerFreshDbTests/SettingsControllerCacheIsolationTests
/// above): they all depend on the row's Version starting at a known, predictable value, which
/// only holds on a pristine, untouched DB - sharing a factory with any other mutating PUT would
/// make the expected Version (and thus OK-vs-409) depend on xUnit's unspecified execution order.
/// </summary>
public class SettingsControllerVersioningFreshDbTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SettingsControllerVersioningFreshDbTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task Get_OnFreshRow_ReturnsVersionZero()
    {
        var dto = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.Equal(0, dto!.Version);
    }
}

public class SettingsControllerVersioningMatchTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SettingsControllerVersioningMatchTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task Put_WithMatchingVersion_SucceedsAndBumpsVersion()
    {
        var initial = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.Equal(0, initial!.Version);

        initial.Version = 0; // matches the row's actual current value
        initial.WeeklyGoalMinHours = 12;
        initial.WeeklyGoalMaxHours = 20;
        var response = await _client.PutAsJsonAsync("/api/settings", initial);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.Equal(1, updated!.Version); // bumped by exactly 1
        Assert.Equal(12, updated.WeeklyGoalMinHours);

        var getAfter = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.Equal(1, getAfter!.Version);
    }
}

public class SettingsControllerVersioningStaleTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SettingsControllerVersioningStaleTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task Put_WithStaleVersion_ReturnsConflictAndDoesNotWrite()
    {
        var initial = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        initial!.Version = 0;
        initial.WeeklyGoalMinHours = 12;
        initial.WeeklyGoalMaxHours = 20;
        // First writer succeeds and moves the row to Version 1.
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/settings", initial)).StatusCode);

        // Second writer still believes Version 0 (e.g. it fetched before the first writer's PUT,
        // simulating the classic "two devices read-modify-write" race) - must be rejected, not
        // silently overwrite the first writer's change.
        var stale = new UserSettingsDto { Version = 0, WeeklyGoalMinHours = 77, WeeklyGoalMaxHours = 88 };
        var conflictResponse = await _client.PutAsJsonAsync("/api/settings", stale);

        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        // The row must still reflect the first writer's change, not the rejected second one.
        var afterConflict = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.Equal(1, afterConflict!.Version);
        Assert.Equal(12, afterConflict.WeeklyGoalMinHours);
    }
}

public class SettingsControllerVersioningAbsentTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SettingsControllerVersioningAbsentTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task Put_WithoutVersion_BehavesLikeLastWriterWinsRegardlessOfCurrentVersion()
    {
        var initial = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        initial!.Version = 0;
        initial.WeeklyGoalMinHours = 12;
        initial.WeeklyGoalMaxHours = 20;
        // Moves the row to Version 1.
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/settings", initial)).StatusCode);

        // A caller that never sends Version at all (compatibility: older client / Home Assistant /
        // an ad-hoc script) must succeed unconditionally, exactly like before this fix - even
        // though its own idea of the row is stale.
        var legacyWrite = new UserSettingsDto { WeeklyGoalMinHours = 33, WeeklyGoalMaxHours = 44 };
        Assert.Null(legacyWrite.Version);
        var response = await _client.PutAsJsonAsync("/api/settings", legacyWrite);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.Equal(33, updated!.WeeklyGoalMinHours);
        Assert.Equal(2, updated.Version); // still incremented - just never CHECKED against the caller's (absent) value
    }
}

/// <summary>
/// M1 regression: SelectedCourseIds/CompletedCourseIds used to be parsed with bare
/// int.Parse in SettingsController.ToDto - one malformed entry (e.g. planted by a bug
/// elsewhere, or by the external studylife-ai capture-enrichment path for other comma-int-list
/// columns) made every subsequent GET throw 500 permanently, since the poisoned row never
/// self-heals. Own factory: seeds the settings row directly via the DbContext (bypassing the
/// normal PUT, which validates/re-serializes and would never itself write garbage).
/// </summary>
public class SettingsControllerPoisonedDataTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerPoisonedDataTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_WithPoisonedSelectedAndCompletedCourseIds_ReturnsOkAndSkipsGarbageTokens()
    {
        await _factory.WithDbAsync(async db =>
        {
            var entity = await db.Settings.GetOrCreateAsync(db);
            entity.SelectedCourseIds = "1,notanumber,3";
            entity.CompletedCourseIds = "corrupted";
            await db.SaveChangesAsync();
        });

        var response = await _client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<UserSettingsDto>();
        Assert.NotNull(dto);
        Assert.Equal(new List<int> { 1, 3 }, dto!.SelectedCourseIds);
        Assert.Empty(dto.CompletedCourseIds);
    }
}
