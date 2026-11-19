// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Demo-instance write-block coverage beyond the single PUT sample in
/// AuthControllerDemoModeTests: the endpoint families the middleware must block (OAuth-client
/// registry, developer CRUD, backup GETs, DELETE verb), the two deliberate exemptions
/// (demo-login exact path, telemetry), and the two GET endpoints that CAN persist on a normal
/// instance but must never do so on a demo (ownership self-heal; the calendar link is only reported, never created on GET).
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
    [InlineData("POST", "/api/settings/builtin-program/dismiss")]
    // The client reports its language on every start; on a demo that must be answered 403 (the
    // client swallows it) rather than change the shared demo user's row.
    [InlineData("PUT", "/api/settings/language")]

    [InlineData("DELETE", "/api/notes/1")]
    // AI consent (AiConsentController): granting or withdrawing is a write like any other, so a
    // demo visitor cannot record a consent or revoke the demo user's AI key.
    [InlineData("POST", "/api/ai/consent/grant")]
    [InlineData("POST", "/api/ai/consent/withdraw")]
    [InlineData("POST", "/api/settings/ai-api-key/generate")]
    public async Task MutatingEndpoints_AreBlockedOnDemoInstance(string method, string path)
    {
        var token = await DemoLoginAsync();
        var response = await SendWithTokenAsync(new HttpMethod(method), path, token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("read-only demo instance", await response.Content.ReadAsStringAsync());
    }

    /// <summary>The consent status is a read, so it stays available on a demo - and reports the AI
    /// as unavailable (the client then hides the floating AI entry and the setup card), as it did
    /// before consent existed.</summary>
    [Fact]
    public async Task AiConsentStatus_OnDemoInstance_ReportsAiUnavailable()
    {
        var token = await DemoLoginAsync();

        var response = await SendWithTokenAsync(HttpMethod.Get, "/api/ai/consent", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<AiConsentStatusDto>();
        Assert.False(status!.AiAvailable);
        Assert.False(status.NeedsDecision);
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
    /// POST /api/dictate is blocked on a demo with its own message: the privacy policy (section 6)
    /// says dictation is off there, so no visitor audio may reach the speech model. The request
    /// must die in the middleware (403 with the dictation-specific error) and never reach the
    /// controller, whose answer on this model-less test host would be 404.
    /// </summary>
    [Fact]
    public async Task Dictate_IsBlockedOnDemoInstance()
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
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("dictation is disabled on the demo instance", body);
        Assert.DoesNotContain("no speech-to-text model", body);
    }

    /// <summary>
    /// GET /api/system/calendar-token only reports the seeded link (stored as a hash, audit
    /// 2026-11-16 L5) and never writes; replacing it is a POST, which the demo write-block refuses,
    /// so the seeded hash stays as it is.
    /// </summary>
    [Fact]
    public async Task CalendarLink_IsReportedButNeverWrittenOnDemoInstance()
    {
        var token = await DemoLoginAsync();
        var seeded = await _factory.WithDbAsync(db =>
            db.AuthUsers.OrderBy(u => u.Id).Select(u => u.CalendarTokenHash).FirstAsync());
        Assert.NotNull(seeded);

        var status = await SendWithTokenAsync(HttpMethod.Get, "/api/system/calendar-token", token);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var dto = await status.Content.ReadFromJsonAsync<CalendarTokenResponseDto>();
        Assert.True(dto!.Exists);
        Assert.Null(dto.CalendarToken);

        var regenerate = await SendWithTokenAsync(HttpMethod.Post, "/api/system/regenerate-calendar-token", token);
        Assert.False(regenerate.IsSuccessStatusCode);

        var after = await _factory.WithDbAsync(db =>
            db.AuthUsers.OrderBy(u => u.Id).Select(u => u.CalendarTokenHash).FirstAsync());
        Assert.Equal(seeded, after);
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
