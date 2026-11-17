// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;

namespace StudyLife.Server.Tests;

/// <summary>Audit 2026-11-16 L10: one startup warning when the passkey domain is not pinned
/// outside Development, nothing otherwise, and never a startup failure.</summary>
public class Fido2DomainStartupCheckTests
{
    private sealed class FakeLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "StudyLife.Server";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<FakeLogger<Fido2DomainStartupCheck>> RunAsync(string environment, string? serverDomain)
    {
        var logger = new FakeLogger<Fido2DomainStartupCheck>();
        var check = new Fido2DomainStartupCheck(
            Options.Create(new Fido2Options { ServerDomain = serverDomain }),
            new FakeEnvironment(environment),
            logger);
        await check.StartAsync(CancellationToken.None);
        return logger;
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "")]
    [InlineData("Production", "   ")]
    [InlineData("Staging", null)]
    public async Task UnpinnedOutsideDevelopment_LogsOneWarningNamingTheSettings(string environment, string? domain)
    {
        var logger = await RunAsync(environment, domain);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Fido2__ServerDomain", entry.Message);
        Assert.Contains("Fido2__Origins__0", entry.Message);
        Assert.Contains("future major version", entry.Message);
    }

    [Fact]
    public async Task PinnedDomain_LogsNothing()
    {
        var logger = await RunAsync("Production", "studyatrium.example.com");
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Development_LogsNothing()
    {
        var logger = await RunAsync("Development", null);
        Assert.Empty(logger.Entries);
    }
}
