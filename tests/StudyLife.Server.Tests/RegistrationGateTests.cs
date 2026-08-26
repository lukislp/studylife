using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Pure unit tests for RegistrationGateService.GetMode - no host needed, just an IConfiguration
/// built from an in-memory dictionary (same shape as DemoModeGuardTests). The integration-level
/// scenarios (actual gating over the real HTTP pipeline) live in the RegistrationGate* classes
/// below.
/// </summary>
public class RegistrationModeConfigTests
{
    private static IConfiguration Config(string? mode) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(mode is null
                ? []
                : new[] { new KeyValuePair<string, string?>("Registration:Mode", mode) })
            .Build();

    [Fact]
    public void Unset_DefaultsToInvite()
    {
        Assert.Equal(RegistrationMode.Invite, RegistrationGateService.GetMode(Config(null)));
    }

    [Theory]
    [InlineData("open", RegistrationMode.Open)]
    [InlineData("OPEN", RegistrationMode.Open)]
    [InlineData("invite", RegistrationMode.Invite)]
    [InlineData("Invite", RegistrationMode.Invite)]
    [InlineData("closed", RegistrationMode.Closed)]
    [InlineData("CLOSED", RegistrationMode.Closed)]
    public void RecognizedValue_MapsToMatchingMode(string configured, RegistrationMode expected)
    {
        Assert.Equal(expected, RegistrationGateService.GetMode(Config(configured)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bogus")]
    public void UnrecognizedValue_FallsBackToInvite(string configured)
    {
        Assert.Equal(RegistrationMode.Invite, RegistrationGateService.GetMode(Config(configured)));
    }
}

// ── Factories (top-level, audit finding A10) ──────────────────────────────────────
//
// One factory TYPE per mode, but every test CLASS below still gets its OWN fresh instance/DB
// (xUnit creates a new IClassFixture<T> instance per test class, not per fixture type) - required
// here because almost every scenario registers a fresh "Alex" and needs to be the one and only
// bootstrap registration in its DB (see CustomWebApplicationFactory's own comment on why the
// SHARED default factory defaults to "open": these dedicated mode factories are exactly the
// override it's talking about). Scenarios that are safe to share a DB across multiple [Fact]s in
// one class (nothing in them depends on being THE bootstrap registration) are combined into a
// single sequential scenario method instead - same "scenario test instead of multiple [Fact]s,
// because the steps causally build on each other" rationale as PasskeyRegistrationTests.

public class OpenModeFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Registration:Mode", "open");
    }
}

public class ClosedModeFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Registration:Mode", "closed");
    }
}

public class InviteModeFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Registration:Mode", "invite");
    }
}

/// <summary>
/// Registration:Mode=open (audit finding A10): the previous, unconditional "open family signup"
/// behavior - bootstrap AND every subsequent self-registration succeed without any invite.
/// Safe to combine into one class/one shared DB (unlike the invite/closed scenarios below):
/// nothing here depends on being THE bootstrap registration, "open" allows every registration
/// unconditionally regardless of how many passkeys already exist or in what order these two
/// [Fact]s happen to run.
/// </summary>
public class RegistrationGateOpenModeTests : IClassFixture<OpenModeFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RegistrationGateOpenModeTests(OpenModeFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Bootstrap_AllowsFirstRegistration_RegardlessOfMode()
    {
        using var key = new FakePasskey();
        var token = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", key);
        Assert.NotEmpty(token);
    }

    [Fact]
    public async Task OpenMode_AllowsSecondRegistration_WithoutInvite()
    {
        using var ownerKey = new FakePasskey();
        await PasskeyHttp.RegisterAsync(_factory, _client, "Owner", ownerKey);

        // Past bootstrap now (a passkey exists) - "open" must keep allowing family signup with
        // no invite token at all, exactly like the pre-A10 behavior.
        using var secondKey = new FakePasskey();
        var token = await PasskeyHttp.RegisterAsync(_factory, _client, "Anna", secondKey);
        Assert.NotEmpty(token);
    }
}

