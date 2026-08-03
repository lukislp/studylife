using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// PKCE-style native-app token handoff (api/auth/handoff + api/auth/exchange) - see
/// AppReturnContext.BuildTokenReturnRedirectAsync for the rationale (the real session token
/// never has to travel through a studylife:// custom-scheme redirect or the Windows loopback
/// listener, only a short-lived, single-use, verifier-bound code does).
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
        var challenge = Base64UrlEncode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    [Fact]
    public async Task HandoffThenExchange_WithCorrectVerifier_ReturnsTheToken()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var (verifier, challenge) = NewPkcePair();

        var handoff = await client.PostAsJsonAsync("/api/auth/handoff",
            new AuthHandoffRequestDto { Token = "sample-session-token", CodeChallenge = challenge });
        Assert.Equal(HttpStatusCode.OK, handoff.StatusCode);
        var handoffResult = await handoff.Content.ReadFromJsonAsync<AuthHandoffResponseDto>();
        Assert.False(string.IsNullOrEmpty(handoffResult!.Code));

        var exchange = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = handoffResult.Code, CodeVerifier = verifier });
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        var exchangeResult = await exchange.Content.ReadFromJsonAsync<AuthExchangeResponseDto>();
        Assert.Equal("sample-session-token", exchangeResult!.Token);
    }

    [Fact]
    public async Task Exchange_WithWrongVerifier_IsRejected()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var (_, challenge) = NewPkcePair();

        var handoff = await client.PostAsJsonAsync("/api/auth/handoff",
            new AuthHandoffRequestDto { Token = "sample-session-token", CodeChallenge = challenge });
        var handoffResult = await handoff.Content.ReadFromJsonAsync<AuthHandoffResponseDto>();

        var (wrongVerifier, _) = NewPkcePair();
        var exchange = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = handoffResult!.Code, CodeVerifier = wrongVerifier });
        Assert.Equal(HttpStatusCode.Unauthorized, exchange.StatusCode);
    }

    [Fact]
    public async Task Exchange_FailedAttemptDoesNotBurnTheCode_CorrectVerifierStillWorksAfterwards()
    {
        // Whoever intercepts the code (the exact scenario this whole mechanism defends
        // against) must not be able to grief the legitimate app's real exchange call just by
        // firing one request with a garbage verifier first - found live in production testing.
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var (verifier, challenge) = NewPkcePair();

        var handoff = await client.PostAsJsonAsync("/api/auth/handoff",
            new AuthHandoffRequestDto { Token = "sample-session-token", CodeChallenge = challenge });
        var handoffResult = await handoff.Content.ReadFromJsonAsync<AuthHandoffResponseDto>();

        var (wrongVerifier, _) = NewPkcePair();
        var failedAttempt = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = handoffResult!.Code, CodeVerifier = wrongVerifier });
        Assert.Equal(HttpStatusCode.Unauthorized, failedAttempt.StatusCode);

        var realAttempt = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = handoffResult.Code, CodeVerifier = verifier });
        Assert.Equal(HttpStatusCode.OK, realAttempt.StatusCode);
        var result = await realAttempt.Content.ReadFromJsonAsync<AuthExchangeResponseDto>();
        Assert.Equal("sample-session-token", result!.Token);
    }

    [Fact]
    public async Task Exchange_CodeIsSingleUse_SecondAttemptIsRejectedEvenWithCorrectVerifier()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var (verifier, challenge) = NewPkcePair();

        var handoff = await client.PostAsJsonAsync("/api/auth/handoff",
            new AuthHandoffRequestDto { Token = "sample-session-token", CodeChallenge = challenge });
        var handoffResult = await handoff.Content.ReadFromJsonAsync<AuthHandoffResponseDto>();

        var first = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = handoffResult!.Code, CodeVerifier = verifier });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await client.PostAsJsonAsync("/api/auth/exchange",
            new AuthExchangeRequestDto { Code = handoffResult.Code, CodeVerifier = verifier });
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
    public async Task Handoff_MissingFields_IsRejected()
    {
        using var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);

        var missingToken = await client.PostAsJsonAsync("/api/auth/handoff",
            new AuthHandoffRequestDto { Token = "", CodeChallenge = "abc" });
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        var missingChallenge = await client.PostAsJsonAsync("/api/auth/handoff",
            new AuthHandoffRequestDto { Token = "abc", CodeChallenge = "" });
        Assert.Equal(HttpStatusCode.BadRequest, missingChallenge.StatusCode);
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
