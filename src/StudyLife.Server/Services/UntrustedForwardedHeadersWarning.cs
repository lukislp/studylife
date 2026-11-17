// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Server.Services;

/// <summary>
/// Runs right after UseForwardedHeaders. ASP.NET Core's middleware removes the X-Forwarded-* values
/// it applied and records them as X-Original-*; when they are still on the request without that
/// record, they came from an upstream that is not trusted
/// (ForwardedHeadersConfig) and were ignored. That is either an attack or a reverse proxy the
/// operator has not listed yet - in the second case the symptom is an endless HTTPS redirect, so
/// this logs the address to add, once per process and address (bounded, so a flood of forged
/// headers from many addresses cannot flood the log).
/// </summary>
public sealed class UntrustedForwardedHeadersWarning(RequestDelegate next, ILogger<UntrustedForwardedHeadersWarning> logger)
{
    private const int MaxReportedAddresses = 16;
    private readonly HashSet<string> _reported = [];

    public Task InvokeAsync(HttpContext context)
    {
        // A trusted proxy's values were applied and leave X-Original-For / -Proto behind (a longer
        // chain may still carry earlier hops in X-Forwarded-For); only headers that were ignored
        // outright count.
        var headers = context.Request.Headers;
        if ((headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("X-Forwarded-Proto"))
            && !headers.ContainsKey("X-Original-For") && !headers.ContainsKey("X-Original-Proto"))
        {
            var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            bool first;
            lock (_reported) first = _reported.Count < MaxReportedAddresses && _reported.Add(remote);
            if (first)
                logger.LogWarning(
                    "Ignored X-Forwarded-* headers from {RemoteAddress}, which is not a trusted proxy. If this is your reverse proxy, "
                    + "list that address as ForwardedHeaders__KnownProxies__0 (or its network as ForwardedHeaders__KnownNetworks__0).",
                    remote);
        }
        return next(context);
    }
}
