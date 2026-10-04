using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;

namespace StudyLife.Server.Discovery;

public static class MdnsRegistration
{
    /// <summary>Registers the announcer and its hosted service. Call only when
    /// Discovery:Mdns:Enabled (or Only) is set - nothing is registered otherwise.</summary>
    public static IServiceCollection AddMdnsAnnouncement(this IServiceCollection services)
    {
        services.AddSingleton<IMdnsAnnouncer>(sp => new MdnsAnnouncer(
            MdnsServiceDescription.From(sp.GetRequiredService<IOptions<MdnsOptions>>().Value, MdnsServiceDescription.CurrentVersion()),
            sp.GetRequiredService<ILogger<MdnsAnnouncer>>()));
        services.AddHostedService<MdnsHostedService>();
        return services;
    }
}

/// <summary>
/// <c>Discovery:Mdns:Only=true</c>: the process runs ONLY the announcer - no database, migrations,
/// Redis or web endpoints. This is the dedicated hostNetwork pod of k8s/optional/studylife-mdns.yaml
/// (web replicas sit on the pod network, where multicast cannot reach the LAN). Program.cs
/// branches here before any of the heavy setup.
/// </summary>
public static class MdnsOnlyHost
{
    /// <summary>Cheap early check on environment variables and command line only (the documented
    /// ways to set it), before the real configuration is built.</summary>
    public static bool IsRequested(string[] args)
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
        return config.Bind<MdnsOptions>(MdnsOptions.SectionName).Only;
    }

    /// <summary>A minimal generic host: configuration, logging, the validated options and the
    /// hosted service. Options are validated on start, so a missing/invalid Url stops it with the
    /// usual readable message.</summary>
    public static IHost Create(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddSingleton<IValidateOptions<MdnsOptions>, MdnsOptionsValidator>();
        builder.Services.AddOptions<MdnsOptions>().BindConfiguration(MdnsOptions.SectionName).ValidateOnStart();
        builder.Services.AddMdnsAnnouncement();
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }
}
