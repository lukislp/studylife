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
