using Microsoft.Extensions.Configuration;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Pure unit tests for DemoModeGuard.IsEnabled - no host needed, just an IConfiguration built
/// from an in-memory dictionary. The integration-level version of these same scenarios (over
/// the real HTTP pipeline) lives in AuthControllerEdgeTests.cs
/// (AuthControllerDemoModeTests / AuthControllerDemoModeUnconfirmedTests).
/// </summary>
public class DemoModeGuardTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void DemoModeNotRequested_IsDisabled()
    {
        Assert.False(DemoModeGuard.IsEnabled(Config()));
    }

    [Fact]
    public void DemoModeRequested_WithoutConfirmation_IsDisabled()
    {
        var config = Config(("DEMO_MODE", "true"));
        Assert.False(DemoModeGuard.IsEnabled(config));
    }

    [Fact]
    public void DemoModeRequested_WithWrongConfirmation_IsDisabled()
    {
        var config = Config(("DEMO_MODE", "true"), ("DEMO_MODE_CONFIRM_DATA_LOSS", "yes"));
        Assert.False(DemoModeGuard.IsEnabled(config));
    }

    [Fact]
    public void DemoModeRequested_WithCorrectConfirmation_IsEnabled()
    {
        var config = Config(("DEMO_MODE", "true"), ("DEMO_MODE_CONFIRM_DATA_LOSS", "yes-delete-all-data"));
        Assert.True(DemoModeGuard.IsEnabled(config));
    }

    [Fact]
    public void ConfirmationAlone_WithoutDemoMode_StaysDisabled()
    {
        var config = Config(("DEMO_MODE_CONFIRM_DATA_LOSS", "yes-delete-all-data"));
        Assert.False(DemoModeGuard.IsEnabled(config));
    }
}
