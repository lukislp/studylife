// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>The one log line an operator needs after the forwarded-header default narrowed
/// (2026-11-16 audit M8): which upstream sent X-Forwarded-* without being trusted.</summary>
public class UntrustedForwardedHeadersWarningTests
{
    private sealed class ListLogger : ILogger<UntrustedForwardedHeadersWarning>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private static async Task SendAsync(UntrustedForwardedHeadersWarning middleware, string remote, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        foreach (var (name, value) in headers) context.Request.Headers[name] = value;
        await middleware.InvokeAsync(context);
    }

    [Fact]
    public async Task IgnoredHeaders_AreReportedOncePerAddress()
    {
        var logger = new ListLogger();
        var nextCalls = 0;
        var middleware = new UntrustedForwardedHeadersWarning(_ => { nextCalls++; return Task.CompletedTask; }, logger);

        await SendAsync(middleware, "192.168.20.200", ("X-Forwarded-Proto", "https"));
        await SendAsync(middleware, "192.168.20.200", ("X-Forwarded-For", "198.51.100.7"));
        await SendAsync(middleware, "10.0.0.9", ("X-Forwarded-For", "198.51.100.7"));

        Assert.Equal(3, nextCalls); // never blocks
        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains("192.168.20.200", logger.Warnings[0]);
        Assert.Contains("ForwardedHeaders__KnownProxies__0", logger.Warnings[0]);
    }

    [Fact]
    public async Task AppliedHeaders_AndPlainRequests_AreNotReported()
    {
        var logger = new ListLogger();
        var middleware = new UntrustedForwardedHeadersWarning(_ => Task.CompletedTask, logger);

        await SendAsync(middleware, "127.0.0.1", ("X-Forwarded-For", "198.51.100.1"), ("X-Original-For", "127.0.0.1:5000"));
        await SendAsync(middleware, "192.168.1.5");

        Assert.Empty(logger.Warnings);
    }
}
