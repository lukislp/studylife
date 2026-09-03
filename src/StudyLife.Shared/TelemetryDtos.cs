using System.ComponentModel.DataAnnotations;
namespace StudyLife.Shared;

/// <summary>
/// POST /api/telemetry request body (docs/ARCHITECTURE.md "Telemetry", phase 2 client beacon).
/// One batch per flush from TelemetryService (web) or a native INativeTelemetry.DrainAsync
/// merge - see TelemetryController for consent/demo handling and the event-to-instrument mapping.
/// </summary>
public class TelemetryBatchDto
{
    /// <summary>Random, client-generated, rotated after 24h - never derived from the user id.
    /// Only used to correlate a boot with its own API calls within logs/dashboards, not stored
    /// against the user.</summary>
    [Required]
    [RegularExpression("^[A-Za-z0-9_-]{16,32}$")]
    public string SessionId { get; set; } = "";
    [MaxLength(20)]
    public string Platform { get; set; } = "web";
    [MaxLength(40)]
    public string AppVersion { get; set; } = "";
    [MaxLength(5)]
    public string Language { get; set; } = "";
    [MaxLength(20)]
    public string Connection { get; set; } = "unknown";
    /// <summary>Max 50 per batch (TelemetryService flushes at 25 - the server cap leaves headroom
    /// for a native DrainAsync merge landing in the same flush).</summary>
    [MaxLength(50)]
    public List<TelemetryEventDto> Events { get; set; } = new();
}

/// <summary>
/// One telemetry event. Deliberately one flat DTO for every event <see cref="Type"/> rather than
/// a polymorphic hierarchy - the wire format is a JSON object with a small, fixed field set per
/// type (see the contract table in docs/ARCHITECTURE.md's Telemetry section) and every field
/// beyond <see cref="Type"/>/<see cref="At"/> is optional, so one shape covers all ten types
/// without a discriminated-union deserializer. Unknown/irrelevant fields for a given type are
/// simply left null and ignored by TelemetryController's mapping switch.
///
/// Naming note: the exception type name for a <c>error</c> event is carried as
/// <see cref="ErrorType"/> (wire field "errorType"), NOT "type" - a JSON object cannot carry two
/// same-named keys ("type" already names the event's own type discriminator, "boot"/"error"/...),
/// so the wire format necessarily uses a different key for it despite how the event field is
/// occasionally shorthanded elsewhere as just "type".
/// </summary>
public class TelemetryEventDto
{
    /// <summary>boot | vitals | api | sse | navigation | error | app_launch | app_resource |
    /// health_query | push. Anything else is dropped and counted under
    /// studylife.client.events.dropped{reason="unknown_type"}.</summary>
    [Required]
    [MaxLength(20)]
    public string Type { get; set; } = "";
    /// <summary>Unix ms, client clock - ordering/logs only, never used for anything security-relevant.</summary>
    public long At { get; set; }

    // boot
    public bool? Cold { get; set; }
    public double? HtmlMs { get; set; }
    public double? BootScriptMs { get; set; }
    public double? WasmDownloadMs { get; set; }
    public double? RuntimeReadyMs { get; set; }
    public double? FirstRenderMs { get; set; }
    public double? DashboardReadyMs { get; set; }
    public double? DownloadBytes { get; set; }
    public bool? SwCacheHit { get; set; }

    // vitals (ms, except Cls which is a unitless ratio)
    public double? Ttfb { get; set; }
    public double? Fcp { get; set; }
    public double? Lcp { get; set; }
    public double? Inp { get; set; }
    public double? Cls { get; set; }

    // api
    [MaxLength(120)]
    public string? Route { get; set; }
    [MaxLength(10)]
    public string? Method { get; set; }
    public int? Status { get; set; }
    public double? DurationMs { get; set; }
    public bool? NotModified { get; set; }
    public int? Retries { get; set; }

    // sse (connected|reconnect|fallback_poll|closed) and push (registered|received|shown) -
    // mutually exclusive event types, safe to share one field.
    [MaxLength(30)]
    public string? Event { get; set; }

    // navigation
    [MaxLength(120)]
    public string? Page { get; set; }
    public double? RenderMs { get; set; }

    // error
    [MaxLength(20)]
    public string? Kind { get; set; } // dotnet|js|native_crash|native_hang|native_anr (also reused by health_query below)
    [MaxLength(120)]
    public string? ErrorType { get; set; }
    [MaxLength(64)]
    public string? StackHash { get; set; }
    [MaxLength(4096)]
    public string? Stack { get; set; }
    public bool? Fatal { get; set; }

    // app_launch
    public double? ColdMs { get; set; }
    public double? WarmMs { get; set; }
    public double? WebviewReadyMs { get; set; }

    // app_resource (MetricKit daily aggregate)
    public double? PeakMemoryMb { get; set; }
    public double? CpuSeconds { get; set; }
    public double? CellularBytes { get; set; }
    public double? WifiBytes { get; set; }

    // health_query (Kind above carries hrv|sleep|steps|vo2max here)
    [MaxLength(20)]
    public string? Authorization { get; set; } // granted|denied|undetermined
    [MaxLength(20)]
    public string? Result { get; set; } // error|empty|below_minimum|sufficient
    public bool? OutlierFiltered { get; set; }

    // push
    public double? LatencyMs { get; set; }
}
