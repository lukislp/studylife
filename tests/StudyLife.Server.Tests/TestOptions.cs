using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;

namespace StudyLife.Server.Tests;

/// <summary>
/// Turns the "Section:Key" dictionaries the unit tests have always written into the typed options
/// the services now take. Deliberately routed through the REAL registration
/// (<see cref="StudyLifeOptionsRegistration.AddStudyLifeOptions"/>) rather than a hand-built
/// options instance: that way every test using this helper also asserts, implicitly, that the
/// production binding still maps those exact key names onto those exact properties.
/// </summary>
internal static class TestOptions
{
    public static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    public static IOptions<T> For<T>(IConfiguration configuration) where T : class =>
        Provider(configuration).GetRequiredService<IOptions<T>>();

    public static IOptions<T> For<T>(params (string Key, string? Value)[] settings) where T : class =>
        For<T>(Configuration(settings));

    public static IOptionsMonitor<T> MonitorFor<T>(IConfiguration configuration) where T : class =>
        Provider(configuration).GetRequiredService<IOptionsMonitor<T>>();

    public static IOptionsMonitor<T> MonitorFor<T>(params (string Key, string? Value)[] settings) where T : class =>
        MonitorFor<T>(Configuration(settings));

    private static ServiceProvider Provider(IConfiguration configuration) =>
        new ServiceCollection()
            .AddSingleton(configuration)
            .AddStudyLifeOptions()
            .BuildServiceProvider();
}
