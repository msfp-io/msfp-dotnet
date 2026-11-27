using System.Net;
using System.Net.Sockets;

namespace Msfp.Protocol;

/// <summary>
/// The addresses a server never connects to for a peer (MSFP
/// core section 3.3): every address it dials comes from a peer, so those that point
/// back into this machine or its cloud are refused.
/// </summary>
public static class Destinations
{
    // Clouds' metadata services outside link-local: Alibaba's (in
    // 100.64.0.0/10), Azure's (a public address), AWS's over IPv6.
    private static readonly IPAddress[] Metadata =
    [
        IPAddress.Parse("100.100.100.200"),
        IPAddress.Parse("168.63.129.16"),
        IPAddress.Parse("fd00:ec2::254"),
    ];

    /// <summary>
    /// Whether <paramref name="ip"/> is never dialed for a peer: loopback
    /// (unless <paramref name="allowLoopback"/>), link-local (a cloud's
    /// metadata service among them), the clouds' metadata services outside
    /// it, unspecified, multicast, and IPv4's "this network", reserved block
    /// and broadcast. An IPv4-mapped IPv6 address is judged as the IPv4 one,
    /// and so is the IPv4 address inside a NAT64 (64:ff9b::/96) or 6to4
    /// (2002::/16) one; local-use NAT64 (64:ff9b:1::/48) is refused whole.
    /// </summary>
    public static bool Refused(IPAddress ip, bool allowLoopback)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (EmbeddedV4(ip) is { } v4 && Refused(v4, allowLoopback))
        {
            return true;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return !allowLoopback;
        }

        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 0 ||                          // this network, unspecified among it
                   (b[0] == 169 && b[1] == 254) ||       // link-local
                   b[0] >= 224 ||                        // multicast, reserved, broadcast
                   Metadata.Contains(ip);
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return true;
        }

        var unzoned = ip.ScopeId == 0; // prefixes hold no address with a zone
        return ip.Equals(IPAddress.IPv6Any) ||
               (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) || // link-local
               b[0] == 0xff ||                             // multicast
               Metadata.Contains(ip) ||
               (unzoned && b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01); // 64:ff9b:1::/48
    }

    // The IPv4 address a NAT64 or 6to4 address reaches.
    private static IPAddress? EmbeddedV4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.ScopeId != 0)
        {
            return null;
        }

        var b = ip.GetAddressBytes();
        ReadOnlySpan<byte> nat64 = [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0];
        if (b.AsSpan(0, 12).SequenceEqual(nat64))
        {
            return new IPAddress(b.AsSpan(12, 4));
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return new IPAddress(b.AsSpan(2, 4));
        }

        return null;
    }
}
