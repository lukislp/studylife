using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// 2026-09-11 audit, finding 6: the three guards added to the dynamic-client flow
/// (AuthController.10.OAuthClients.cs) - PKCE on the assertion, the displayed-scope echo, and
/// key supersede on re-consent. GenericOAuthConnectFlowTests keeps covering the base flow.
/// </summary>
public class GenericOAuthConnectPkceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public GenericOAuthConnectPkceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private const string RedirectUri = "https://example-addon.test/callback";
    private static readonly List<string> Scopes = new() { "WebhooksProxy.List" };

    private static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string AssertionOf(GenericConnectResponseDto result) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(result.RedirectTo).Query)["assertion"] ?? "";

    private async Task SeedClientAsync(string clientId) => await _factory.WithDbAsync(async db =>
    {
        db.OAuthClients.Add(new OAuthClientEntity
        {
            ClientId = clientId,
            Name = "PKCE add-on",
            Description = "d",
            AllowedRedirectUris = RedirectUri,
            RequestedScopes = string.Join(',', Scopes),
            OwnerAuthUserId = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    });

    private Task CleanupAsync(string clientId) => _factory.WithDbAsync(async db =>
    {
        db.OAuthClients.RemoveRange(await db.OAuthClients.Where(c => c.ClientId == clientId).ToListAsync());
        db.ClientApiKeys.RemoveRange(await db.ClientApiKeys.Where(k => k.ClientId == clientId).ToListAsync());
        await db.SaveChangesAsync();
    });

    private async Task<string> ConnectAsync(string clientId, string? challenge, string? method = "S256", List<string>? scopes = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var session = _factory.CreateClient();
        var response = await session.PostAsJsonAsync("/api/auth/connect", new GenericConnectRequestDto
        {
            ClientId = clientId,
            RedirectUri = RedirectUri,
            State = "s",
            CodeChallenge = challenge,
            CodeChallengeMethod = challenge is null ? null : method,
            Scopes = scopes ?? Scopes,
        });
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK) return "";
        return AssertionOf((await response.Content.ReadFromJsonAsync<GenericConnectResponseDto>())!);
    }

    private async Task<HttpResponseMessage> ExchangeAsync(string clientId, string assertion, string? verifier)
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        return await anon.PostAsJsonAsync("/api/auth/assertion-exchange",
            new GenericAssertionExchangeRequestDto { ClientId = clientId, Assertion = assertion, CodeVerifier = verifier });
    }

    [Fact]
    public async Task Pkce_HappyPath_VerifierRedeemsTheAssertion()
    {
        await SeedClientAsync("pkce-ok");
        var (verifier, challenge) = NewPkce();

        var assertion = await ConnectAsync("pkce-ok", challenge);
        var response = await ExchangeAsync("pkce-ok", assertion, verifier);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await response.Content.ReadFromJsonAsync<GenericAssertionExchangeResponseDto>())!.ApiKey));
        await CleanupAsync("pkce-ok");
    }

    [Fact]
    public async Task Pkce_WrongOrMissingVerifier_IsRejected_AndTheAssertionStaysRedeemable()
    {
        await SeedClientAsync("pkce-wrong");
        var (verifier, challenge) = NewPkce();
        var (otherVerifier, _) = NewPkce();
        var assertion = await ConnectAsync("pkce-wrong", challenge);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ExchangeAsync("pkce-wrong", assertion, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ExchangeAsync("pkce-wrong", assertion, otherVerifier)).StatusCode);
        // Neither attempt consumed it - the legitimate client can still finish within the window.
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync("pkce-wrong", assertion, verifier)).StatusCode);
        await CleanupAsync("pkce-wrong");
    }

    [Theory]
    [InlineData("too-short", "S256")]
    [InlineData("nope!nope!nope!nope!nope!nope!nope!nope!nope!nope!", "S256")]
    [InlineData("YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXowMTIzNDU2Nzg5YWJjZGVmZ2g", "plain")]
    public async Task Connect_WithMalformedChallengeOrMethod_ReturnsBadRequest(string challenge, string method)
    {
        await SeedClientAsync("pkce-malformed");
        await ConnectAsync("pkce-malformed", challenge, method, expected: HttpStatusCode.BadRequest);
        await CleanupAsync("pkce-malformed");
    }

    [Fact]
    public async Task Connect_WithoutChallenge_StillWorksWhilePkceIsOptional()
    {
        // Consent:RequirePkce is unset in the test host - the two existing dynamic clients
        // (studylife-cli, studylife-alexa) do not send a challenge yet.
        await SeedClientAsync("pkce-optional");
        var assertion = await ConnectAsync("pkce-optional", null);
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync("pkce-optional", assertion, null)).StatusCode);
        await CleanupAsync("pkce-optional");
    }

    [Fact]
    public async Task Connect_WithScopesThatDifferFromTheRegistration_ReturnsConflict()
    {
        await SeedClientAsync("scope-echo");
        await ConnectAsync("scope-echo", null, scopes: new List<string> { "WebhooksProxy.List", "Notes.GetAll" }, expected: HttpStatusCode.Conflict);
        await ConnectAsync("scope-echo", null, scopes: new List<string>(), expected: HttpStatusCode.Conflict);
        await CleanupAsync("scope-echo");
    }

    [Fact]
    public async Task Reconnect_SupersedesThePreviousKey()
    {
        await SeedClientAsync("supersede");
        var first = await (await ExchangeAsync("supersede", await ConnectAsync("supersede", null), null))
            .Content.ReadFromJsonAsync<GenericAssertionExchangeResponseDto>();
        using (var firstKey = ApiKeyTestHelpers.CreateClientWithKey(_factory, first!.ApiKey))
            Assert.Equal(HttpStatusCode.OK, (await firstKey.GetAsync("/api/auth/whoami")).StatusCode);

        var second = await (await ExchangeAsync("supersede", await ConnectAsync("supersede", null), null))
            .Content.ReadFromJsonAsync<GenericAssertionExchangeResponseDto>();

        using var oldKey = ApiKeyTestHelpers.CreateClientWithKey(_factory, first.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldKey.GetAsync("/api/auth/whoami")).StatusCode);
        using var newKey = ApiKeyTestHelpers.CreateClientWithKey(_factory, second!.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await newKey.GetAsync("/api/auth/whoami")).StatusCode);
        var live = await _factory.WithDbAsync(db => db.ClientApiKeys.CountAsync(k => k.ClientId == "supersede" && k.AuthUserId == 1));
        Assert.Equal(1, live);
        await CleanupAsync("supersede");
    }
}