/// <summary>
/// Registration:Mode=closed (audit finding A10): bootstrap still succeeds (must never brick a
/// fresh install), but every subsequent self-registration is rejected - even with an otherwise
/// valid invite, since invite CREATION (owner-only) is independent of the current gate mode. Two
/// SEPARATE classes (own DB each): both scenarios below need to be the sole "first ever"
/// registration in their DB, which a shared fixture across two [Fact]s cannot guarantee (xUnit
/// gives no ordering - whichever runs second would find a passkey already there from the first).
/// </summary>
public class RegistrationGateClosedModeBootstrapTests : IClassFixture<ClosedModeFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RegistrationGateClosedModeBootstrapTests(ClosedModeFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Bootstrap_AllowsFirstRegistration_EvenWhenClosed()
    {
        using var key = new FakePasskey();
        var token = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", key);
        Assert.NotEmpty(token);
    }
}

public class RegistrationGateClosedModeRejectsSignupTests : IClassFixture<ClosedModeFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RegistrationGateClosedModeRejectsSignupTests(ClosedModeFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ClosedMode_RejectsSecondRegistration_EvenWithValidInvite()
    {
        using var ownerKey = new FakePasskey();
        var ownerToken = await PasskeyHttp.RegisterAsync(_factory, _client, "Alex", ownerKey);

        // Invite creation itself is owner-only plumbing, independent of Registration:Mode - the
        // owner can still generate one, but "closed" means it can never actually be redeemed.
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/invites");
        createRequest.Headers.Add("X-Session-Token", ownerToken);
        var createResponse = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var invite = await createResponse.Content.ReadFromJsonAsync<CreateInviteResponseDto>();
        Assert.NotNull(invite);

        var beginResponse = await _client.PostAsJsonAsync("/api/auth/register/begin",
            new PasskeyRegisterBeginRequestDto { DisplayName = "Anna", InviteToken = invite!.Token });
        Assert.Equal(HttpStatusCode.Forbidden, beginResponse.StatusCode);
        var error = await beginResponse.Content.ReadFromJsonAsync<RegistrationGateErrorDto>();
        Assert.Equal("closed", error?.Reason);
    }
}

/// <summary>Shared helpers for the Registration:Mode=invite scenarios below - each test class owns
/// its own InviteModeFactory instance (own DB), so these are instance methods, not statics.</summary>
public abstract class RegistrationGateInviteModeTestsBase : IClassFixture<InviteModeFactory>
{
    protected readonly CustomWebApplicationFactory Factory;
    protected readonly HttpClient Client;

    protected RegistrationGateInviteModeTestsBase(InviteModeFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient();
    }

    protected async Task<string> RegisterOwnerAsync()
    {
        using var ownerKey = new FakePasskey();
        return await PasskeyHttp.RegisterAsync(Factory, Client, "Alex", ownerKey);
    }

    protected async Task<CreateInviteResponseDto> CreateInviteAsync(string ownerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/invites");
        request.Headers.Add("X-Session-Token", ownerToken);
        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var invite = await response.Content.ReadFromJsonAsync<CreateInviteResponseDto>();
        Assert.NotNull(invite);
        return invite!;
    }

    /// <summary>register/begin with no session (self-registration path) - raw call, doesn't
    /// assert success like PasskeyHttp.BeginAsync, since these tests want to inspect a 403.</summary>
    protected Task<HttpResponseMessage> RawBeginAsync(string displayName, string? inviteToken) =>
        Client.PostAsJsonAsync("/api/auth/register/begin",
            new PasskeyRegisterBeginRequestDto { DisplayName = displayName, InviteToken = inviteToken });

    protected async Task<List<InviteListItemDto>> GetInvitesAsync(string ownerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/invites");
        request.Headers.Add("X-Session-Token", ownerToken);
        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<InviteListItemDto>>())!;
    }
}

