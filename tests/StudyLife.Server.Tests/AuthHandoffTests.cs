// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// PKCE-style native-app token handoff (api/auth/handoff + api/auth/exchange) - see
/// AppReturnContext.BuildTokenReturnRedirectAsync for the rationale (the real session token
/// never has to travel through a studylife:// custom-scheme redirect or the Windows loopback
/// listener, only a short-lived, single-use, verifier-bound code does). Since the 2026-11-16
/// audit (M3) the handoff is session-only: the token handed off is the one the request
/// authenticates with, the challenge must have the S256 shape, and both bodies are size-capped.
/// </summary>
public class AuthHandoffTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthHandoffTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string Verifier, string Challenge) NewPkcePair()
    {
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private Task<string> NewSessionAsync() => _factory.WithDbAsync(async db =>
    {
        var token = AuthSessionService.IssueSession(db, 1, DateTime.UtcNow);
        await db.SaveChangesAsync();
        return token;
    });

    private static Task<HttpResponseMessage> HandoffAsync(HttpClient client, string? sessionToken, HttpContent body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/handoff") { Content = body };
        if (sessionToken is not null) request.Headers.Add(AuthSessionService.TokenHeaderName, sessionToken);
        return client.SendAsync(request);
    }

    private async Task<(string Code, string Verifier, string Token)> HandOffNewSessionAsync(HttpClient client)
    {
        var token = await NewSessionAsync();
        var (verifier, challenge) = NewPkcePair();
        var handoff = await HandoffAsync(client, token, JsonContent.Create(new AuthHandoffRequestDto { CodeChallenge = challenge }));
        Assert.Equal(HttpStatusCode.OK, handoff.StatusCode);
        var code = (await handoff.Content.ReadFromJsonAsync<AuthHandoffResponseDto>())!.Code;
        Assert.Matches(PkceShape.CodePattern, code);
        return (code, verifier, token);
    }

    [Fact]
    public async Task HandoffThenExchange_WithCorrectVerifier_ReturnsTheSessionsOwnToken()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var (code, verifier, token) = await HandOffNewSessionAsync(client);

        var exchange = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = code, CodeVerifier = verifier });

        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        Assert.Equal(token, (await exchange.Content.ReadFromJsonAsync<AuthExchangeResponseDto>())!.Token);
    }

    /// <summary>Audit M3: anonymous callers used to be able to store arbitrary data in the cache
    /// that also holds the WebAuthn challenges.</summary>
    [Fact]
    public async Task Handoff_WithoutASession_IsRejected()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var (_, challenge) = NewPkcePair();

        var anonymous = await HandoffAsync(client, null, JsonContent.Create(new AuthHandoffRequestDto { CodeChallenge = challenge }));
        var forged = await HandoffAsync(client, "not-a-session", JsonContent.Create(new AuthHandoffRequestDto { CodeChallenge = challenge }));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    /// <summary>A token in the body (the old request shape) is ignored: only the authenticated
    /// session is ever handed off.</summary>
    [Fact]
    public async Task Handoff_IgnoresATokenInTheBody()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var token = await NewSessionAsync();
        var (verifier, challenge) = NewPkcePair();

        var handoff = await HandoffAsync(client, token,
            new StringContent($$"""{"token":"attacker-chosen","codeChallenge":"{{challenge}}"}""", Encoding.UTF8, "application/json"));
        var code = (await handoff.Content.ReadFromJsonAsync<AuthHandoffResponseDto>())!.Code;
        var exchange = await client.PostAsJsonAsync("/api/auth/exchange", new AuthExchangeRequestDto { Code = code, CodeVerifier = verifier });

        Assert.Equal(token, (await exchange.Content.ReadFromJsonAsync<AuthExchangeResponseDto>())!.Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 44 characters
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa+")] // 43, not base64url
    public async Task Handoff_AChallengeOfTheWrongShape_IsRejected(string challenge)
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var token = await NewSessionAsync();

        var response = await HandoffAsync(client, token, JsonContent.Create(new AuthHandoffRequestDto { CodeChallenge = challenge }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Handoff_AndExchange_RefuseOversizedBodies()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var token = await NewSessionAsync();
        var padding = new string('a', 64 * 1024);

        var handoff = await HandoffAsync(client, token,
            new StringContent($$"""{"codeChallenge":"{{padding}}"}""", Encoding.UTF8, "application/json"));
        var exchange = await client.PostAsync("/api/auth/exchange",
            new StringContent($$"""{"code":"{{padding}}","codeVerifier":"x"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, handoff.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, exchange.StatusCode);
    }

    [Fact]
    public async Task Exchange_WithWrongVerifier_IsRejected()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var (code, _, _) = await HandOffNewSessionAsync(client);

        var (wrongVerifier, _) = NewPkcePair();
        var exchange = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = code, CodeVerifier = wrongVerifier });
        Assert.Equal(HttpStatusCode.Unauthorized, exchange.StatusCode);
    }

    [Fact]
    public async Task Exchange_FailedAttemptDoesNotBurnTheCode_CorrectVerifierStillWorksAfterwards()
    {
        // Whoever intercepts the code (the exact scenario this whole mechanism defends
        // against) must not be able to grief the legitimate app's real exchange call just by
        // firing one request with a garbage verifier first - found live in production testing.
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var (code, verifier, token) = await HandOffNewSessionAsync(client);

        var (wrongVerifier, _) = NewPkcePair();
        var failedAttempt = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = code, CodeVerifier = wrongVerifier });
        Assert.Equal(HttpStatusCode.Unauthorized, failedAttempt.StatusCode);

        var realAttempt = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = code, CodeVerifier = verifier });
        Assert.Equal(HttpStatusCode.OK, realAttempt.StatusCode);
        Assert.Equal(token, (await realAttempt.Content.ReadFromJsonAsync<AuthExchangeResponseDto>())!.Token);
    }

    [Fact]
    public async Task Exchange_CodeIsSingleUse_SecondAttemptIsRejectedEvenWithCorrectVerifier()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var (code, verifier, _) = await HandOffNewSessionAsync(client);

        var first = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = code, CodeVerifier = verifier });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = code, CodeVerifier = verifier });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Exchange_UnknownCode_IsRejected()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var (verifier, _) = NewPkcePair();

        var exchange = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = "does-not-exist", CodeVerifier = verifier });
        Assert.Equal(HttpStatusCode.Unauthorized, exchange.StatusCode);
    }

    [Fact]
    public async Task Exchange_MissingFields_IsRejected()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var missingVerifier = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = "abc", CodeVerifier = "" });
        Assert.Equal(HttpStatusCode.Unauthorized, missingVerifier.StatusCode);
    }
}
