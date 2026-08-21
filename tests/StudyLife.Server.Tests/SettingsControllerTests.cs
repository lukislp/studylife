using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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
