using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Endpoint-level parity lock for GET /api/setup/overview - same idea as
/// DashboardSummaryEndpointTests, but there is no shared computation here: every section must be
/// BYTE-IDENTICAL JSON to what its own endpoint already returns for the same caller. A mismatch
/// means the bundle's internal helper call has drifted from what the real endpoint returns.
/// </summary>
public class SetupOverviewEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Overview_MatchesEveryIndividualEndpoint_ForASeededOwnerUser()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // Pre-create the calendar token via the real endpoint (which lazily generates it) so the
        // bundle's "only when it already exists" contract is exercised against a token that
        // genuinely already exists, not the generate-on-first-fetch path.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/system/calendar-token")).StatusCode);

        // One key per slot - same generate endpoints the setup cards themselves call.
        foreach (var slot in new[] { "ha", "ai", "mcp", "capture", "focusguard", "focustunes", "tray", "developer" })
        {
            var response = await client.PostAsync($"/api/settings/{slot}-api-key/generate", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var webhookKeyResponse = await client.PostAsJsonAsync("/api/settings/webhooks-api-keys",
            new CreateWebhookApiKeyRequestDto { Name = "zapier" });
        Assert.Equal(HttpStatusCode.OK, webhookKeyResponse.StatusCode);

        var inviteResponse = await client.PostAsync("/api/auth/invites", null);
        Assert.Equal(HttpStatusCode.OK, inviteResponse.StatusCode);

        var programResponse = await client.PostAsJsonAsync("/api/studyprograms", new CreateStudyProgramRequestDto
        {
            Name = "Overview Parity Program",
            Courses = new List<CreateStudyProgramCourseDto> { new() { Semester = 1, Name = "C1", Ects = 6 } },
        });
        Assert.Equal(HttpStatusCode.OK, programResponse.StatusCode);
        var program = await programResponse.Content.ReadFromJsonAsync<StudyProgramSummaryDto>();
        Assert.NotNull(program?.Id);

        var goalResponse = await client.PutAsJsonAsync("/api/coursegoals/1", new CourseGoalDto
        {
            CourseId = 1,
            CourseName = "x",
            Grade = 1.7m,
            CompletedTopics = "",
        });
        Assert.Equal(HttpStatusCode.OK, goalResponse.StatusCode);

        // ── Fetch every individual endpoint through the SAME client/session ──────────────────
        var settings = await client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        var capabilities = await client.GetFromJsonAsync<SystemCapabilitiesResponseDto>("/api/system/capabilities");
        var version = await client.GetFromJsonAsync<VersionResponseDto>("/api/system/version");
        var calendarToken = await client.GetFromJsonAsync<CalendarTokenResponseDto>("/api/system/calendar-token");
        var studyPrograms = await client.GetFromJsonAsync<List<StudyProgramSummaryDto>>("/api/studyprograms");
        var courseGoals = await client.GetFromJsonAsync<List<CourseGoalDto>>("/api/coursegoals");
        var haKey = await client.GetFromJsonAsync<HaApiKeyStatusDto>("/api/settings/ha-api-key");
        var aiKey = await client.GetFromJsonAsync<AiApiKeyStatusDto>("/api/settings/ai-api-key");
        var mcpKey = await client.GetFromJsonAsync<McpApiKeyStatusDto>("/api/settings/mcp-api-key");
        var captureKey = await client.GetFromJsonAsync<CaptureApiKeyStatusDto>("/api/settings/capture-api-key");
        var focusGuardKey = await client.GetFromJsonAsync<FocusGuardApiKeyStatusDto>("/api/settings/focusguard-api-key");
        var focusTunesKey = await client.GetFromJsonAsync<FocusTunesApiKeyStatusDto>("/api/settings/focustunes-api-key");
        var trayKey = await client.GetFromJsonAsync<TrayApiKeyStatusDto>("/api/settings/tray-api-key");
        var developerKey = await client.GetFromJsonAsync<DeveloperApiKeyStatusDto>("/api/settings/developer-api-key");
        var webhookApiKeys = await client.GetFromJsonAsync<List<WebhookApiKeyDto>>("/api/settings/webhooks-api-keys");
        var clientKeys = await client.GetFromJsonAsync<List<ClientApiKeyListItemDto>>("/api/auth/client-keys");
        var invites = await client.GetFromJsonAsync<List<InviteListItemDto>>("/api/auth/invites");
        var restoreStatus = await client.GetFromJsonAsync<RestoreStatusResponseDto>("/api/backup/restore/status");
        var accountInfo = await client.GetFromJsonAsync<AccountInfoDto>("/api/auth/account-info");
        var demoInfo = await client.GetFromJsonAsync<DemoInfoDto>("/api/auth/demo");

        var overviewResponse = await client.GetAsync("/api/setup/overview");
        Assert.Equal(HttpStatusCode.OK, overviewResponse.StatusCode);
        var overview = await overviewResponse.Content.ReadFromJsonAsync<SetupOverviewDto>();
        Assert.NotNull(overview);

        AssertJsonEqual(settings, overview!.Settings);
        AssertJsonEqual(capabilities, overview.Capabilities);
        AssertJsonEqual(version, overview.Version);
        Assert.Equal(calendarToken!.CalendarToken, overview.CalendarToken);
        AssertJsonEqual(studyPrograms, overview.StudyPrograms);
        AssertJsonEqual(courseGoals, overview.CourseGoals);
        AssertJsonEqual(haKey, overview.HaApiKey);
        AssertJsonEqual(aiKey, overview.AiApiKey);
        AssertJsonEqual(mcpKey, overview.McpApiKey);
        AssertJsonEqual(captureKey, overview.CaptureApiKey);
        AssertJsonEqual(focusGuardKey, overview.FocusGuardApiKey);
        AssertJsonEqual(focusTunesKey, overview.FocusTunesApiKey);
        AssertJsonEqual(trayKey, overview.TrayApiKey);
        AssertJsonEqual(developerKey, overview.DeveloperApiKey);
        AssertJsonEqual(webhookApiKeys, overview.WebhookApiKeys);
        AssertJsonEqual(clientKeys, overview.ClientKeys);
        AssertJsonEqual(invites, overview.Invites);
        AssertJsonEqual(restoreStatus, overview.RestoreStatus);
        Assert.Equal(accountInfo!.IsOwner, overview.IsOwner);
        Assert.Equal(demoInfo!.Demo, overview.IsDemo);

        // Sanity: the seeded data actually produced non-trivial values, so the assertions above
        // are comparing real content, not two independently empty/default DTOs.
        Assert.True(overview.HaApiKey!.HasKey);
        Assert.Single(overview.WebhookApiKeys!);
        Assert.Single(overview.Invites!);
        Assert.Contains(overview.CourseGoals!, g => g.CourseId == 1);
        Assert.NotNull(overview.CalendarToken);
    }

    /// <summary>
    /// Owner-only sections (Invites/RestoreStatus) must be null for a non-owner session, exactly
    /// mirroring the 403 the individual endpoints give that same user - never data a non-owner
    /// could not already get through the real endpoints.
    /// </summary>
    [Fact]
    public async Task OwnerOnlySections_AreNull_ForNonOwnerUser_WhileRealEndpointsAnswer403()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        using var ownerKey = new FakePasskey();
        await PasskeyHttp.RegisterAsync(factory, client, "Owner", ownerKey);
        using var memberKey = new FakePasskey();
        var memberToken = await PasskeyHttp.RegisterAsync(factory, client, "Member", memberKey);

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Session-Token", memberToken);
            return await client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/auth/invites")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/backup/restore/status")).StatusCode);

        var overviewResponse = await SendAsync(HttpMethod.Get, "/api/setup/overview");
        Assert.Equal(HttpStatusCode.OK, overviewResponse.StatusCode);
        var overview = await overviewResponse.Content.ReadFromJsonAsync<SetupOverviewDto>();
        Assert.NotNull(overview);
        Assert.Null(overview!.Invites);
        Assert.Null(overview.RestoreStatus);
        Assert.False(overview.IsOwner);

        // Client-keys is NOT owner-restricted (any session user may see their own issued add-on
        // keys) - control check that the bundle doesn't over-null a section nobody denies.
        var clientKeysResponse = await SendAsync(HttpMethod.Get, "/api/auth/client-keys");
        Assert.Equal(HttpStatusCode.OK, clientKeysResponse.StatusCode);
        Assert.NotNull(overview.ClientKeys);
    }

    /// <summary>
    /// The bundle must never perform the calendar token's lazy-create write (a GET with a side
    /// effect, see SystemController.GetCalendarToken/SetupController's class doc): a user who
    /// never touched the calendar feature gets CalendarToken == null from the bundle, and the DB
    /// still has no token afterward.
    /// </summary>
    [Fact]
    public async Task Overview_NeverCreatesACalendarToken()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var overviewResponse = await client.GetAsync("/api/setup/overview");
        Assert.Equal(HttpStatusCode.OK, overviewResponse.StatusCode);
        var overview = await overviewResponse.Content.ReadFromJsonAsync<SetupOverviewDto>();
        Assert.NotNull(overview);
        Assert.Null(overview!.CalendarToken);

        var stillNoToken = await factory.WithDbAsync(async db =>
        {
            var user = await db.AuthUsers.SingleAsync(u => u.Id == 1);
            return user.CalendarToken;
        });
        Assert.Null(stillNoToken);
    }

    [Fact]
    public async Task ApiKeyCredential_IsRejected_SessionOnlyEndpoint()
    {
        using var factory = new CustomWebApplicationFactory();
        var sessionClient = factory.CreateClient();

        var generateResponse = await sessionClient.PostAsync("/api/settings/ha-api-key/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var generated = await generateResponse.Content.ReadFromJsonAsync<HaApiKeyGenerateResponseDto>();
        Assert.NotNull(generated);

        using var keyClient = ApiKeyTestHelpers.CreateClientWithKey(factory, generated!.ApiKey);
        // A bare API key - even one from the widest slot (ha) - must NOT reach this endpoint: no
        // slot in ApiKeyScopes lists it, and SessionOnly rejects it before scoping applies.
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/api/setup/overview")).StatusCode);
    }

    /// <summary>
    /// Demo-instance parity: the bundle must answer exactly what the individual endpoints already
    /// answer for the pre-seeded demo user (all key slots empty, calendar token pre-seeded by
    /// DemoSeeder, owner-only backup/invite reads unavailable), and - the actual hard constraint
    /// behind this whole feature - a write through the bundle's own route prefix must still be
    /// blocked exactly like every other /api mutation on a demo instance (GET-only guarantee).
    /// </summary>
    [Fact]
    public async Task DemoInstance_OverviewMatchesIndividualEndpoints_AndWritesStayBlocked()
    {
        using var factory = new AuthControllerDemoModeTests.DemoModeFactory();
        var anonymous = ApiKeyTestHelpers.CreateClientWithKey(factory, null);

        var login = await anonymous.PostAsync("/api/auth/demo-login", null);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>())!.Token!;

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Session-Token", token);
            return await anonymous.SendAsync(request);
        }

        var calendarToken = await (await SendAsync(HttpMethod.Get, "/api/system/calendar-token"))
            .Content.ReadFromJsonAsync<CalendarTokenResponseDto>();
        var haKey = await (await SendAsync(HttpMethod.Get, "/api/settings/ha-api-key"))
            .Content.ReadFromJsonAsync<HaApiKeyStatusDto>();
        // Blocked entirely by the demo write-block middleware for ANY method, before
        // BackupController is ever reached - see Program.cs.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/backup/restore/status")).StatusCode);

        var overviewResponse = await SendAsync(HttpMethod.Get, "/api/setup/overview");
        Assert.Equal(HttpStatusCode.OK, overviewResponse.StatusCode);
        var overview = await overviewResponse.Content.ReadFromJsonAsync<SetupOverviewDto>();
        Assert.NotNull(overview);
        Assert.True(overview!.IsDemo);
        Assert.False(haKey!.HasKey);
        AssertJsonEqual(haKey, overview.HaApiKey);
        // DemoSeeder pre-seeds a calendar token for the demo user - already exists, so the bundle
        // reports it exactly like the individual (non-generating, since it already exists) fetch.
        Assert.NotNull(calendarToken!.CalendarToken);
        Assert.Equal(calendarToken.CalendarToken, overview.CalendarToken);
        // Blocked on the real endpoint (middleware) => null in the bundle, never data.
        Assert.Null(overview.RestoreStatus);

        // The actual hard constraint: a write through the bundle's own controller/route prefix
        // must still be blocked like every other /api mutation on a demo instance - the bundle
        // being GET-only must not have created some new prefix the write-block middleware forgot.
        using var writeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/setup/overview");
        writeRequest.Headers.Add("X-Session-Token", token);
        var writeResponse = await anonymous.SendAsync(writeRequest);
        // No POST action exists on SetupController at all - 404/405, not 200. Either way, not
        // success: nothing about this feature opened a way to write on a demo instance.
        Assert.True(writeResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden);
    }

    private static void AssertJsonEqual<T>(T? expected, T? actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected, JsonOptions), JsonSerializer.Serialize(actual, JsonOptions));
}