/// <summary>Registration:Mode=invite - the production DEFAULT when unset (see
/// CustomWebApplicationFactory for why the SHARED test factory overrides that default to "open",
/// and RegistrationModeConfigTests for the pure-unit-test pin of the actual fallback).</summary>
public class RegistrationGateInviteModeBootstrapTests(InviteModeFactory factory) : RegistrationGateInviteModeTestsBase(factory)
{
    [Fact]
    public async Task Bootstrap_AllowsFirstRegistration_InDefaultInviteMode()
    {
        var token = await RegisterOwnerAsync();
        Assert.NotEmpty(token);
    }
}

/// <summary>The three rejection reasons (audit A10) - combined into one scenario against one
/// owner/DB, since none of them mutate state the others depend on.</summary>
public class RegistrationGateInviteModeRejectionTests(InviteModeFactory factory) : RegistrationGateInviteModeTestsBase(factory)
{
    [Fact]
    public async Task RegisterBegin_RejectsMissingInvalidAndExpiredTokens()
    {
        var ownerToken = await RegisterOwnerAsync();

        // No token at all.
        var withoutToken = await RawBeginAsync("Anna", inviteToken: null);
        Assert.Equal(HttpStatusCode.Forbidden, withoutToken.StatusCode);
        Assert.Equal("invite_required", (await withoutToken.Content.ReadFromJsonAsync<RegistrationGateErrorDto>())?.Reason);

        // A token that was never issued.
        var garbageToken = await RawBeginAsync("Anna", inviteToken: "this-token-does-not-exist");
        Assert.Equal(HttpStatusCode.Forbidden, garbageToken.StatusCode);
        Assert.Equal("invite_invalid", (await garbageToken.Content.ReadFromJsonAsync<RegistrationGateErrorDto>())?.Reason);

        // A real invite, backdated past its expiry (same effect as waiting 7 days, no fake clock).
        var invite = await CreateInviteAsync(ownerToken);
        await Factory.WithDbAsync(async db =>
        {
            var row = await db.AuthInvites.SingleAsync(i => i.Id == invite.Id);
            row.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        });
        var expiredToken = await RawBeginAsync("Anna", invite.Token);
        Assert.Equal(HttpStatusCode.Forbidden, expiredToken.StatusCode);
        Assert.Equal("invite_invalid", (await expiredToken.Content.ReadFromJsonAsync<RegistrationGateErrorDto>())?.Reason);
    }
}

/// <summary>Consumption timing (audit A10): a begin alone never burns the invite, complete
/// consumes it exactly once, and a stale/reused token is then rejected like any invalid one.
/// One scenario/one owner, since each step depends on the previous one's state.</summary>
public class RegistrationGateInviteModeConsumptionTests(InviteModeFactory factory) : RegistrationGateInviteModeTestsBase(factory)
{
    [Fact]
    public async Task ValidToken_NotConsumedAtBegin_ConsumedExactlyOnceAtComplete_ThenRejectedOnReuse()
    {
        var ownerToken = await RegisterOwnerAsync();
        var invite = await CreateInviteAsync(ownerToken);

        // Two separate begin calls with the SAME still-unused token both succeed - proves begin
        // only VALIDATES, it never marks the invite used (only register/complete does, see
        // AuthController.RegisterComplete / RegistrationGateService.TryConsumeInviteAsync).
        var firstBegin = await RawBeginAsync("Anna", invite.Token);
        Assert.Equal(HttpStatusCode.OK, firstBegin.StatusCode);
        var (optionsId, optionsJson) = await ReadBeginResponseAsync(firstBegin);

        var secondBegin = await RawBeginAsync("Anna", invite.Token);
        Assert.Equal(HttpStatusCode.OK, secondBegin.StatusCode);

        var stillUnused = await GetInvitesAsync(ownerToken);
        Assert.Null(Assert.Single(stillUnused, i => i.Id == invite.Id).UsedAt);

        using var passkey = new FakePasskey();
        var completeResponse = await PasskeyHttp.CompleteAsync(Client, "/api/auth/register/complete", optionsId,
            passkey.CreateAttestationResponse(optionsJson, PasskeyHttp.Origin));
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var completeDto = await completeResponse.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();
        Assert.NotNull(completeDto?.Token);

        // Consumed now.
        var afterComplete = await GetInvitesAsync(ownerToken);
        Assert.NotNull(Assert.Single(afterComplete, i => i.Id == invite.Id).UsedAt);

        // And a fresh registration attempt with the now-used token is rejected exactly like an
        // unknown one.
        var reuseResponse = await RawBeginAsync("Someone Else", invite.Token);
        Assert.Equal(HttpStatusCode.Forbidden, reuseResponse.StatusCode);
        var error = await reuseResponse.Content.ReadFromJsonAsync<RegistrationGateErrorDto>();
        Assert.Equal("invite_invalid", error?.Reason);
    }

