using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Early-return/error branches of AuthController that the happy-path passkey tests never
/// touch: input validation on register/begin, the "challenge unknown or expired" paths of
/// register/complete + login/complete, the session binding of the additional-passkey flow,
/// and the Fido2 verification failure (wrong origin).
/// </summary>
public class AuthControllerRegisterEdgeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthControllerRegisterEdgeTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Syntactically valid options JSON with everything FakePasskey needs
    /// (challenge/rp.id/user.id) - lets us build well-formed responses for challenges the
    /// server never issued, so the "pending is null" branch is what actually rejects.</summary>
    private const string FakeRegistrationOptionsJson =
        """{"challenge":"AAAAAAAAAAAAAAAAAAAAAA","rp":{"id":"localhost"},"user":{"id":"AAAAAAAAAAAAAAAAAAAAAA"}}""";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RegisterBegin_EmptyDisplayName_ReturnsBadRequest(string displayName)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register/begin",
            new PasskeyRegisterBeginRequestDto { DisplayName = displayName });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterBegin_DisplayNameOver100Chars_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register/begin",
            new PasskeyRegisterBeginRequestDto { DisplayName = new string('x', 101) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterComplete_NullResponse_ReturnsBadRequest()
    {
        // {"optionsId":"...","response":null} -> 400. Note: through the real stack this 400
        // comes from [ApiController]'s implicit required validation for the non-nullable
        // Response property, BEFORE the action runs - the controller's own null guard
        // (RegisterComplete line "request.Response is null") is unreachable defense-in-depth.
        // This fact pins the observable contract (400, no state created) either way.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register/complete")
        {
            Content = new StringContent("""{"optionsId":"does-not-exist","response":null}""",
                Encoding.UTF8, "application/json"),
        };
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterComplete_UnknownOptionsId_ReturnsBadRequest()
    {
        // Well-formed attestation, but for a challenge the server never issued (or that
        // expired after the 5-minute lifetime) -> "Registration challenge unknown or expired".
        using var passkey = new FakePasskey();
        var attestation = passkey.CreateAttestationResponse(FakeRegistrationOptionsJson, PasskeyHttp.Origin);
        var response = await PasskeyHttp.CompleteAsync(_client, "/api/auth/register/complete",
            "0000never-issued0000", attestation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unknown or expired", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>
/// Own class/factory: these cases need a real registered user (and thus mutate the DB) -
/// isolated from the input-validation cases above.
/// </summary>
public class AuthControllerRegisterVerificationFailureTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthControllerRegisterVerificationFailureTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RegisterAndLoginEdgeCases_AreRejectedWithoutCreatingState()
    {
        // ── Fido2 verification failure: attestation built for a DIFFERENT origin ────────────
        // The challenge is genuine (real register/begin), but the clientDataJSON claims
        // https://evil.example - Fido2NetLib throws Fido2VerificationException, the server
        // answers 400 and must NOT have persisted a credential.
        using var evilKey = new FakePasskey();
        var (evilOptionsId, evilOptionsJson) = await PasskeyHttp.BeginAsync(_client, "/api/auth/register/begin",
            new PasskeyRegisterBeginRequestDto
            {
                DisplayName = "Eve",
                SetupSecret = await GetSetupSecretAsync(),
            });
        var evilComplete = await PasskeyHttp.CompleteAsync(_client, "/api/auth/register/complete",
            evilOptionsId, evilKey.CreateAttestationResponse(evilOptionsJson, "https://evil.example"));
        Assert.Equal(HttpStatusCode.BadRequest, evilComplete.StatusCode);
        Assert.Contains("could not be verified", await evilComplete.Content.ReadAsStringAsync());
        await _factory.WithDbAsync(async db =>
            Assert.False(await db.PasskeyCredentials.AnyAsync(c => c.CredentialId == evilKey.CredentialId)));

        // ── Real registration as a basis for the additional-passkey session binding ─────────
        using var firstKey = new FakePasskey();
        var token = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", firstKey);

        // begin-additional with a valid session, but complete WITHOUT one: the challenge was
        // bound to Alex' account (RequiresSessionAtComplete=true), so a caller who cannot
        // present the same session at complete time gets 401 - a replayed optionsId alone
        // must never be enough to plant a passkey onto the account.
        var (additionalId, additionalJson) = await PasskeyHttp.BeginAsync(
            _client, "/api/auth/register/begin-additional", body: null, sessionToken: token);
        using var plantedKey = new FakePasskey();
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var sessionlessComplete = await PasskeyHttp.CompleteAsync(anon, "/api/auth/register/complete",
            additionalId, plantedKey.CreateAttestationResponse(additionalJson, PasskeyHttp.Origin));
        Assert.Equal(HttpStatusCode.Unauthorized, sessionlessComplete.StatusCode);
        await _factory.WithDbAsync(async db =>
            Assert.False(await db.PasskeyCredentials.AnyAsync(c => c.CredentialId == plantedKey.CredentialId)));

        // ── login/complete with a well-formed assertion for an unknown challenge: 401 ───────
        // (pending is null - uniform 401, indistinguishable from a bad signature.)
        var (_, loginOptionsJson) = await PasskeyHttp.BeginAsync(_client, "/api/auth/login/begin");
        var unknownChallenge = await PasskeyHttp.CompleteAsync(_client, "/api/auth/login/complete",
            "0000never-issued0000", firstKey.CreateAssertionResponse(loginOptionsJson, PasskeyHttp.Origin, signCount: 2));
        Assert.Equal(HttpStatusCode.Unauthorized, unknownChallenge.StatusCode);

        // ── Sign count DROPPING TO ZERO despite a valid signature: 401 ──────────────────────
        // The stored counter is 1 (from registration). A cloned key on hardware without
        // counter support would report 0 - Fido2NetLib's own spec check only fires for
        // NONZERO regressions, so this case is exactly what the controller's explicit
        // "either value > 0 requires strict increase" guard exists for (the 0/0 Apple case
        // stays allowed and is covered by the normal login tests).
        var (zeroId, zeroJson) = await PasskeyHttp.BeginAsync(_client, "/api/auth/login/begin");
        var zeroCount = await PasskeyHttp.CompleteAsync(_client, "/api/auth/login/complete",
            zeroId, firstKey.CreateAssertionResponse(zeroJson, PasskeyHttp.Origin, signCount: 0));
        Assert.Equal(HttpStatusCode.Unauthorized, zeroCount.StatusCode);
    }

    private async Task<string> GetSetupSecretAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SystemSecretsService>().EnsureSetupSecretAsync();
    }
}

/// <summary>
/// Demo endpoints on a NORMAL (non-demo) instance: /api/auth/demo honestly reports
/// demo:false, and demo-login is hard-disabled with 404 (no auto-session for anyone).
/// </summary>
public class AuthControllerDemoDisabledTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthControllerDemoDisabledTests(CustomWebApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task GetDemoInfo_WithoutDemoMode_ReturnsDemoFalse()
    {
        var dto = await _client.GetFromJsonAsync<DemoInfoDto>("/api/auth/demo");
        Assert.NotNull(dto);
        Assert.False(dto!.Demo);
    }

    [Fact]
    public async Task DemoLogin_WithoutDemoMode_ReturnsNotFound()
    {
        var response = await _client.PostAsync("/api/auth/demo-login", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// DEMO_MODE=true instance: Program.cs wipes + reseeds the DB via DemoSeeder at startup and
/// registers the write-block middleware; AuthController.DemoLogin then issues a REAL session
/// for the seeded demo user. One scenario fact, because the 503 case ("demo user not seeded")
/// requires destroying the user AFTER the happy path ran (xUnit gives no order guarantee).
/// </summary>
public class AuthControllerDemoModeTests : IClassFixture<AuthControllerDemoModeTests.DemoModeFactory>
{
    public class DemoModeFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("DEMO_MODE", "true");
        }
    }

    private readonly DemoModeFactory _factory;
    private readonly HttpClient _client;

    public AuthControllerDemoModeTests(DemoModeFactory factory)
    {
        _factory = factory;
        _client = ApiKeyTestHelpers.CreateClientWithKey(factory, null); // demo endpoints are unauthenticated
    }

    [Fact]
    public async Task DemoLogin_IssuesRealSession_And503WhenNoUserSeeded()
    {
        // ── Discovery: the client can tell it's on a demo instance ──────────────────────────
        var info = await _client.GetFromJsonAsync<DemoInfoDto>("/api/auth/demo");
        Assert.True(info!.Demo);

        // ── Happy path: auto-login issues a real session for the seeded demo user ───────────
        var login = await _client.PostAsync("/api/auth/demo-login", null);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var dto = await login.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();
        Assert.NotNull(dto?.Token);
        Assert.Equal("Demo", dto!.DisplayName);

        // The token is a completely normal session: works on any read endpoint.
        var notes = await PasskeyHttp.GetWithTokenAsync(_client, "/api/notes", dto.Token!);
        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);

        // Sanity check of the demo write-block: mutations outside demo-login are 403.
        using (var put = new HttpRequestMessage(HttpMethod.Put, "/api/settings")
        {
            Content = JsonContent.Create(new UserSettingsDto()),
        })
        {
            put.Headers.Add("X-Session-Token", dto.Token!);
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(put)).StatusCode); // ... writes don't.
        }

        // ── Broken deployment: demo user missing entirely -> 503, not a crash ───────────────
        // (FKs are deliberately loose in this schema, see the AuthUserId comments in
        // StudyLifeDb - deleting the user rows directly is enough.)
        await _factory.WithDbAsync(async db =>
        {
            await db.AuthSessions.ExecuteDeleteAsync();
            await db.AuthUsers.ExecuteDeleteAsync();
        });
        var brokenLogin = await _client.PostAsync("/api/auth/demo-login", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, brokenLogin.StatusCode);
        Assert.Contains("demo user not seeded", await brokenLogin.Content.ReadAsStringAsync());
    }
}
