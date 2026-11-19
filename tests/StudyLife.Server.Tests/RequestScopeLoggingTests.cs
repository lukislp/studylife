// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StudyLife.Server.Diagnostics;

namespace StudyLife.Server.Tests;

/// <summary>Audit 2026-11-19 S10(b): a log line written while serving a share link must not carry the token.</summary>
public class RequestScopeLoggingTests
{
    private sealed class ScopeCapture : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public List<string> Seen { get; } = [];

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new Capture(this);
        public void Dispose() { }

        private sealed class Capture(ScopeCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner._scopes.ForEachScope((scope, list) => list.Add(scope?.ToString() ?? ""), owner.Seen);
        }
    }

    private static async Task<List<string>> ScopesWhileLoggingAsync(bool withRedaction)
    {
        var capture = new ScopeCapture();
        // The request activity only exists while something listens (the app's OpenTelemetry does in
        // production); it is what puts TraceId/SpanId into the scopes.
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(capture);
        if (withRedaction) builder.Logging.WithoutRequestPathScope();
        await using var app = builder.Build();
        app.MapGet("/api/progress/shared/{token}", (ILogger<RequestScopeLoggingTests> log) =>
        {
            log.LogWarning("something failed");
            return Results.Ok();
        });
        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync("/api/progress/shared/secret-token-123");
        response.EnsureSuccessStatusCode();
        await app.StopAsync();
        return capture.Seen;
    }

    [Fact]
    public async Task Without_the_rule_the_request_path_is_in_the_scopes()
    {
        // Control: proves the leak this guards against exists on this runtime.
        var scopes = await ScopesWhileLoggingAsync(withRedaction: false);

        Assert.Contains(scopes, s => s.Contains("secret-token-123"));
    }

    [Fact]
    public async Task With_the_rule_no_scope_carries_the_request_path()
    {
        var scopes = await ScopesWhileLoggingAsync(withRedaction: true);

        Assert.DoesNotContain(scopes, s => s.Contains("secret-token-123"));
        // The trace link that IncludeScopes exists for keeps working.
        Assert.Contains(scopes, s => s.Contains("TraceId"));
    }
}
