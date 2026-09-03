using System.Net;
using System.Net.Http.Json;
using StudyLife.Server.Controllers;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>2026-09 audit follow-ups (S10/S13/M7): declarative DTO limits, APNs token shape,
/// ICS carriage-return escaping, and the Permissions-Policy header.</summary>
public class SecurityPolishTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SecurityPolishTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Note_WithOversizedTitle_IsRejectedByModelValidation()
    {
        var response = await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = new string('t', 501),
            Content = "ok",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Note_WithinLimits_StillWorks()
    {
        var response = await _client.PostAsJsonAsync("/api/notes", new NoteDto
        {
            Title = new string('t', 500),
            Content = new string('c', 50_000),
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Session_WithOversizedTopic_IsRejected()
    {
        var now = DateTime.Now;
        var response = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 31,
            CourseName = "x",
            CourseColor = "#000000",
            Topic = new string('x', 501),
            StartTime = now.AddDays(7),
            EndTime = now.AddDays(7).AddHours(1),
            TimerModeId = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("abc")] // too short to be a device token
    [InlineData("../../3/device/other")] // path traversal into the APNs request path
    [InlineData("token with spaces")]
    [InlineData("tok?en#1")]
    public async Task ApnsSubscribe_RejectsPathUnsafeTokens(string token)
    {
        var response = await _client.PostAsJsonAsync("/api/push/subscribe-apns", new ApnsSubscribeRequest(token, "iPhone"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApnsSubscribe_AcceptsHexToken()
    {
        var token = new string('a', 64);
        var response = await _client.PostAsJsonAsync("/api/push/subscribe-apns", new ApnsSubscribeRequest(token, "iPhone"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _client.PostAsJsonAsync("/api/push/unsubscribe-apns", new ApnsSubscribeRequest(token, null));
    }

    [Fact]
    public async Task IcsExport_StripsCarriageReturns_FromTopics()
    {
        var now = DateTime.Now;
        var create = await _client.PostAsJsonAsync("/api/sessions", new StudySessionDto
        {
            CourseId = 31,
            CourseName = "x",
            CourseColor = "#000000",
            Topic = "Line one\r\nX-INJECTED:yes",
            StartTime = now.AddDays(9),
            EndTime = now.AddDays(9).AddHours(1),
            TimerModeId = 1,
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var token = await _client.GetFromJsonAsync<CalendarTokenResponseDto>("/api/system/calendar-token");
        using var anon = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var ics = await anon.GetStringAsync($"/api/sessions/ics?calendarToken={token!.CalendarToken}");

        Assert.Contains("Line one\\nX-INJECTED:yes", ics);
        Assert.DoesNotContain("\r\nX-INJECTED", ics);
    }

    [Fact]
    public async Task Responses_CarryPermissionsPolicy()
    {
        var response = await _client.GetAsync("/");
        Assert.True(response.Headers.TryGetValues("Permissions-Policy", out var values));
        Assert.Contains("microphone=(self)", values!.Single());
        Assert.Contains("camera=()", values!.Single());
    }
}
