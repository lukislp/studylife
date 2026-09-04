using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyLife.Shared;

/// <summary>
/// System.Text.Json source-generated metadata for the DTOs the client reads and writes on its
/// hot paths (dashboard start: sessions, settings, courses, goals, programs; the offline write
/// queue; telemetry batches). Without it every first (de)serialisation of a type builds its
/// metadata through reflection at runtime - cheap on the server, but on the iOS app (Mono AOT)
/// the first GET api/sessions cost ~500 ms from the phone's point of view while the server
/// answered in 18 ms (measured through the client telemetry on 2026-09-04). The generated
/// converters remove that warm-up and the per-call reflection cost.
///
/// Options match what the server's MVC serialiser produces and what the client's
/// GetFromJsonAsync defaults to (JsonSerializerDefaults.Web: camelCase, case-insensitive
/// reading). [JsonPropertyName] attributes on individual DTOs are honoured by the generator.
/// Types not listed here still work through the reflection fallback, see
/// StudyLife.Client's StudyLifeJson.Options.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StudySessionDto))]
[JsonSerializable(typeof(List<StudySessionDto>))]
[JsonSerializable(typeof(UserSettingsDto))]
[JsonSerializable(typeof(CourseDto))]
[JsonSerializable(typeof(List<CourseDto>))]
[JsonSerializable(typeof(CourseGoalDto))]
[JsonSerializable(typeof(List<CourseGoalDto>))]
[JsonSerializable(typeof(NoteDto))]
[JsonSerializable(typeof(List<NoteDto>))]
[JsonSerializable(typeof(StudyProgramSummaryDto))]
[JsonSerializable(typeof(List<StudyProgramSummaryDto>))]
[JsonSerializable(typeof(StudyProgramDetailDto))]
[JsonSerializable(typeof(CourseResourceDto))]
[JsonSerializable(typeof(List<CourseResourceDto>))]
[JsonSerializable(typeof(AccountInfoDto))]
[JsonSerializable(typeof(DemoInfoDto))]
[JsonSerializable(typeof(SystemCapabilitiesResponseDto))]
[JsonSerializable(typeof(VersionResponseDto))]
[JsonSerializable(typeof(TelemetryBatchDto))]
[JsonSerializable(typeof(TelemetryEventDto))]
// Dashboard summary (GET api/dashboard/summary): the one response that replaced the raw dashboard
// fetches above on the client's hottest path - its nested DTOs are reached through this root.
[JsonSerializable(typeof(DashboardSummaryDto))]
public partial class StudyLifeJsonContext : JsonSerializerContext
{
}
