using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Demo-instance write-block coverage beyond the single PUT sample in
/// AuthControllerDemoModeTests: the endpoint families the middleware must block (OAuth-client
/// registry, developer CRUD, backup GETs, DELETE verb), the two deliberate exemptions
/// (demo-login exact path, dictate), and the two GET endpoints that CAN persist on a normal
/// instance but must never do so on a demo (calendar-token lazy create, ownership self-heal).
/// Own factory instance (fresh DB) so the destructive AuthControllerDemoModeTests - which
/// deletes all AuthUsers - can never race this class's demo-login dependency.
/// </summary>
public class DemoModeWriteBlockTests : IClassFixture<AuthControllerDemoModeTests.DemoModeFactory>
{
    private readonly AuthControllerDemoModeTests.DemoModeFactory _factory;
    private readonly HttpClient _client;

    public DemoModeWriteBlockTests(AuthControllerDemoModeTests.DemoModeFactory factory)
    {
        _factory = factory;
        _client = ApiKeyTestHelpers.CreateClientWithKey(factory, null);
    }

    private async Task<string> DemoLoginAsync()
    {
        var login = await _client.PostAsync("/api/auth/demo-login", null);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>())!.Token!;
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            // A body is irrelevant to the middleware (it rejects before model binding), but an
            // empty JSON object keeps the request well-formed for every endpoint under test.
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("X-Session-Token", token);
        return await _client.SendAsync(request);
    }

    /// <summary>
    /// The entire add-on/OAuth surface added with the marketplace work - generic connect flow,
    /// developer CRUD, developer-key issuance, plus one of the legacy hardcoded consent
    /// endpoints and a DELETE (a verb no other demo test covered). All must hit the generic
    /// write-block, even with a genuinely valid demo session attached.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/auth/connect")]
    [InlineData("POST", "/api/auth/assertion-exchange")]
    [InlineData("POST", "/api/auth/mcp-connect")]
    [InlineData("POST", "/api/developer/clients")]
    [InlineData("PUT", "/api/developer/clients/some-client")]
    [InlineData("DELETE", "/api/developer/clients/some-client")]
    [InlineData("POST", "/api/settings/developer-api-key/generate")]
    [InlineData("DELETE", "/api/notes/1")]
    public async Task MutatingEndpoints_AreBlockedOnDemoInstance(string method, string path)
    {
        var token = await DemoLoginAsync();
        var response = await SendWithTokenAsync(new HttpMethod(method), path, token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("read-only demo instance", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// /api/backup is blocked for ANY method on a demo - its GETs hand out the raw database
    /// (SystemSecrets, session-token hashes) and the full JSON export, and the demo user
    /// would pass the owner check. Distinct 403 message from the generic write-block because
    /// the middleware intercepts the whole prefix in its own earlier branch.
    /// </summary>
    [Theory]
    [InlineData("/api/backup/database")]
    [InlineData("/api/backup/export")]
    [InlineData("/api/backup/restore/status")]
    public async Task BackupReads_AreBlockedOnDemoInstance(string path)
    {
        var token = await DemoLoginAsync();
        var response = await SendWithTokenAsync(HttpMethod.Get, path, token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("backups are disabled", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The demo-login exemption is an EXACT path match (empty remainder) - a subpath must fall
    /// through to the generic block. Pins the IsNullOrEmpty(remainder) check in Program.cs.
    /// </summary>
    [Fact]
    public async Task DemoLoginSubpath_IsNotExemptFromWriteBlock()
    {
        var response = await _client.PostAsync("/api/auth/demo-login/extra", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("read-only demo instance", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// POST /api/dictate is deliberately exempt from the write-block (pure transform, nothing
    /// persisted). The test host has no Whisper model, so the controller itself answers 404 -
    /// the point is that the request REACHES the controller instead of dying in the middleware
    /// with the write-block's 403.
    /// </summary>
    [Fact]
    public async Task Dictate_IsExemptFromWriteBlock()
    {
        var token = await DemoLoginAsync();
        // A well-formed multipart body with one non-file field: a fully EMPTY multipart form
        // fails form binding with a generic 400 before the action runs, which would make this
        // test ambiguous about what it actually proves.
        var form = new MultipartFormDataContent { { new StringContent("de"), "lang" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/dictate")
        {
            Content = form,
        };
        request.Headers.Add("X-Session-Token", token);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("no speech-to-text model", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// GET /api/system/calendar-token lazily creates+persists a token on a normal instance -
    /// on a demo it must never write. Normally unreachable there (DemoSeeder pre-seeds the
    /// token, verified by the first half), but the second half proves the explicit guard holds
    /// even if the seeded token is gone, instead of relying on the seeder line alone.
    /// </summary>
    [Fact]
    public async Task CalendarToken_NeverLazilyPersistsOnDemoInstance()
    {
        var token = await DemoLoginAsync();

        // Seeded path: returns the pre-seeded token unchanged, twice.
        var first = await SendWithTokenAsync(HttpMethod.Get, "/api/system/calendar-token", token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstDto = await first.Content.ReadFromJsonAsync<CalendarTokenResponseDto>();
        var seeded = await _factory.WithDbAsync(db =>
            db.AuthUsers.OrderBy(u => u.Id).Select(u => u.CalendarToken).FirstAsync());
        Assert.Equal(seeded, firstDto!.CalendarToken);

        // Un-seeded path: no lazy create on demo - 503, and the DB row stays untouched.
        await _factory.WithDbAsync(db => db.AuthUsers
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.CalendarToken, (string?)null)));
        try
        {
            var second = await SendWithTokenAsync(HttpMethod.Get, "/api/system/calendar-token", token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
            var still = await _factory.WithDbAsync(db =>
                db.AuthUsers.OrderBy(u => u.Id).Select(u => u.CalendarToken).FirstAsync());
            Assert.Null(still);
        }
        finally
        {
            // Restore the seeded token so sibling tests (arbitrary order) see normal demo state.
            await _factory.WithDbAsync(db => db.AuthUsers
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.CalendarToken, seeded)));
        }
    }

    /// <summary>
    /// OwnershipService's ownerless self-heal runs an ExecuteUpdate from plain GETs
    /// (account-info/invites). On a demo it must answer with the DERIVED result (lowest Id is
    /// owner - functionality unchanged for the caller) but never persist it. Normally
    /// unreachable there (DemoSeeder seeds IsOwner=true); this forces the ownerless state.
    /// </summary>
    [Fact]
    public async Task OwnershipSelfHeal_DoesNotPersistOnDemoInstance()
    {
        var token = await DemoLoginAsync();
        await _factory.WithDbAsync(db => db.AuthUsers
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsOwner, false)));
        try
        {
            var response = await SendWithTokenAsync(HttpMethod.Get, "/api/auth/account-info", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dto = await response.Content.ReadFromJsonAsync<AccountInfoDto>();
            Assert.True(dto!.IsOwner); // derived answer stays correct...

            var persisted = await _factory.WithDbAsync(db =>
                db.AuthUsers.OrderBy(u => u.Id).Select(u => u.IsOwner).FirstAsync());
            Assert.False(persisted); // ...but nothing was written.
        }
        finally
        {
            await _factory.WithDbAsync(db => db.AuthUsers
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsOwner, true)));
        }
    }
}