    private static async Task<(string OptionsId, string OptionsJson)> ReadBeginResponseAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<PasskeyBeginResponseDto>();
        Assert.NotNull(dto);
        return (dto!.OptionsId, dto.OptionsJson);
    }
}

/// <summary>Concurrent double-complete race on the SAME invite (audit A10) - two begin flows (e.g.
/// two browser tabs following the same shared link) both succeed, but only the first complete may
/// consume the invite; the second must fail cleanly (403, no half-created user) rather than also
/// registering. Own class/DB: needs its own fresh owner+invite, and asserts the exact AuthUsers
/// count afterward.</summary>
public class RegistrationGateInviteModeDoubleCompleteRaceTests(InviteModeFactory factory) : RegistrationGateInviteModeTestsBase(factory)
{
    [Fact]
    public async Task DoubleCompleteRace_LoserFailsCleanly_WinnerUnaffected()
    {
        var ownerToken = await RegisterOwnerAsync();
        var invite = await CreateInviteAsync(ownerToken);

        var winnerBegin = await RawBeginAsync("Anna", invite.Token);
        Assert.Equal(HttpStatusCode.OK, winnerBegin.StatusCode);
        var winnerBeginDto = await winnerBegin.Content.ReadFromJsonAsync<PasskeyBeginResponseDto>();
        Assert.NotNull(winnerBeginDto);

        var loserBegin = await RawBeginAsync("Anna Impostor", invite.Token);
        Assert.Equal(HttpStatusCode.OK, loserBegin.StatusCode);
        var loserBeginDto = await loserBegin.Content.ReadFromJsonAsync<PasskeyBeginResponseDto>();
        Assert.NotNull(loserBeginDto);

        using var winnerKey = new FakePasskey();
        using var loserKey = new FakePasskey();

        var winnerResponse = await PasskeyHttp.CompleteAsync(Client, "/api/auth/register/complete", winnerBeginDto!.OptionsId,
            winnerKey.CreateAttestationResponse(winnerBeginDto.OptionsJson, PasskeyHttp.Origin));
        Assert.Equal(HttpStatusCode.OK, winnerResponse.StatusCode);

        var loserResponse = await PasskeyHttp.CompleteAsync(Client, "/api/auth/register/complete", loserBeginDto!.OptionsId,
            loserKey.CreateAttestationResponse(loserBeginDto.OptionsJson, PasskeyHttp.Origin));
        Assert.Equal(HttpStatusCode.Forbidden, loserResponse.StatusCode);
        var error = await loserResponse.Content.ReadFromJsonAsync<RegistrationGateErrorDto>();
        Assert.Equal("invite_invalid", error?.Reason);

        // The loser's transaction rolled back cleanly - no half-created user left behind: only
        // the owner (Alex) and the winner (Anna) exist, never a third "Anna Impostor" row.
        await Factory.WithDbAsync(async db =>
        {
            var displayNames = await db.AuthUsers.Select(u => u.DisplayName).ToListAsync();
            Assert.Equal(2, displayNames.Count);
            Assert.DoesNotContain("Anna Impostor", displayNames);
        });
    }
}

