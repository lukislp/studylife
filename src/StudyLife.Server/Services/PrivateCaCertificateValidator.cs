// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace StudyLife.Server.Services;

/// <summary>
/// Server certificate validation for TLS peers whose certificate comes from a private CA that the
/// operating system does not know - the cluster-internal cert-manager CA behind Redis
/// (k8s/03-redis.yaml) and the Kubernetes API server's own CA (the ServiceAccount ca.crt).
///
/// The rules, in this order:
/// <list type="number">
/// <item>No certificate at all, or a host name mismatch, is always a failure. The name check is
/// done by SslStream against the host the client asked for (or StackExchange.Redis' SslHost) and
/// is NOT something a custom trust root can fix, so it is never ignored.</item>
/// <item>No errors at all means the system trust store already accepts the chain - fine.</item>
/// <item>A chain error is re-evaluated: the chain is rebuilt with the configured CA certificates as
/// the ONLY trust anchors (CustomRootTrust, the system roots play no part in that rebuild). The
/// peer is accepted only if that rebuild succeeds.</item>
/// </list>
///
/// Revocation is not checked in the rebuild: a cert-manager CA issuer publishes no CRL or OCSP
/// endpoint, so an online check could only ever fail or time out. Short certificate lifetimes
/// (cert-manager default 90 days) take the place of revocation for these internal certificates.
///
/// Replaces two earlier shortcuts: the Redis client accepted ANY certificate
/// (`CertificateValidation += ... => true`), and the Kubernetes API client checked only the
/// chain and ignored the host name result.
/// </summary>
public static class PrivateCaCertificateValidator
{
    /// <summary>Validates <paramref name="certificate"/> following the rules in the class
    /// summary. <paramref name="trustedRoots"/> must not be empty.</summary>
    public static bool Validate(
        X509Certificate? certificate,
        X509Chain? presentedChain,
        SslPolicyErrors sslPolicyErrors,
        X509Certificate2Collection trustedRoots)
    {
        ArgumentNullException.ThrowIfNull(trustedRoots);
        if (trustedRoots.Count == 0)
            throw new ArgumentException("At least one trusted CA certificate is required.", nameof(trustedRoots));

        if (certificate is null)
            return false;
        if ((sslPolicyErrors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            return false;
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        // Only RemoteCertificateChainErrors is left: decide on the chain against the private CA.
        // The certificate object belongs to the caller (SslStream disposes it), so only a copy
        // made here is disposed here.
        var ownedCopy = certificate is X509Certificate2 ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var leaf = ownedCopy ?? (X509Certificate2)certificate;
        using var ownedCopyScope = ownedCopy;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trustedRoots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // Intermediates the server sent along with its leaf (if any) are candidates for the
        // path, never anchors - anchors come from trustedRoots only.
        if (presentedChain is not null)
        {
            foreach (var element in presentedChain.ChainElements)
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        }
        return chain.Build(leaf);
    }

    /// <summary>
    /// Loads every certificate from a PEM file (one CA or a bundle). Throws if the file is missing
    /// or contains no certificate - a configured but unusable trust root is a configuration error
    /// that should stop the process at startup, not silently turn every TLS connection into a
    /// failure later.
    /// </summary>
    public static X509Certificate2Collection LoadTrustedRoots(string pemPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pemPath);
        if (!File.Exists(pemPath))
            throw new FileNotFoundException($"Trusted CA certificate file not found: {pemPath}", pemPath);

        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(pemPath);
        if (roots.Count == 0)
            throw new InvalidOperationException($"No certificate found in trusted CA file {pemPath}.");
        return roots;
    }

    /// <summary>Convenience overload: a ready-to-use callback for SslStream, HttpClientHandler
    /// and StackExchange.Redis that closes over the given trust roots.</summary>
    public static RemoteCertificateValidationCallback CreateCallback(X509Certificate2Collection trustedRoots)
    {
        ArgumentNullException.ThrowIfNull(trustedRoots);
        if (trustedRoots.Count == 0)
            throw new ArgumentException("At least one trusted CA certificate is required.", nameof(trustedRoots));
        return (_, certificate, chain, errors) => Validate(certificate, chain, errors, trustedRoots);
    }
}
