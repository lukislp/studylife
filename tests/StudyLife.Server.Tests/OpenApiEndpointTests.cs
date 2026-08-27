using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace StudyLife.Server.Tests;

/// <summary>
/// GET /openapi/v1.json (audit finding D2 - formal API contract). Two things are pinned here:
///
/// 1. The endpoint must be reachable WITHOUT any credential. AuthorizationOptions.FallbackPolicy
///    (ApiAccess, see StudyLifeAuthorizationPolicies) applies to every endpoint that carries no
///    authorization metadata of its own - app.MapOpenApi() in Program.cs therefore needs its own
///    .AllowAnonymous(), exactly like the SPA fallback (index.html) and the Apple
///    site-association endpoint right above it. Without that call this test 401s instead of 200 -
///    that regression is exactly what this test exists to catch.
/// 2. The generated document actually contains the DTO component schemas a client generator
///    needs (spot-checked by name, not by full content - the committed docs/api/openapi.json is
///    the source of truth for the exact shape, see the "openapi-contract" CI job).
/// </summary>
public class OpenApiEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OpenApiEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetOpenApiDocument_WithoutAnyCredential_Returns200()
    {
        // Deliberately a bare client with NEITHER the session-token header CustomWebApplicationFactory
        // normally attaches by default NOR an API key - see ApiKeyTestHelpers, the same pattern
        // used by every other "must work anonymously" test in this suite (e.g. AasaEndpointTests).
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey: null);

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetOpenApiDocument_ContainsKeyDtoComponentSchemas()
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey: null);

        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var schemas = document.GetProperty("components").GetProperty("schemas");

        // A representative cross-section, not the full DTO list - controllers return typed
        // ActionResult<T>, so real component schema names fall out for free; this is a tripwire
        // against a future controller regressing to a bare IActionResult/anonymous object (see
        // RestoreStatusResponseDto and friends in StudyLife.Shared/Dtos.cs for what that fix
        // looks like) or the OpenAPI generation itself silently breaking.
        foreach (var expectedSchema in new[]
        {
            "StudySessionDto", "NoteDto", "UserSettingsDto", "CourseDto", "CourseGoalDto",
            "TimerStateDto", "WhoamiResponseDto",
            "McpConnectRequestDto", "McpConnectResponseDto",
            "McpAssertionExchangeRequestDto", "McpAssertionExchangeResponseDto",
            "CaptureConnectRequestDto", "CaptureConnectResponseDto",
            "CaptureAssertionExchangeRequestDto", "CaptureAssertionExchangeResponseDto",
        })
        {
            Assert.True(
                schemas.TryGetProperty(expectedSchema, out _),
                $"Expected component schema '{expectedSchema}' to be present in the generated OpenAPI document.");
        }
    }

    [Fact]
    public async Task GetOpenApiDocument_DeclaresTheTwoHeaderSecuritySchemes()
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey: null);

        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var securitySchemes = document.GetProperty("components").GetProperty("securitySchemes");

        var sessionToken = securitySchemes.GetProperty("SessionToken");
        Assert.Equal("apiKey", sessionToken.GetProperty("type").GetString());
        Assert.Equal("X-Session-Token", sessionToken.GetProperty("name").GetString());
        Assert.Equal("header", sessionToken.GetProperty("in").GetString());

        var apiKey = securitySchemes.GetProperty("ApiKey");
        Assert.Equal("apiKey", apiKey.GetProperty("type").GetString());
        Assert.Equal("X-Api-Key", apiKey.GetProperty("name").GetString());
        Assert.Equal("header", apiKey.GetProperty("in").GetString());
    }

    /// <summary>
    /// Audit finding D2 follow-up: the default generator only derives a schema's "required" set
    /// from C# `required`-keyword/constructor-parameter syntax - none of the plain mutable DTOs
    /// in StudyLife.Shared/Dtos.cs use that syntax, so without
    /// StudyLifeOpenApiRequiredPropertiesTransformer every schema would report "required: []"
    /// regardless of which fields are actually guaranteed non-null (this under-specifies the
    /// contract for client generators, e.g. a consumer contract test failed on exactly
    /// "Course.id required in model but schema required: []"). Pins the transformer's
    /// CLR-nullability-based fix on CourseDto (a plain non-nullable int Id) and StudySessionDto
    /// (a mix of non-nullable and nullable `string?` fields on the same class).
    /// </summary>
    [Fact]
    public async Task GetOpenApiDocument_MarksNonNullablePropertiesAsRequired()
    {
        var client = ApiKeyTestHelpers.CreateClientWithKey(_factory, apiKey: null);

        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var schemas = document.GetProperty("components").GetProperty("schemas");

        var courseRequired = schemas.GetProperty("CourseDto").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("id", courseRequired);

        var sessionRequired = schemas.GetProperty("StudySessionDto").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToHashSet();
        // Non-nullable CLR properties: required.
        foreach (var expectedRequired in new[] { "id", "courseId", "courseName", "startTime", "endTime", "isCompleted", "timerModeId" })
            Assert.Contains(expectedRequired, sessionRequired);
        // Nullable CLR properties (string? Topic/Notes/RecurrenceGroupId): stay optional.
        foreach (var expectedOptional in new[] { "topic", "notes", "recurrenceGroupId" })
            Assert.DoesNotContain(expectedOptional, sessionRequired);
    }
}
