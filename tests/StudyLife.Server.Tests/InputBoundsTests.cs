using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Controllers;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Request-boundary limits added by the 2026-09 audit (findings S2/S4/S11): the exam-plan
/// generator's upper bounds, the push-endpoint URL policy, and the history window clamp. Each
/// is a plain integration check that the limit is enforced with a 400 (or tolerated, for the
/// clamp) and that nothing was persisted for a rejected request.
/// </summary>
public class InputBoundsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public InputBoundsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task UseBuiltInCatalogAsync() =>
        BackgroundTaskTestSettings.PutAsync(_client, s => s.ActiveStudyProgramId = null);

    private async Task<int> SessionCountAsync() =>
        await _factory.WithDbAsync(db => db.Sessions.IgnoreQueryFilters().CountAsync());

    // ---------- POST /api/planner/exam-plan ----------

    [Fact]
    public async Task ExamPlan_ExamDateBeyondTwoYears_IsRejected_AndCreatesNothing()
    {
        await UseBuiltInCatalogAsync();
        var before = await SessionCountAsync();

        var response = await _client.PostAsJsonAsync("/api/planner/exam-plan", new ExamPlanRequestDto
        {
            CourseId = CourseCatalog.AppliedAICourses[0].Id,
            ExamDate = new DateTime(2999, 1, 1),
            TotalHours = 30000,
            SessionLengthMinutes = 1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SessionCountAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(481)]
    [InlineData(100000)]
    public async Task ExamPlan_SessionLengthOutsideFiveToFourEightyMinutes_IsRejected(int minutes)
    {
        await UseBuiltInCatalogAsync();

        var response = await _client.PostAsJsonAsync("/api/planner/exam-plan", new ExamPlanRequestDto
        {
            CourseId = CourseCatalog.AppliedAICourses[0].Id,
            ExamDate = DateTime.Today.AddDays(30),
            TotalHours = 3,
            SessionLengthMinutes = minutes,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExamPlan_TotalHoursAboveOneThousand_IsRejected()
    {
        await UseBuiltInCatalogAsync();

        var response = await _client.PostAsJsonAsync("/api/planner/exam-plan", new ExamPlanRequestDto
        {
            CourseId = CourseCatalog.AppliedAICourses[0].Id,
            ExamDate = DateTime.Today.AddDays(30),
            TotalHours = 1000.5,
            SessionLengthMinutes = 90,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExamPlan_NullOrZeroOptionalFields_StillFallBackToDefaults()
    {
        // The bounds must not break the documented "null/0 = server default" contract.
        await UseBuiltInCatalogAsync();

        var response = await _client.PostAsJsonAsync("/api/planner/exam-plan", new ExamPlanRequestDto
        {
            CourseId = CourseCatalog.AppliedAICourses[0].Id,
            ExamDate = DateTime.Today.AddDays(14),
            TotalHours = 0,
            SessionLengthMinutes = 0,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<List<StudySessionDto>>();
        Assert.NotEmpty(created!);
        Assert.All(created!, s => Assert.Equal(90, (s.EndTime - s.StartTime).TotalMinutes));
    }

    // ---------- GET /api/sessions/history?days= ----------

    [Theory]
    [InlineData(int.MinValue)] // Math.Abs would throw -> used to be a 500
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task GetHistory_OutOfRangeDays_IsClampedInsteadOfFailing(int days)
    {
        var response = await _client.GetAsync($"/api/sessions/history?days={days}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<List<StudySessionDto>>());
    }

    // ---------- POST /api/push/subscribe ----------

    [Theory]
    [InlineData("http://push.example.com/plain-http")]
    [InlineData("https://10.0.0.5:8080/admin/delete")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://localhost/push")]
    [InlineData("https://redis/")]
    [InlineData("https://studylife-web.studylife-scale.svc.cluster.local/api/")]
    [InlineData("https://user:pw@push.example.com/x")]
    [InlineData("this is not a url")]
    public async Task Subscribe_NonPublicHttpsEndpoint_IsRejected_AndNotStored(string endpoint)
    {
        var response = await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest(endpoint, "p256dh-key-value", "auth-key-value"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var stored = await _factory.WithDbAsync(db => db.PushSubscriptions.IgnoreQueryFilters().AnyAsync(s => s.Endpoint == endpoint));
        Assert.False(stored);
    }

    [Fact]
    public async Task Subscribe_OversizedKeys_AreRejected()
    {
        var response = await _client.PostAsJsonAsync("/api/push/subscribe",
            new PushSubscribeRequest($"https://push.example.com/{Guid.NewGuid():N}", new string('a', 513), "auth-key-value"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Subscribe_RealPushServiceShapes_AreStillAccepted()
    {
        foreach (var endpoint in new[]
        {
            $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}",
            $"https://updates.push.services.mozilla.com/wpush/v2/{Guid.NewGuid():N}",
            $"https://web.push.apple.com/{Guid.NewGuid():N}",
            $"https://ntfy.example.org/up/{Guid.NewGuid():N}", // self-hosted UnifiedPush distributor
        })
        {
            var response = await _client.PostAsJsonAsync("/api/push/subscribe",
                new PushSubscribeRequest(endpoint, "p256dh-key-value", "auth-key-value"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}

public class OutboundUrlPolicyTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd12::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("::ffff:10.0.0.1", false)] // IPv4-mapped private
    public void IsPublicAddress_ClassifiesRanges(string ip, bool expected) =>
        Assert.Equal(expected, Services.OutboundUrlPolicy.IsPublicAddress(System.Net.IPAddress.Parse(ip)));

    [Fact]
    public void IsAcceptablePushEndpoint_RejectsOverlongUrl() =>
        Assert.False(Services.OutboundUrlPolicy.IsAcceptablePushEndpoint("https://push.example.com/" + new string('x', 2100)));
}
