namespace StudyLife.Server.Configuration;

/// <summary>
/// One place where every configuration section this app understands is bound to a typed options
/// class. Before this existed, ~37 raw `_config["Section:Key"]`/`GetValue&lt;T&gt;("...")` reads were
/// scattered across Program.cs, the controllers and the services, each repeating the key name and
/// its default inline (2026-09 structure audit).
///
/// Key names, env-var mappings (Section__Key) and defaults are unchanged - the options classes
/// only move the SAME strings and the SAME fallbacks into one declaration per section, so the
/// prod ConfigMap (k8s/01-config-and-secret.yaml) and the docs keep describing the app exactly.
///
/// Two kinds of access exist and both are served from here:
/// <list type="bullet">
/// <item>Runtime consumers inject <c>IOptions&lt;T&gt;</c> (singletons that read once at
/// construction) or <c>IOptionsMonitor&lt;T&gt;</c> (everything that re-reads per request today, so
/// a configuration reload keeps taking effect exactly as it did with IConfiguration - and the
/// cached CurrentValue is cheaper than the former string lookup + conversion).</item>
/// <item>Program.cs needs several values BEFORE the container exists (Kestrel listeners, which
/// DbContext/cache provider to register at all). Those use <see cref="Bind{T}"/>, which produces
/// the same bound object from the raw configuration - still one typed read per section instead of
/// one string lookup per key.</item>
/// </list>
/// </summary>
public static class StudyLifeOptionsRegistration
{
    /// <summary>
    /// Binds every section into DI. Called once from Program.cs, before anything resolves an
    /// options instance.
    /// </summary>
    public static IServiceCollection AddStudyLifeOptions(this IServiceCollection services)
    {
        services.AddOptions<TelemetryOptions>().BindConfiguration(TelemetryOptions.SectionName);
        services.AddOptions<WebBackendTlsOptions>().BindConfiguration(WebBackendTlsOptions.SectionName);
        services.AddOptions<CacheOptions>().BindConfiguration(CacheOptions.SectionName);
        services.AddOptions<DataProtectionKeyRingOptions>().BindConfiguration(DataProtectionKeyRingOptions.SectionName);
        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);
        services.AddOptions<TrustedProxyOptions>().BindConfiguration(TrustedProxyOptions.SectionName);
        services.AddOptions<SpeechOptions>().BindConfiguration(SpeechOptions.SectionName);
        services.AddOptions<TtsOptions>().BindConfiguration(TtsOptions.SectionName);
        services.AddOptions<SttOptions>().BindConfiguration(SttOptions.SectionName);
        services.AddOptions<Fido2Options>().BindConfiguration(Fido2Options.SectionName);
        services.AddOptions<ConsentOptions>().BindConfiguration(ConsentOptions.SectionName);
        services.AddOptions<RegistrationOptions>().BindConfiguration(RegistrationOptions.SectionName);
        services.AddOptions<VapidOptions>().BindConfiguration(VapidOptions.SectionName);
        services.AddOptions<StudyLifeAiOptions>().BindConfiguration(StudyLifeAiOptions.SectionName);
        services.AddOptions<StudyLifeWebhooksOptions>().BindConfiguration(StudyLifeWebhooksOptions.SectionName);
        services.AddOptions<StudyLifeDevelopersOptions>().BindConfiguration(StudyLifeDevelopersOptions.SectionName);
        services.AddOptions<ApnsOptions>().BindConfiguration(ApnsOptions.SectionName);
        services.AddOptions<AppleOptions>().BindConfiguration(AppleOptions.SectionName);

        // The only section with a value that can be validated unconditionally: a ReplicaCount
        // below 1 was never a workable configuration (the shard filter divides by it), so it is
        // refused at startup with the attribute's message. Every other "required" key in this app
        // is required only in combination with another key (Cache:ConnectionString when
        // Cache:Provider=Redis, ...) - those keep their explicit, already-worded startup throws in
        // Program.cs, which DataAnnotations cannot express.
        services.AddOptions<WorkerOptions>()
            .BindConfiguration(WorkerOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Startup-time counterpart to injecting IOptions&lt;T&gt;: binds one section straight off the
    /// raw configuration, for the decisions Program.cs has to make before builder.Build().
    /// A missing section yields the options class with its declared defaults.
    /// </summary>
    public static T Bind<T>(this IConfiguration configuration, string sectionName) where T : new()
        => configuration.GetSection(sectionName).Get<T>() ?? new T();
}
