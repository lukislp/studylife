namespace StudyLife.Server.Configuration;

/// <summary>
/// Telemetry section (docs/ARCHITECTURE.md "Telemetry"). Everything here is opt-in: with no
/// <see cref="MetricsPort"/> and no <see cref="OtlpEndpoint"/> the process behaves exactly as it
/// did before telemetry existed (Pi/docker-compose and every test run).
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>Fallback for both sample ratios - 10 %, the value the raw reads used to carry
    /// inline at their call sites.</summary>
    public const double DefaultSampleRatio = 0.10;

    /// <summary>Dedicated non-public Kestrel listener that answers nothing but GET /metrics.
    /// Null (unset) = metrics off.</summary>
    public int? MetricsPort { get; set; }

    /// <summary>OTLP collector endpoint, e.g.
    /// http://otel-collector.monitoring.svc.cluster.local:4317. Empty/unset = traces off.</summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>Head-based trace sampling ratio; <see cref="DefaultSampleRatio"/> when unset.
    /// Deliberately nullable so an explicitly empty value keeps falling back to the default,
    /// exactly like the former GetValue&lt;double?&gt;(...) ?? 0.10.</summary>
    public double? TraceSampleRatio { get; set; }

    /// <summary>Ratio the client reports its own telemetry at (served via
    /// /api/system/capabilities); <see cref="DefaultSampleRatio"/> when unset.</summary>
    public double? ClientSampleRatio { get; set; }
}

/// <summary>
/// Encrypted hop ingress-nginx -&gt; Kestrel (Kubernetes only, see k8s/04-web.yaml). Both paths
/// must be set for the 8443 listener to be added; the Pi/docker-compose deployment sets neither
/// and keeps running plain HTTP:8080.
/// </summary>
public sealed class WebBackendTlsOptions
{
    public const string SectionName = "WebBackendTls";

    public string? CertPath { get; set; }
    public string? KeyPath { get; set; }

    /// <summary>True only when both paths are configured - the exact condition Program.cs used
    /// to spell out at its call site.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(CertPath) && !string.IsNullOrEmpty(KeyPath);
}

/// <summary>
/// Cache/Redis section. <see cref="Provider"/> switches between the process-local in-memory
/// cache (default, unchanged single-instance behaviour) and Redis (multi-pod operation).
/// </summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    public const string MemoryProvider = "Memory";
    public const string RedisProvider = "Redis";

    public string Provider { get; set; } = MemoryProvider;

    /// <summary>Required when <see cref="Provider"/> is Redis - enforced by an explicit startup
    /// throw in Program.cs rather than a DataAnnotation, because "required only when another key
    /// has a particular value" is not expressible as an attribute (same for
    /// <see cref="DataProtectionKeyRingOptions"/> and <see cref="DatabaseOptions.ConnectionString"/>).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Redis AUTH password, kept separate from the connection string so Kubernetes can
    /// source it from the same `redis-auth` Secret the Redis StatefulSet reads.</summary>
    public string? Password { get; set; }

    /// <summary>Dedicated Redis ACL user instead of "default" - what makes enabling AUTH a
    /// zero-downtime change (see docs/SCALING.md "Redis AUTH").</summary>
    public string? User { get; set; }

    public bool IsRedis => string.Equals(Provider, RedisProvider, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Certificate that encrypts the DataProtection key ring at rest in Redis (section name stays
/// "DataProtection"; the type is named for the key ring so it cannot be confused with ASP.NET
/// Core's own DataProtectionOptions). Only consulted in the
/// Redis branch, where both paths are mandatory (see <see cref="CacheOptions.ConnectionString"/>
/// for why that requirement is not a DataAnnotation).
/// </summary>
public sealed class DataProtectionKeyRingOptions
{
    public const string SectionName = "DataProtection";

    public string? CertPath { get; set; }
    public string? KeyPath { get; set; }
}

/// <summary>
/// Database section. <see cref="Provider"/> picks SQLite (default, single instance) or Postgres
/// (scaled operation); <see cref="Migrate"/> is what keeps the k8s worker from racing the web
/// pod's Database.Migrate().
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public const string SqliteProvider = "Sqlite";
    public const string PostgresProvider = "Postgres";

    public string Provider { get; set; } = SqliteProvider;

    /// <summary>Required when <see cref="Provider"/> is Postgres - explicit startup throw, see
    /// <see cref="CacheOptions.ConnectionString"/>.</summary>
    public string? ConnectionString { get; set; }

    public bool Migrate { get; set; } = true;

    public bool IsPostgres => string.Equals(Provider, PostgresProvider, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Which upstream addresses are trusted to report the real client IP/scheme, i.e. the input of
/// <see cref="Services.ForwardedHeadersConfig"/>. Section name stays "ForwardedHeaders" (env
/// ForwardedHeaders__KnownNetworks__0 etc.); the type is named after what it decides rather than
/// after the section, so it does not read like ASP.NET Core's own ForwardedHeadersOptions.
/// </summary>
public sealed class TrustedProxyOptions
{
    public const string SectionName = Services.ForwardedHeadersConfig.SectionName;

    /// <summary>CIDRs, e.g. 10.42.3.0/24. Listing any network or proxy REPLACES the RFC1918 +
    /// loopback defaults entirely.</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>Single IP addresses.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Hops to walk; null keeps ASP.NET Core's own default of 1.</summary>
    public int? ForwardLimit { get; set; }
}
