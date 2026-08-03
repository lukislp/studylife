using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Emergency login codes (api/auth/recovery/*, see docs/PLAN-passkey-recovery-codes.md).
/// Careful when extending: recovery/login has its own, strict rate-limit partition
/// (5 attempts/15 min per IP, in the TestServer all requests share the "unknown"
/// partition) - this class therefore deliberately stays under 5 login calls, the
/// limit test itself lives in its OWN class with its own factory.
/// </summary>
public class RecoveryCodeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RecoveryCodeTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Generate_RequiresRealSession()
    {
        // Rejected both anonymously AND via API key - a leaked key must not be able to
        // build itself emergency access (session requirement, same as for ha-api-key).
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var anonResponse = await anon.PostAsync("/api/auth/recovery/generate", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);
    }

    [Fact]
    public async Task GenerateLoginAndReuse_FullLifecycle()
    {
        // 1) Generate: exactly 8 codes in the documented format, status matches.
        var generate = await _client.PostAsync("/api/auth/recovery/generate", null);
        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        var first = await generate.Content.ReadFromJsonAsync<RecoveryCodesResponseDto>();
        Assert.NotNull(first);
        Assert.Equal(8, first!.Codes.Count);
        Assert.All(first.Codes, c => Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", c));

        // 2) Regenerating invalidates the old batch completely.
        var regenerate = await _client.PostAsync("/api/auth/recovery/generate", null);
        var second = await regenerate.Content.ReadFromJsonAsync<RecoveryCodesResponseDto>();
        Assert.NotNull(second);
        Assert.Empty(first.Codes.Intersect(second!.Codes));

        var status = await _client.GetFromJsonAsync<RecoveryStatusDto>("/api/auth/recovery/status");
        Assert.Equal(8, status!.TotalCount);
        Assert.Equal(8, status.UnusedCount);

        // 3) Old code after regeneration -> 401 (login call #1).
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var staleLogin = await anon.PostAsJsonAsync("/api/auth/recovery/login",
            new RecoveryLoginRequestDto { Code = first.Codes[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, staleLogin.StatusCode);

        // 4) Valid code -> session; lowercasing + hyphens are normalized
        //    (login call #2).
        var login = await anon.PostAsJsonAsync("/api/auth/recovery/login",
            new RecoveryLoginRequestDto { Code = second.Codes[0].ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();
        Assert.False(string.IsNullOrEmpty(session!.Token));

        // The issued session is a REAL session (a session-required endpoint works).
        using var recovered = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        recovered.DefaultRequestHeaders.Add(AuthSessionService.TokenHeaderName, session.Token);
        var accountInfo = await recovered.GetAsync("/api/auth/account-info");
        Assert.Equal(HttpStatusCode.OK, accountInfo.StatusCode);

        // 5) The same code is consumed -> 401 (login call #3), status counts down.
        var replay = await anon.PostAsJsonAsync("/api/auth/recovery/login",
            new RecoveryLoginRequestDto { Code = second.Codes[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var statusAfter = await _client.GetFromJsonAsync<RecoveryStatusDto>("/api/auth/recovery/status");
        Assert.Equal(7, statusAfter!.UnusedCount);
    }

    [Fact]
    public async Task Login_UnknownCode_IsRejected()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var response = await anon.PostAsJsonAsync("/api/auth/recovery/login",
            new RecoveryLoginRequestDto { Code = "AAAA-BBBB-CCCC" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_ResolvesOwningUser_NotJustAnyUser()
    {
        // Multi-user isolation: a code seeded directly for user 2 issues a session
        // for USER 2 - provable via the AuthUserId of the created session row.
        const string rawCode = "ZZZZTESTUSER";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            db.AuthUsers.Add(new AuthUserEntity { Id = 2, DisplayName = "Zweitnutzer", CreatedAt = DateTime.UtcNow });
            db.RecoveryCodes.Add(new RecoveryCodeEntity
            {
                AuthUserId = 2,
                CodeHash = AuthSessionService.HashToken(rawCode),
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var login = await anon.PostAsJsonAsync("/api/auth/recovery/login",
            new RecoveryLoginRequestDto { Code = rawCode });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<PasskeyCompleteResponseDto>();
        Assert.Equal("Zweitnutzer", session!.DisplayName);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudyLifeDb>();
            var tokenHash = AuthSessionService.HashToken(session.Token!);
            var issued = db.AuthSessions.Single(s => s.TokenHash == tokenHash);
            Assert.Equal(2, issued.AuthUserId);
        }
    }
}

/// <summary>
/// Own class (= own factory = own rate limiter): recovery/login is throttled to
/// 5 attempts/15 min per IP - brute-forcing the 12-character codes is the
/// only attack vector of the unauthenticated endpoint.
/// </summary>
public class RecoveryLoginRateLimitTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RecoveryLoginRateLimitTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task SixthAttempt_IsThrottledWith429()
    {
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        for (var i = 0; i < 5; i++)
        {
            var attempt = await anon.PostAsJsonAsync("/api/auth/recovery/login",
                new RecoveryLoginRequestDto { Code = $"XXXX-YYYY-{i:D4}" });
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        var sixth = await anon.PostAsJsonAsync("/api/auth/recovery/login",
            new RecoveryLoginRequestDto { Code = "XXXX-YYYY-9999" });
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
    }
}
