using System.Security.Cryptography;

namespace Msfp.Protocol;

/// <summary>
/// Servers that can't be called (MSFP core section 7.5): one that can connect
/// out but can't be connected to says so with inbound false in its pair
/// and hello messages. Its peers then never connect to it, and it asks
/// them, on a schedule, for what it would otherwise be told. Its address
/// never resolves (MSFP core section 5.2): https://msfp-&lt;26 characters&gt;.invalid,
/// made once and kept.
/// </summary>
public static class Uncallables
{
    private const string Prefix = "https://msfp-";
    private const string Suffix = ".invalid";
    private const int AddressBytes = 16;
    private const string LowerBase32 = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>A hello every 20 seconds while the other operator hasn't accepted...</summary>
    public static readonly TimeSpan HelloWaiting = TimeSpan.FromSeconds(20);

    /// <summary>...for a day after the pair, then every <see cref="HelloPaired"/>.</summary>
    public static readonly TimeSpan HelloWaitingFor = TimeSpan.FromHours(24);

    /// <summary>A hello every 5 minutes once paired.</summary>
    public static readonly TimeSpan HelloPaired = TimeSpan.FromMinutes(5);

    /// <summary>A hello every hour once the peer says it doesn't know this server.</summary>
    public static readonly TimeSpan HelloGone = TimeSpan.FromHours(1);

    /// <summary>A new address of a server that can't be called, at random. A server makes it once, and keeps it.</summary>
    public static string NewAddress() => NewAddress(RandomNumberGenerator.Create());

    /// <summary>A new address of a server that can't be called, from this randomness.</summary>
    public static string NewAddress(RandomNumberGenerator random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var b = new byte[AddressBytes];
        random.GetBytes(b);
        return Prefix + Keys.Base32(b, LowerBase32) + Suffix;
    }

    /// <summary>
    /// Accepts the address of a server that can't be called in its one
    /// form (MSFP core section 5.2): https://msfp-, 26 characters of a-z 2-7
    /// that read back as exactly 16 bytes (the 2 unused bits of the last
    /// zero), .invalid, nothing else.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.UncallableAddress"/>.</exception>
    public static void Check(string? address)
    {
        if (!Valid(address))
        {
            throw new ProtocolException(ProtocolError.UncallableAddress);
        }
    }

    /// <summary>Whether address is one of a server that can't be called.</summary>
    public static bool Valid(string? address)
    {
        if (address is null || !address.StartsWith(Prefix, StringComparison.Ordinal) ||
            !address.EndsWith(Suffix, StringComparison.Ordinal) || address.Length != Prefix.Length + 26 + Suffix.Length)
        {
            return false;
        }

        var text = address.Substring(Prefix.Length, 26);
        // 26 characters are 130 bits: 16 bytes and 2 bits that must be zero.
        int buffer = 0, bits = 0, n = 0;
        foreach (var c in text)
        {
            var v = LowerBase32.IndexOf(c, StringComparison.Ordinal);
            if (v < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                n++;
                buffer &= (1 << bits) - 1;
            }
        }

        return n == AddressBytes && buffer == 0;
    }

    /// <summary>
    /// Checks a message's inbound against the address it is read with
    /// (MSFP core sections 5.2 and 9.2): a sender saying it can't be called must
    /// have an address of that form. address is the message's own, or, in a
    /// hello without one, the one stored for its sender. A request that
    /// fails this is a bad body; an answer, a badly signed one.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.UncallableAddress"/>.</exception>
    public static void CheckInbound(bool uncallable, string? address)
    {
        if (uncallable)
        {
            Check(address);
        }
    }

    /// <summary>
    /// How long a server that can't be called waits before its next
    /// scheduled hello to a peer, before <see cref="Vary"/>: waiting says
    /// the peer's operator hasn't accepted yet, gone that it said it
    /// doesn't know us, sincePair how long ago the pair was.
    /// </summary>
    public static TimeSpan HelloWait(bool waiting, bool gone, TimeSpan sincePair)
    {
        if (gone)
        {
            return HelloGone;
        }

        return waiting && sincePair < HelloWaitingFor ? HelloWaiting : HelloPaired;
    }

    /// <summary>d give or take a fifth, by r in [0, 1): peers then don't call together.</summary>
    public static TimeSpan Vary(TimeSpan d, double r) => d - (d / 5) + TimeSpan.FromTicks((long)(r * (2 * d / 5).Ticks));

    /// <summary><see cref="HelloWait"/>, varied at random.</summary>
    public static TimeSpan NextHello(bool waiting, bool gone, TimeSpan sincePair) =>
        Vary(HelloWait(waiting, gone, sincePair), Random.Shared.NextDouble());
}

/// <summary>A signed refusal's retryAfter (MSFP core section 8.3).</summary>
public static class RetryAfter
{
    /// <summary>The longest a signed refusal may ask a caller to wait, in seconds: an hour.</summary>
    public const int Max = 3600;

    /// <summary>A signed refusal's retryAfter, 0 when it has none in range (1 to 3,600 seconds).</summary>
    public static int Read(ReadOnlySpan<byte> body)
    {
        try
        {
            var n = StrictJson.ReadAnswer(body).GetInteger("retryAfter");
            return n is >= 1 and <= Max ? (int)n.Value : 0;
        }
        catch (ProtocolException)
        {
            return 0;
        }
    }
}
