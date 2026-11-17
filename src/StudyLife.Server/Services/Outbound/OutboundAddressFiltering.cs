// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;

namespace StudyLife.Server.Services.Outbound;

/// <summary>Which resolved addresses a connection to a user-supplied URL may reach.</summary>
public interface IOutboundAddressFilter
{
    bool IsAllowed(IPAddress address);
}

/// <summary>The production filter: public internet addresses only (see <see cref="OutboundAddressClassifier"/>).</summary>
public sealed class PublicAddressFilter : IOutboundAddressFilter
{
    public static readonly PublicAddressFilter Instance = new();

    public bool IsAllowed(IPAddress address) => OutboundAddressClassifier.IsPublic(address);
}

/// <summary>Turns a host name into the addresses a connection may use.</summary>
public interface IHostAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>The system resolver (the pod's /etc/resolv.conf in Kubernetes).</summary>
public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public static readonly DnsHostAddressResolver Instance = new();

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// A connection to a user-supplied URL was refused before a socket was opened, because its host
/// resolved to an address the <see cref="IOutboundAddressFilter"/> does not allow (or to nothing).
/// SocketsHttpHandler surfaces it as the inner exception of an HttpRequestException.
/// </summary>
public sealed class OutboundAddressRejectedException(string host, string reason) : Exception($"Outbound connection to '{host}' refused: {reason}.")
{
    public string Host { get; } = host;
}
