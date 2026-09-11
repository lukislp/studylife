using System.Net;
using System.Net.Http.Json;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// 2026-09-11 audit: the Expensive and Telemetry rate-limit policies partition on the
/// NameIdentifier claim, but UseRateLimiter ran before UseAuthentication, so the claim was
/// never there and every "per-user" bucket silently became the per-IP fallback. Pinned here via
/// the Telemetry policy (30 batches/minute): user 1 exhausts its bucket, a second user from the
/// same client address must still be admitted. Own factory (own limiter state) - the class
/// fixture guarantees no sibling test has already spent part of the window.
/// </summary>
public class RateLimitPartitionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RateLimitPartitionTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static TelemetryBatchDto Batch() => new()
    {
        SessionId = "abcdefghijklmnop1234",
        Platform = "web",
        AppVersion = "1.0.0",
        Language = "en",
        Connection = "wifi",
        Events = new List<TelemetryEventDto> { new() { Type = "boot", Cold = true, HtmlMs = 10 } },
    };

    [Fact]
    public async Task TelemetryBucket_IsPerUser_NotPerClientAddress()
    {
        var userOne = _factory.CreateClient();
        var userTwoToken = await _factory.WithDbAsync(async db =>
        {
            var user = new AuthUserEntity { DisplayName = "Second user", CreatedAt = DateTime.UtcNow };
            db.AuthUsers.Add(user);
            await db.SaveChangesAsync();
            var token = AuthSessionService.IssueSession(db, user.Id, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return token;
        });
        using var userTwo = SessionTestHelpers.ClientWithSession(_factory, userTwoToken);

        // No consent granted, so every admitted batch answers 204 without recording anything -
        // only the limiter's verdict matters here.
        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await userOne.PostAsJsonAsync("/api/telemetry", Batch())).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await userOne.PostAsJsonAsync("/api/telemetry", Batch())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await userTwo.PostAsJsonAsync("/api/telemetry", Batch())).StatusCode);
    }
}
