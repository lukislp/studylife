// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Server.Diagnostics;

/// <summary>
/// Audit 2026-11-19 S10(b): the console logger runs with <c>IncludeScopes</c> (the trace and span id
/// scopes are what Grafana's log-to-trace link keys on). ASP.NET Core's hosting layer opens one more
/// scope per request that carries the raw <c>RequestPath</c>, so a warning logged while serving
/// <c>/api/progress/shared/{token}</c> would write the progress-share token to every log line, and
/// from there to Loki. The scope is created by the <c>Microsoft.AspNetCore.Hosting.Diagnostics</c>
/// logger only while that category is enabled at all, so switching the category off removes the
/// path (and the request id, which nothing here uses) and leaves the trace and span ids in place.
/// The request start/finish lines of that category were already below the configured level.
/// </summary>
public static class RequestScopeLogging
{
    internal const string HostingDiagnosticsCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>Adds the rule in code, not only in appsettings.json, so an operator's own Logging
    /// section cannot bring the path back by accident.</summary>
    public static ILoggingBuilder WithoutRequestPathScope(this ILoggingBuilder logging) =>
        logging.AddFilter(HostingDiagnosticsCategory, LogLevel.None);
}
