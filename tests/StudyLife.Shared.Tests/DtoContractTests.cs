using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>
/// Contract tests for the small response/transport DTOs that are otherwise only touched by
/// serializers at runtime: verifies defaults (what a client sees when the server omits a field)
/// and that a System.Text.Json round-trip with web defaults (camelCase, as ASP.NET Core uses)
/// preserves every property - guarding against typos in property names or accidentally
/// removed setters, which would silently produce empty fields on the client.
/// </summary>
public class DtoContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Web), Web)!;

    [Fact]
    public void HaApiKeyStatusDto_Defaults_MeanNoKeyGenerated()
    {
        var dto = new HaApiKeyStatusDto();

        Assert.False(dto.HasKey);
        Assert.Null(dto.CreatedAt);
    }

    [Fact]
    public void HaApiKeyStatusDto_RoundTrip_PreservesKeyStatusAndTimestamp()
    {
        var created = new DateTime(2026, 8, 1, 12, 30, 0);
        var dto = RoundTrip(new HaApiKeyStatusDto { HasKey = true, CreatedAt = created });

        Assert.True(dto.HasKey);
        Assert.Equal(created, dto.CreatedAt);
    }

    [Fact]
    public void HaApiKeyGenerateResponseDto_RoundTrip_PreservesPlaintextKeyAndTimestamp()
    {
        var created = new DateTime(2026, 8, 2, 9, 0, 0);
        var dto = RoundTrip(new HaApiKeyGenerateResponseDto { ApiKey = "sl_secret123", CreatedAt = created });

        Assert.Equal("sl_secret123", dto.ApiKey);
        Assert.Equal(created, dto.CreatedAt);
    }

    [Fact]
    public void HaApiKeyGenerateResponseDto_Default_HasEmptyKeyNotNull()
    {
        Assert.Equal("", new HaApiKeyGenerateResponseDto().ApiKey);
    }

    [Fact]
    public void AiApiKeyStatusDto_Defaults_MeanNoKeyGenerated()
    {
        var dto = new AiApiKeyStatusDto();

        Assert.False(dto.HasKey);
        Assert.Null(dto.CreatedAt);
    }

    [Fact]
    public void AiApiKeyStatusDto_RoundTrip_PreservesKeyStatusAndTimestamp()
    {
        var created = new DateTime(2026, 8, 1, 12, 30, 0);
        var dto = RoundTrip(new AiApiKeyStatusDto { HasKey = true, CreatedAt = created });

        Assert.True(dto.HasKey);
        Assert.Equal(created, dto.CreatedAt);
    }

    [Fact]
    public void AiApiKeyGenerateResponseDto_RoundTrip_PreservesPlaintextKeyAndTimestamp()
    {
        var created = new DateTime(2026, 8, 2, 9, 0, 0);
        var dto = RoundTrip(new AiApiKeyGenerateResponseDto { ApiKey = "sl_secret123", CreatedAt = created });

        Assert.Equal("sl_secret123", dto.ApiKey);
        Assert.Equal(created, dto.CreatedAt);
    }

    [Fact]
    public void AiApiKeyGenerateResponseDto_Default_HasEmptyKeyNotNull()
    {
        Assert.Equal("", new AiApiKeyGenerateResponseDto().ApiKey);
    }

    [Fact]
    public void McpApiKeyStatusDto_Defaults_MeanNoKeyGenerated()
    {
        var dto = new McpApiKeyStatusDto();

        Assert.False(dto.HasKey);
        Assert.Null(dto.CreatedAt);
    }

    [Fact]
    public void McpApiKeyStatusDto_RoundTrip_PreservesKeyStatusAndTimestamp()
    {
        var created = new DateTime(2026, 8, 1, 12, 30, 0);
        var dto = RoundTrip(new McpApiKeyStatusDto { HasKey = true, CreatedAt = created });

        Assert.True(dto.HasKey);
        Assert.Equal(created, dto.CreatedAt);
    }

    [Fact]
    public void McpApiKeyGenerateResponseDto_RoundTrip_PreservesPlaintextKeyAndTimestamp()
    {
        var created = new DateTime(2026, 8, 2, 9, 0, 0);
        var dto = RoundTrip(new McpApiKeyGenerateResponseDto { ApiKey = "sl_secret123", CreatedAt = created });

        Assert.Equal("sl_secret123", dto.ApiKey);
        Assert.Equal(created, dto.CreatedAt);
    }

    [Fact]
    public void McpApiKeyGenerateResponseDto_Default_HasEmptyKeyNotNull()
    {
        Assert.Equal("", new McpApiKeyGenerateResponseDto().ApiKey);
    }

    [Fact]
    public void VersionResponseDto_RoundTrip_PreservesVersionString()
    {
        Assert.Equal("1.42.0", RoundTrip(new VersionResponseDto { Version = "1.42.0" }).Version);
        Assert.Equal("", new VersionResponseDto().Version);
    }

    [Fact]
    public void DemoInfoDto_DefaultsToFalse_AndRoundTripsTrue()
    {
        // Always demo:false on a normal deployment - the default must never accidentally be true,
        // or every login page would try the demo auto-sign-in.
        Assert.False(new DemoInfoDto().Demo);
        Assert.True(RoundTrip(new DemoInfoDto { Demo = true }).Demo);
    }

    [Fact]
    public void PushSubscriptionDto_RoundTrip_PreservesAllThreeWebPushFields()
    {
        var dto = RoundTrip(new PushSubscriptionDto
        {
            Endpoint = "https://push.example/sub/1",
            P256dh = "BPubKey",
            Auth = "authSecret",
        });

        Assert.Equal("https://push.example/sub/1", dto.Endpoint);
        Assert.Equal("BPubKey", dto.P256dh);
        Assert.Equal("authSecret", dto.Auth);
    }

    [Fact]
    public void PushSubscriptionDto_Defaults_AreEmptyStringsNotNull()
    {
        // The server persists these verbatim; null defaults would blow up the WebPush library.
        var dto = new PushSubscriptionDto();

        Assert.Equal("", dto.Endpoint);
        Assert.Equal("", dto.P256dh);
        Assert.Equal("", dto.Auth);
    }

    [Fact]
    public void PlanProposal_Defaults_MatchTheAppWideCourseFallbacks()
    {
        var proposal = new PlanProposal();

        Assert.Equal(0, proposal.CourseId);
        Assert.Equal("", proposal.CourseName);
        Assert.Equal("#6C5CE7", proposal.CourseColor); // same fallback accent as the entities
        Assert.Null(proposal.Topic);
        Assert.Equal(default, proposal.Start);
        Assert.Equal(default, proposal.End);
    }

    [Fact]
    public void PlanProposal_RoundTrip_PreservesProposedSlot()
    {
        var proposal = RoundTrip(new PlanProposal
        {
            CourseId = 7,
            CourseName = "Analysis 1",
            CourseColor = "#123456",
            Topic = "Ableitungen",
            Start = new DateTime(2026, 8, 10, 9, 0, 0),
            End = new DateTime(2026, 8, 10, 10, 30, 0),
        });

        Assert.Equal(7, proposal.CourseId);
        Assert.Equal("Analysis 1", proposal.CourseName);
        Assert.Equal("#123456", proposal.CourseColor);
        Assert.Equal("Ableitungen", proposal.Topic);
        Assert.Equal(new DateTime(2026, 8, 10, 9, 0, 0), proposal.Start);
        Assert.Equal(new DateTime(2026, 8, 10, 10, 30, 0), proposal.End);
    }
}
