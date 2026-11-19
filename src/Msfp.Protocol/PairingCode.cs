using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Msfp.Protocol;

/// <summary>
/// A pairing code (core section 5.1): what an operator sends another,
/// however they like, to pair their two servers. It carries the address of
/// this server, its public key, a secret used once, and when the code
/// stops working. As text: "msfp-pair-1." then the rest in base64url.
/// </summary>
/// <param name="Address">Where the server that made it answers.</param>
/// <param name="Key">Its public key, 32 bytes.</param>
/// <param name="Secret">The secret, 32 bytes.</param>
/// <param name="Expires">When it stops working, a whole second.</param>
public sealed record PairingCode(string Address, byte[] Key, byte[] Secret, DateTimeOffset Expires)
{
    /// <summary>The text's prefix: the format and the core version it pairs in.</summary>
    public const string Prefix = "msfp-pair-1.";

    /// <summary>The secret's length.</summary>
    public const int SecretSize = 32;

    /// <summary>How long a pairing code works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    // A code's text after its prefix: 72 bytes and the longest address, in base64url.
    private const int MaxText = (((32 + SecretSize + 8 + Protocol.Address.MaxLength) * 8) + 5) / 6; // 779

    /// <summary>
    /// A new code for this server at address, with a new secret, valid from
    /// now for <see cref="Lifetime"/>.
    /// </summary>
    /// <exception cref="ProtocolException">The address isn't one (<see cref="Protocol.Address.Check"/>).</exception>
    public static PairingCode Create(string address, ReadOnlySpan<byte> publicKey, DateTimeOffset now, RandomNumberGenerator? random = null)
    {
        Protocol.Address.Check(address);
        var secret = new byte[SecretSize];
        if (random is null)
        {
            RandomNumberGenerator.Fill(secret);
        }
        else
        {
            random.GetBytes(secret);
        }

        return new PairingCode(address, publicKey.ToArray(), secret,
            DateTimeOffset.FromUnixTimeSeconds((now + Lifetime).ToUnixTimeSeconds()));
    }

    /// <summary>
    /// The code as text, to show and copy. Its bytes: the public key (32),
    /// the secret (32), when it expires (8, Unix seconds, big-endian), then
    /// the address.
    /// </summary>
    public override string ToString()
    {
        var address = Encoding.UTF8.GetBytes(Address);
        var b = new byte[Key.Length + Secret.Length + 8 + address.Length];
        Key.CopyTo(b, 0);
        Secret.CopyTo(b, Key.Length);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(Key.Length + Secret.Length), (ulong)Expires.ToUnixTimeSeconds());
        address.CopyTo(b, Key.Length + Secret.Length + 8);
        return Prefix + Base64UrlText.Encode(b);
    }

    /// <summary>
    /// Reads a pairing code pasted by an operator. White space around it,
    /// or inside it (from an email), is ignored. Whether it has expired
    /// isn't checked: the server that made it does, and its answer says so.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.Code"/>, or
    /// <see cref="ProtocolError.PlainHttp"/> for a code otherwise good with a plain http address.</exception>
    public static PairingCode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bad = new ProtocolException(ProtocolError.Code);
        var joined = string.Concat(text.Where(c => !IsSpace(c)));
        if (!joined.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw bad;
        }

        var rest = joined[Prefix.Length..];
        if (rest.Length > MaxText)
        {
            throw bad;
        }

        var b = Base64UrlText.Decode(rest);
        const int Fixed = 32 + SecretSize + 8;
        if (b is null || b.Length <= Fixed)
        {
            throw bad;
        }

        var expires = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(Fixed - 8, 8));
        if (expires > (ulong)StrictJson.MaxInteger)
        {
            throw bad;
        }

        string address;
        try
        {
            address = new UTF8Encoding(false, true).GetString(b, Fixed, b.Length - Fixed);
        }
        catch (ArgumentException)
        {
            throw bad;
        }

        var code = new PairingCode(address, b[..32], b[32..(32 + SecretSize)], DateTimeOffset.FromUnixTimeSeconds((long)expires));
        // A key MSFP accepts (section 2.1): never one of small order.
        if (!Ed25519Strict.ValidKey(code.Key))
        {
            throw bad;
        }

        try
        {
            Protocol.Address.Check(code.Address);
        }
        catch (ProtocolException e) when (e.Error == ProtocolError.PlainHttp)
        {
            throw; // a code otherwise good: the panel says why
        }
        catch (ProtocolException)
        {
            throw bad;
        }

        return code;
    }

    // Go's strings.Fields: Unicode white space.
    private static bool IsSpace(char c) => char.IsWhiteSpace(c);

    /// <inheritdoc/>
    public bool Equals(PairingCode? other) =>
        other is not null && Address == other.Address && Key.AsSpan().SequenceEqual(other.Key) &&
        Secret.AsSpan().SequenceEqual(other.Secret) && Expires == other.Expires;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Address, Expires);
}
