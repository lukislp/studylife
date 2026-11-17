// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Sockets;

namespace StudyLife.Server.Services.Outbound;

/// <summary>
/// The <see cref="SocketsHttpHandler.ConnectCallback"/> for user-supplied URLs (2026-11-16 audit M4).
/// The host of every new connection is resolved here, ALL its addresses must pass the
/// <see cref="IOutboundAddressFilter"/>, and the socket is then opened to one of exactly those vetted
/// addresses. Checking and connecting with the same resolution closes the DNS-rebinding gap that a
/// separate "validate, then let the handler resolve again" check leaves open. A name that answers
/// with a mix of public and private addresses is refused as a whole: a well-behaved public service
/// never needs a private record, and accepting the public half would only make the outcome depend
/// on address order.
///
/// TLS is unaffected: the handler layers SslStream on the returned stream and still uses the
/// request's host name for SNI and certificate validation, so https keeps verifying the name the
/// user gave, not the IP literal.
/// </summary>
public sealed class VettedConnector(IHostAddressResolver resolver, IOutboundAddressFilter filter)
{
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await ResolveVettedAsync(endpoint.Host, cancellationToken);

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>The addresses a connection to <paramref name="host"/> may use; throws
    /// <see cref="OutboundAddressRejectedException"/> when any of them is not allowed.</summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveVettedAsync(string host, CancellationToken cancellationToken)
    {
        // An IP literal (the handler passes IPv6 literals without brackets) is vetted as-is; asking
        // DNS about it would only add a round trip.
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await resolver.ResolveAsync(host, cancellationToken);

        if (addresses.Length == 0)
            throw new OutboundAddressRejectedException(host, "the name did not resolve");
        if (addresses.FirstOrDefault(a => !filter.IsAllowed(a)) is { } refused)
            throw new OutboundAddressRejectedException(host, $"it resolves to the non-public address {refused}");
        return addresses;
    }
}
