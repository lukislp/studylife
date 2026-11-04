// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Npgsql;
using StudyLife.Server.Configuration;

namespace StudyLife.Server.Services;

/// <summary>
/// Applies <see cref="DatabaseOptions.TlsCaCertificatePath"/> to the Npgsql connection string, so
/// the app verifies the PostgreSQL server certificate without the connection string in the
/// Secret having to carry any TLS parameter. The decision table:
/// <list type="bullet">
/// <item>No CA path: the connection string is returned unchanged (Npgsql's own default, which
/// is encryption when offered but no certificate check). Keeps local setups such as
/// docker-compose.scale.yml, whose Postgres serves no TLS, working.</item>
/// <item>CA path set: the CA file is checked once at startup (missing or empty file = startup
/// error, through <see cref="PrivateCaCertificateValidator.LoadTrustedRoots"/>), then
/// <c>SSL Mode=VerifyFull</c> and <c>Root Certificate=&lt;path&gt;</c> are forced. VerifyFull means
/// TLS is mandatory, the chain must end at that CA, and the certificate must name the host from
/// the connection string. Whatever SSL Mode the connection string had is overridden - the key
/// expresses the intent to verify, a weaker mode next to it is a mistake, not a choice.</item>
/// </list>
/// Npgsql's own VerifyFull + Root Certificate path is used instead of a custom validation callback
/// (as Redis has) for two reasons: Npgsql refuses a user callback together with VerifyFull, and
/// Npgsql reads the Root Certificate file on every new physical connection. The CloudNativePG CA
/// is rotated by the operator, and the projected Secret file follows it - an in-memory copy loaded
/// at startup would go stale in long-running pods. Revocation stays unchecked (Npgsql's default),
/// same reasoning as in <see cref="PrivateCaCertificateValidator"/>: an internal CA publishes no
/// CRL or OCSP endpoint.
///
/// There is intentionally no switch that turns validation off: a connection string that asks for
/// Trust Server Certificate or names a different Root Certificate while the key is set is a
/// startup error.
/// </summary>
public static class PostgresTlsConfiguration
{
    /// <summary>Returns the connection string the app should use.</summary>
    public static string Apply(string connectionString, DatabaseOptions databaseOptions)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        if (string.IsNullOrWhiteSpace(databaseOptions.TlsCaCertificatePath))
            return connectionString;

        var caPath = databaseOptions.TlsCaCertificatePath.Trim();
        // Fail fast on a missing or empty trust root, like Cache:TlsCaCertificatePath does. The
        // collection itself is not kept - Npgsql reads the file per connection (see summary).
        foreach (var root in PrivateCaCertificateValidator.LoadTrustedRoots(caPath))
            root.Dispose();

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        // Read through the keyword indexer: the typed property is obsolete in newer Npgsql
        // versions, and the warning would fail the build.
        if (builder.TryGetValue("Trust Server Certificate", out var trust)
            && bool.TryParse(Convert.ToString(trust, System.Globalization.CultureInfo.InvariantCulture), out var trustAll)
            && trustAll)
        {
            throw new InvalidOperationException(
                "Database:TlsCaCertificatePath is set, but the connection string has Trust Server Certificate=true. Remove it - certificate validation cannot be switched off.");
        }

        if (!string.IsNullOrWhiteSpace(builder.RootCertificate)
            && !string.Equals(builder.RootCertificate.Trim(), caPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database:TlsCaCertificatePath ({caPath}) and the connection string's Root Certificate ({builder.RootCertificate}) disagree. Set the CA in one place only.");
        }

        builder.SslMode = SslMode.VerifyFull;
        builder.RootCertificate = caPath;
        return builder.ConnectionString;
    }
}
