using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>Servers' addresses (core section 5.2) and their changes (core section 13).</summary>
public static partial class Address
{
    /// <summary>The longest address, in bytes.</summary>
    public const int MaxLength = 512;

    /// <summary>How long a server that moved still accepts requests signed for its former host (core section 13.1): 30 days.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromDays(30);

    /// <summary>How often, at most, a peer calls an announced address again after a confirming hello failed (section 13.2).</summary>
    public static readonly TimeSpan MoveRetry = TimeSpan.FromHours(1);

    /// <summary>How long the confirmation of a new address may keep failing before the operator is warned (section 13.2).</summary>
    public static readonly TimeSpan MoveWarn = TimeSpan.FromHours(24);

    private static volatile bool s_plainHttp;

    /// <summary>
    /// Lets addresses be plain http, as MSFP allows an implementation's
    /// tests to (core section 3.1): outside the protocol, never for
    /// servers in use. Off by default; process-wide.
    /// </summary>
    public static void AllowPlainHttpForTests(bool on) => s_plainHttp = on;

    /// <summary>Whether the test switch of <see cref="AllowPlainHttpForTests"/> is on.</summary>
    public static bool PlainHttpAllowed => s_plainHttp;

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\z")]
    private static partial Regex LabelRe();

    [GeneratedRegex("^[1-9][0-9]{0,4}\\z")]
    private static partial Regex PortRe();

    /// <summary>
    /// Accepts a server's address in MSFP's one form (core section 5.2):
    /// "https" in lower case, "://", a host, an optional port, and nothing
    /// after (no path, not even "/", no "?", "#" or "@"); at most 512 bytes.
    /// The host is a name (labels of a-z 0-9 -, 1 to 63 characters, not
    /// starting or ending with -, at most 253 in all), an IPv4 address as
    /// four numbers without leading zeros (a host whose last label is all
    /// digits must be one), or a bracketed IPv6 address as RFC 5952 writes
    /// it. The port is 1 to 65535 without leading zeros, never 443.
    /// Plain http otherwise in that form is <see cref="ProtocolError.PlainHttp"/>,
    /// accepted only with the test switch on, its port then never 80.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.Address"/> or <see cref="ProtocolError.PlainHttp"/>.</exception>
    public static void Check(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bad = new ProtocolException(ProtocolError.Address);
        var sep = address.IndexOf("://", StringComparison.Ordinal);
        if (System.Text.Encoding.UTF8.GetByteCount(address) > MaxLength || sep < 0)
        {
            throw bad;
        }

        var scheme = address[..sep];
        var rest = address[(sep + 3)..];
        if ((scheme != "https" && scheme != "http") || rest.Length == 0 || rest.IndexOfAny(['/', '?', '#', '@']) >= 0)
        {
            throw bad;
        }

        string host, port = "";
        var hasPort = false;
        if (rest.StartsWith('['))
        {
            var end = rest.IndexOf(']', StringComparison.Ordinal);
            if (end < 0)
            {
                throw bad;
            }

            host = rest[1..end];
            var after = rest[(end + 1)..];
            if (after.Length > 0)
            {
                if (!after.StartsWith(':'))
                {
                    throw bad;
                }

                port = after[1..];
                hasPort = true;
            }

            if (!IPAddress.TryParse(host, out var a) || a.AddressFamily != AddressFamily.InterNetworkV6 ||
                host.Contains('%', StringComparison.Ordinal) || a.IsIPv4MappedToIPv6 != host.Contains('.', StringComparison.Ordinal) ||
                Rfc5952(a) != host)
            {
                throw bad;
            }
        }
        else
        {
            var colon = rest.IndexOf(':', StringComparison.Ordinal);
            host = colon < 0 ? rest : rest[..colon];
            if (colon >= 0)
            {
                port = rest[(colon + 1)..];
                hasPort = true;
            }

            var labels = host.Split('.');
            var last = labels[^1];
            if (last.Length > 0 && last.All(c => c is >= '0' and <= '9'))
            {
                if (!Ipv4Canonical(host))
                {
                    throw bad;
                }
            }
            else
            {
                if (host.Length > 253 || !labels.All(l => LabelRe().IsMatch(l)))
                {
                    throw bad;
                }
            }
        }

        if (hasPort)
        {
            if (!PortRe().IsMatch(port) || !int.TryParse(port, out var n) || n > 65535 ||
                (scheme == "https" && n == 443) || (scheme == "http" && n == 80))
            {
                throw bad;
            }
        }

        if (scheme == "http" && !s_plainHttp)
        {
            throw new ProtocolException(ProtocolError.PlainHttp);
        }
    }

