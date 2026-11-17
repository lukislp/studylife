// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Sockets;

namespace StudyLife.Server.Services.Outbound;

/// <summary>
/// Decides whether an IP address is a public internet address that the server may connect to on a
/// user's behalf (2026-11-16 audit M4). Everything that only exists inside a private network, the
/// cluster or the host itself is refused: loopback, RFC1918, carrier-grade NAT (100.64.0.0/10, where
/// Tailscale lives), link-local (including the 169.254.169.254 metadata address), unique-local and
/// site-local IPv6, multicast, unspecified, documentation and benchmark ranges. IPv6 forms that carry
/// an IPv4 address (IPv4-mapped, IPv4-compatible, NAT64 64:ff9b::/96, 6to4 2002::/16) are judged by
/// the embedded IPv4 address, so a private target cannot hide behind an IPv6 spelling. Teredo is
/// refused outright, since its embedded address is the relay's, not the target's.
/// </summary>
public static class OutboundAddressClassifier
{
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address),
            _ => false,
        };
    }

    private static bool IsPublicIPv4(byte[] o) => o[0] switch
    {
        0 => false,                                           // 0.0.0.0/8 "this network", unspecified
        10 => false,                                          // RFC1918
        127 => false,                                         // loopback
        100 when o[1] >= 64 && o[1] <= 127 => false,          // 100.64.0.0/10 carrier-grade NAT
        169 when o[1] == 254 => false,                        // link-local incl. cloud metadata
        172 when o[1] >= 16 && o[1] <= 31 => false,           // RFC1918
        192 when o[1] == 168 => false,                        // RFC1918
        192 when o[1] == 0 && o[2] == 0 => false,             // 192.0.0.0/24 IETF protocol assignments
        192 when o[1] == 0 && o[2] == 2 => false,             // 192.0.2.0/24 TEST-NET-1
        198 when o[1] == 18 || o[1] == 19 => false,           // 198.18.0.0/15 benchmarking
        198 when o[1] == 51 && o[2] == 100 => false,          // 198.51.100.0/24 TEST-NET-2
        203 when o[1] == 0 && o[2] == 113 => false,           // 203.0.113.0/24 TEST-NET-3
        >= 224 => false,                                      // multicast, reserved, broadcast
        _ => true,
    };

    private static bool IsPublicIPv6(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicIPv4(address.MapToIPv4().GetAddressBytes());

        var b = address.GetAddressBytes();
        if (b.Take(15).All(x => x == 0)) return false;                       // :: and ::1
        if (b.Take(12).All(x => x == 0)) return IsPublicIPv4(b[12..16]);     // ::a.b.c.d (IPv4-compatible, deprecated)
        if (b[0] == 0xFF) return false;                                      // ff00::/8 multicast
        if ((b[0] & 0xFE) == 0xFC) return false;                             // fc00::/7 unique local
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return false;             // fe80::/10 link-local
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0) return false;             // fec0::/10 site-local (deprecated)
        if (b[0] == 0x01 && b[1] == 0x00 && b.Skip(2).Take(6).All(x => x == 0)) return false; // 100::/64 discard
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false;      // 2001:db8::/32 documentation
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false;      // 2001::/32 Teredo
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)                   // 64:ff9b::/96 and 64:ff9b:1::/48 NAT64
            return IsPublicIPv4(b[12..16]);
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublicIPv4(b[2..6]);                     // 2002::/16 6to4
        return true;
    }
}
