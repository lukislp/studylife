// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Security;
using StudyLife.Server.Configuration;

namespace StudyLife.Server.Services;

/// <summary>
/// Applies the TLS settings from <see cref="CacheOptions"/> to the StackExchange.Redis
/// configuration. Kept out of Program.cs so the decision table is testable:
/// <list type="bullet">
/// <item>TLS off (no ssl=true): nothing changes, the TLS keys are ignored.</item>
/// <item>TLS on, no CA path: no custom callback - .NET's default validation against the system
/// trust store, host name included. Right for a Redis with a publicly trusted certificate.</item>
/// <item>TLS on, CA path set: the CA file is loaded once at startup (missing or empty file =
/// startup error) and <see cref="PrivateCaCertificateValidator"/> becomes the validation
/// callback.</item>
/// <item><see cref="CacheOptions.TlsHost"/> set: becomes SslHost, the name every connection
/// is checked against.</item>
/// </list>
/// There is intentionally no switch that turns validation off.
/// </summary>
public static class RedisTlsConfiguration
{
    /// <summary>Returns the validation callback it installed, or null when the default validation
    /// applies (TLS off, or no CA configured) - the return value exists for the tests.</summary>
    public static RemoteCertificateValidationCallback? Apply(StackExchange.Redis.ConfigurationOptions redisOptions, CacheOptions cacheOptions)
    {
        ArgumentNullException.ThrowIfNull(redisOptions);
        ArgumentNullException.ThrowIfNull(cacheOptions);

        if (!redisOptions.Ssl)
            return null;

        if (!string.IsNullOrWhiteSpace(cacheOptions.TlsHost))
            redisOptions.SslHost = cacheOptions.TlsHost.Trim();

        if (!string.IsNullOrWhiteSpace(cacheOptions.TlsCaCertificatePath))
        {
            var roots = PrivateCaCertificateValidator.LoadTrustedRoots(cacheOptions.TlsCaCertificatePath.Trim());
            var callback = PrivateCaCertificateValidator.CreateCallback(roots);
            redisOptions.CertificateValidation += callback;
            return callback;
        }
        return null;
    }
}
