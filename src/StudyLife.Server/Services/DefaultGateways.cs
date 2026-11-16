// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace StudyLife.Server.Services;

/// <summary>
/// The default gateways of this host or container - in a Docker container on a bridge network,
/// the bridge gateway, which is where a reverse proxy on the same machine appears to come from
/// when it reaches a published port. ForwardedHeadersConfig trusts them while no proxy is
/// configured. Read once at startup; a lookup failure just means "none".
/// </summary>
public static class DefaultGateways
{
    public static IReadOnlyList<IPAddress> Discover()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(i => i.GetIPProperties().GatewayAddresses.Select(g => g.Address))
                .Where(IsUsable)
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        catch (PlatformNotSupportedException)
        {
            return [];
        }
    }

    /// <summary>A real gateway address: not the "any" placeholder some platforms report and not an
    /// IPv6 link-local address (those need a scope id and are never a proxy's source here).</summary>
    internal static bool IsUsable(IPAddress address) =>
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)
        && !(address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal);
}
