using System.Net;
using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// POST /api/system/regenerate-calendar-token (SystemController) - unlike the former
/// GET bootstrap-key, deliberately NOT exempt from auth (reasoning in the controller). The
/// former regenerate-api-key endpoint (global, rotating key) was removed with Phase 3 -
/// its tests along with it, see the comment in CustomWebApplicationFactory.cs.
/// </summary>
public class SystemControllerRegenerateAuthTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SystemControllerRegenerateAuthTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task RegenerateCalendarToken_WithoutKey_Returns401()
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var response = await client.PostAsync("/api/system/regenerate-calendar-token", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>Same isolation pattern as above, for the calendar token.</summary>
public class SystemControllerRegenerateCalendarTokenFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SystemControllerRegenerateCalendarTokenFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task RegenerateCalendarToken_ReturnsNewToken_AndImmediatelyInvalidatesOldTokenOnIcsFeed()
    {
        var client = _factory.CreateClient(); // Start the host + logged in by default via session token.

        var initialResponse = await client.GetAsync("/api/system/calendar-token");
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var oldToken = (await initialResponse.Content.ReadFromJsonAsync<CalendarTokenResponseDto>())!.CalendarToken;

        var response = await client.PostAsync("/api/system/regenerate-calendar-token", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<RegenerateCalendarTokenResponseDto>();
        Assert.NotNull(dto);
        Assert.False(string.IsNullOrEmpty(dto!.CalendarToken));
        Assert.NotEqual(oldToken, dto.CalendarToken);

        // regenerate-calendar-token itself requires a real session (SystemController.
        // SessionAuthUserId), but the ICS feed underneath checks exclusively against the
        // separate ?calendarToken= (resolves its owner through that, see Program.cs).
        var noKeyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, null);
        var oldTokenResponse = await noKeyClient.GetAsync($"/api/sessions/ics?calendarToken={oldToken}");
        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenResponse.StatusCode);

        var newTokenResponse = await noKeyClient.GetAsync($"/api/sessions/ics?calendarToken={dto.CalendarToken}");
        Assert.Equal(HttpStatusCode.OK, newTokenResponse.StatusCode);
    }
}
