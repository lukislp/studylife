// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Options;

namespace StudyLife.Server.Configuration;

/// <summary>
/// Audit 2026-11-16 L10: without <c>Fido2:ServerDomain</c> the passkey relying-party id comes from
/// the request's Host header (AuthController.CreateFido2). Behind the documented gateway that is
/// fine, but a request that reaches the server some other way can move the relying party. Outside
/// Development this logs one prominent warning at startup that names the settings to pin. Not an
/// error yet: existing installations keep starting unchanged; a future major version will require
/// the setting (docs/SECURITY-ASSURANCE.md section 9, README "Pin the passkey domain").
/// </summary>
public sealed class Fido2DomainStartupCheck(
    IOptions<Fido2Options> options,
    IHostEnvironment environment,
    ILogger<Fido2DomainStartupCheck> logger) : IHostedService
{
    internal const string Warning =
        "Fido2:ServerDomain is not configured, so the passkey relying-party id is taken from each " +
        "request's Host header. Pin it to the public domain users sign in on: set " +
        "Fido2__ServerDomain=<your-domain> and Fido2__Origins__0=https://<your-domain>. " +
        "A future major version will refuse to start without it.";

    /// <summary>True when the warning applies: not Development and no pinned domain.</summary>
    internal static bool ShouldWarn(Fido2Options fido2, IHostEnvironment env) =>
        !env.IsDevelopment() && string.IsNullOrWhiteSpace(fido2.ServerDomain);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (ShouldWarn(options.Value, environment))
            logger.LogWarning("{Fido2DomainWarning}", Warning);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