    /// <summary>Whether an address is in MSFP's one form (<see cref="Check"/>).</summary>
    public static bool Valid(string address)
    {
        try
        {
            Check(address);
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    // Four decimal numbers 0-255, no leading zeros: Go's netip form.
    private static bool Ipv4Canonical(string host)
    {
        var parts = host.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var p in parts)
        {
            if (p.Length is 0 or > 3 || !p.All(c => c is >= '0' and <= '9') || (p.Length > 1 && p[0] == '0') ||
                int.Parse(p, System.Globalization.CultureInfo.InvariantCulture) > 255)
            {
                return false;
            }
        }

        return true;
    }

    // An IPv6 address as RFC 5952 writes it (as Go's netip does): lower
    // case, no leading zeros, the longest run of two or more zero groups
    // (the first of equal runs) as "::", an IPv4-mapped one as ::ffff:a.b.c.d.
    internal static string Rfc5952(IPAddress a)
    {
        var b = a.GetAddressBytes();
        if (a.IsIPv4MappedToIPv6)
        {
            return "::ffff:" + new IPAddress(b[12..]).ToString();
        }

        var g = new int[8];
        for (var i = 0; i < 8; i++)
        {
            g[i] = (b[2 * i] << 8) | b[(2 * i) + 1];
        }

        int bestStart = -1, bestLen = 0;
        for (var i = 0; i < 8;)
        {
            if (g[i] != 0)
            {
                i++;
                continue;
            }

            var j = i;
            while (j < 8 && g[j] == 0)
            {
                j++;
            }

            if (j - i > bestLen && j - i >= 2)
            {
                bestStart = i;
                bestLen = j - i;
            }

            i = j;
        }

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 8; i++)
        {
            if (i == bestStart)
            {
                sb.Append("::");
                i += bestLen - 1;
                continue;
            }

            if (sb.Length > 0 && sb[^1] != ':')
            {
                sb.Append(':');
            }

            sb.Append(g[i].ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// An address's host, with its port if it has one: what requests for
    /// that server are signed for (core section 6.2). "" for an address that
    /// doesn't read as one.
    /// </summary>
    public static string Host(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var sep = address.IndexOf("://", StringComparison.Ordinal);
        if (sep < 0)
        {
            return "";
        }

        var rest = address[(sep + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>
    /// Reads the address a signed hello, request or answer names for its
    /// sender, against the one stored for it (core section 13.2, step 1):
    /// null when it names none, or the one stored; otherwise the address
    /// to confirm.
    /// </summary>
    /// <exception cref="ProtocolException">One not of section 5.2's form: the stored one stays.</exception>
    public static string? Announced(string? stored, string? address)
    {
        if (string.IsNullOrEmpty(address) || address == stored)
        {
            return null;
        }

        Check(address);
        return address;
    }
}

/// <summary>
/// A peer's announced address, not confirmed yet (core section 13.2):
/// until a hello there is answered under its key, the stored address stays.
/// </summary>
/// <param name="Address">The address announced.</param>
/// <param name="Tried">When a confirming hello was last sent there; null before the first.</param>
/// <param name="Failing">When the confirmation first failed; null while none has.</param>
public sealed record AddressMove(string Address, DateTimeOffset? Tried = null, DateTimeOffset? Failing = null)
{
    /// <summary>Whether the confirming hello may be sent now: the first at once, then at most once every MoveRetry after a failure.</summary>
    public bool Due(DateTimeOffset now) => Tried is null || Failing is null || now - Tried.Value >= Protocol.Address.MoveRetry;

    /// <summary>Whether the operator should be told: the confirmation has been failing for MoveWarn.</summary>
    public bool Warn(DateTimeOffset now) => Failing is { } f && now - f >= Protocol.Address.MoveWarn;
}