/// <summary>Invite management (POST/GET/DELETE /api/auth/invites) is owner-only: a non-owner
/// session gets 403 (manual Forbid(), see AuthController.IsOwnerAsync), and a bare API key gets
/// 403 too - via the scope-only-failure exception (AlwaysChallengeAuthorizationMiddlewareResultHandler),
/// since these three actions are deliberately absent from ApiKeyScopes for every slot.</summary>
public class RegistrationGateInviteCrudOwnershipTests(InviteModeFactory factory) : RegistrationGateInviteModeTestsBase(factory)
{
    [Fact]
    public async Task InviteCrud_OwnerOnly_NonOwnerSessionAndApiKeyBothForbidden()
    {
        var ownerToken = await RegisterOwnerAsync();
        var invite = await CreateInviteAsync(ownerToken);

        // A non-owner needs a valid invite themselves just to exist in invite mode.
        var nonOwnerBegin = await RawBeginAsync("Anna", invite.Token);
        Assert.Equal(HttpStatusCode.OK, nonOwnerBegin.StatusCode);
        var nonOwnerBeginDto = await nonOwnerBegin.Content.ReadFromJsonAsync<PasskeyBeginResponseDto>();
        Assert.NotNull(nonOwnerBeginDto);
        using var nonOwnerKey = new FakePasskey();
        var nonOwnerComplete = await PasskeyHttp.CompleteAsync(Client, "/api/auth/register/complete", nonOwnerBeginDto!.OptionsId,
            nonOwnerKey.CreateAttestationResponse(nonOwnerBeginDto.OptionsJson, PasskeyHttp.Origin));
        Assert.Equal(HttpStatusCode.OK, nonOwnerComplete.StatusCode);
        var nonOwnerToken = (await nonOwnerComplete.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>())!.Token!;

        // Non-owner session: 403 on all three invite-management endpoints.
        using (var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/invites"))
        {
            createRequest.Headers.Add("X-Session-Token", nonOwnerToken);
            Assert.Equal(HttpStatusCode.Forbidden, (await Client.SendAsync(createRequest)).StatusCode);
        }
        using (var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/invites"))
        {
            listRequest.Headers.Add("X-Session-Token", nonOwnerToken);
            Assert.Equal(HttpStatusCode.Forbidden, (await Client.SendAsync(listRequest)).StatusCode);
        }
        using (var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/invites/{invite.Id}"))
        {
            deleteRequest.Headers.Add("X-Session-Token", nonOwnerToken);
            Assert.Equal(HttpStatusCode.Forbidden, (await Client.SendAsync(deleteRequest)).StatusCode);
        }

        // A bare API key (any slot - none of them list the invite endpoints in ApiKeyScopes)
        // gets 403 too, via the scope-only-failure exception, before ownership is even checked.
        using var generateKeyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/settings/ha-api-key/generate");
        generateKeyRequest.Headers.Add("X-Session-Token", ownerToken);
        var keyResponse = await Client.SendAsync(generateKeyRequest);
        Assert.Equal(HttpStatusCode.OK, keyResponse.StatusCode);
        var apiKey = (await keyResponse.Content.ReadFromJsonAsync<HaApiKeyGenerateResponseDto>())!.ApiKey;

        using var apiKeyClient = ApiKeyTestHelpers.CreateClientWithKey(Factory, apiKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await apiKeyClient.PostAsync("/api/auth/invites", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await apiKeyClient.GetAsync("/api/auth/invites")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await apiKeyClient.DeleteAsync($"/api/auth/invites/{invite.Id}")).StatusCode);

        // Control: the owner can do all three.
        using (var ownerListRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/invites"))
        {
            ownerListRequest.Headers.Add("X-Session-Token", ownerToken);
            Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(ownerListRequest)).StatusCode);
        }
        using (var ownerDeleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/invites/{invite.Id}"))
        {
            ownerDeleteRequest.Headers.Add("X-Session-Token", ownerToken);
            Assert.Equal(HttpStatusCode.NoContent, (await Client.SendAsync(ownerDeleteRequest)).StatusCode);
        }
    }
}
