using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Shared.Tests;

/// <summary>The generated context must produce exactly the wire format the reflection-based
/// Web defaults produce - the server keeps serialising with reflection, the client now reads
/// with generated converters, and both sides must agree byte for byte.</summary>
public class StudyLifeJsonContextTests
{
    private static readonly JsonSerializerOptions Reflection = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Session_round_trip_matches_reflection_output()
    {
        var dto = new StudySessionDto
        {
            Id = 42,
            CourseId = 7,
            StartTime = new DateTime(2026, 9, 4, 8, 30, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc),
            IsCompleted = true,
        };

        var generated = JsonSerializer.Serialize(dto, StudyLifeJsonContext.Default.StudySessionDto);
        var reflection = JsonSerializer.Serialize(dto, Reflection);
        Assert.Equal(reflection, generated);

        var back = JsonSerializer.Deserialize(reflection, StudyLifeJsonContext.Default.StudySessionDto);
        Assert.NotNull(back);
        Assert.Equal(dto.Id, back!.Id);
        Assert.Equal(dto.StartTime, back.StartTime);
        Assert.Equal(dto.IsCompleted, back.IsCompleted);
    }

    [Fact]
    public void Settings_list_and_telemetry_types_are_generated()
    {
        Assert.NotNull(StudyLifeJsonContext.Default.UserSettingsDto);
        Assert.NotNull(StudyLifeJsonContext.Default.ListStudySessionDto);
        Assert.NotNull(StudyLifeJsonContext.Default.ListCourseDto);
        Assert.NotNull(StudyLifeJsonContext.Default.TelemetryBatchDto);
        Assert.NotNull(StudyLifeJsonContext.Default.SystemCapabilitiesResponseDto);
    }
}
