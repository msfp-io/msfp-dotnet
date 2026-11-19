using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Msfp.Protocol;

/// <summary>
/// A server's own key pair (core section 4): pure Ed25519 of RFC 8032,
/// made from a 32-byte seed. Ed25519 keys are small, and signing is fast.
/// </summary>
public sealed class MsfpKey
{
    private readonly byte[] _seed;
    private readonly byte[] _public;

    private MsfpKey(byte[] seed)
    {
        _seed = seed;
        _public = new byte[Ed25519Strict.KeySize];
        Ed25519.GeneratePublicKey(_seed, 0, _public, 0);
    }

    /// <summary>A new random key pair.</summary>
    public static MsfpKey Generate() => new(RandomNumberGenerator.GetBytes(Ed25519Strict.KeySize));

    /// <summary>A new key pair from the given cryptographic source.</summary>
    public static MsfpKey Generate(RandomNumberGenerator random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var seed = new byte[Ed25519Strict.KeySize];
        random.GetBytes(seed);
        return new MsfpKey(seed);
    }

    /// <summary>Rebuilds a key pair from its 32-byte seed, as stored.</summary>
    /// <exception cref="ArgumentException">The seed isn't 32 bytes.</exception>
    public static MsfpKey FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != Ed25519Strict.KeySize)
        {
            throw new ArgumentException("federation: a key's seed is 32 bytes", nameof(seed));
        }

        return new MsfpKey(seed.ToArray());
    }

    /// <summary>A copy of the seed: what to store, sealed, to rebuild the key pair later.</summary>
    public byte[] Seed() => (byte[])_seed.Clone();

    /// <summary>A copy of the public key, the key others know this server by.</summary>
    public byte[] Public() => (byte[])_public.Clone();

    /// <summary>The public key without a copy.</summary>
    public ReadOnlySpan<byte> PublicSpan => _public;

    /// <summary>The key's fingerprint (core section 4.2).</summary>
    public string Fingerprint => Keys.Fingerprint(_public);

    /// <summary>The 64-byte signature of a message.</summary>
    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var sig = new byte[Ed25519Strict.SignatureSize];
        var m = message.ToArray();
        Ed25519.Sign(_seed, 0, m, 0, m.Length, sig, 0);
        return sig;
    }
}

/// <summary>Public keys: fingerprints, commitments, comparisons.</summary>
public static class Keys
{
    // How much of the key's SHA-256 the fingerprint shows: 15 bytes, 120
    // bits, 24 characters.
    private const int FingerprintBytes = 15;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// A key in a form people can compare, read aloud or over the phone
    /// (core section 4.2): 24 letters and digits in groups of four,
    /// "ABCD-EFGH-…".
    /// </summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicKey) => CommitmentFingerprint(Commitment(publicKey));

    /// <summary>
    /// The next key commitment to a key (core section 12.5): the SHA-256
    /// of its 32 bytes, sent base64url.
    /// </summary>
    public static byte[] Commitment(ReadOnlySpan<byte> publicKey) => SHA256.HashData(publicKey);

    /// <summary>
    /// The fingerprint of the key a commitment is to: both are made of the
    /// key's SHA-256, so the next key's fingerprint is known from its
    /// commitment alone. Empty for a commitment too short.
    /// </summary>
    public static string CommitmentFingerprint(ReadOnlySpan<byte> commitment)
    {
        if (commitment.Length < FingerprintBytes)
        {
            return "";
        }

        var text = Base32(commitment[..FingerprintBytes]);
        var b = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i += 4)
        {
            if (i > 0)
            {
                b.Append('-');
            }

            b.Append(text, i, 4);
        }

        return b.ToString();
    }

    /// <summary>Whether two public keys are the same.</summary>
    public static bool Same(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceEqual(b);

    // RFC 4648 base32, upper case; 15 bytes make exactly 24 characters.
    internal static string Base32(ReadOnlySpan<byte> bytes, string alphabet = Base32Alphabet)
    {
        var sb = new System.Text.StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var x in bytes)
        {
            buffer = (buffer << 8) | x;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(alphabet[(buffer >> bits) & 31]);
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }
}
