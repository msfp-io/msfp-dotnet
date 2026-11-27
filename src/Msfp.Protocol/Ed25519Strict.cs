using System.Collections.Concurrent;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Msfp.Protocol;

/// <summary>
/// Ed25519 as MSFP's MSFP core section 2.1 wants it, stricter than RFC 8032
/// alone: a public key, and a signature's R, must be the one encoding of a
/// point of the prime-order subgroup, never of small order (the identity
/// among them), and S must be below L. Under the identity as a key, the
/// signature (R = identity, S = 0) would verify every message. Honest keys
/// and signatures are [x]B for some x, and always pass.
/// </summary>
/// <remarks>
/// .NET has no Ed25519 of its own: BouncyCastle's managed one does the
/// arithmetic, and its full point check (canonical y, on the curve, not of
/// small order, [L]P the identity) is the one applied to keys and to R.
/// </remarks>
public static class Ed25519Strict
{
    /// <summary>A public key's length, and a seed's.</summary>
    public const int KeySize = 32;

    /// <summary>A signature's length.</summary>
    public const int SignatureSize = 64;

    // L, the order of the prime-order subgroup, little-endian.
    private static readonly byte[] L =
    [
        0xed, 0xd3, 0xf5, 0x5c, 0x1a, 0x63, 0x12, 0x58,
        0xd6, 0x9c, 0xf7, 0xa2, 0xde, 0xf9, 0xde, 0x14,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0x10,
    ];

    // Keys already checked, valid or not: a key is checked once (section
    // 2.1 allows keeping the result), and a peer's key comes with every
    // request. Cleared when full, so strangers sending keys of their own
    // can't make it grow.
    private static readonly ConcurrentDictionary<string, bool> KeyChecks = new(StringComparer.Ordinal);
    private const int MaxKeyChecks = 4096;

    /// <summary>
    /// Whether b is the canonical encoding of a point of the prime-order
    /// subgroup that isn't of small order: what a public key and a
    /// signature's R must be.
    /// </summary>
    internal static bool PrimeOrderPoint(ReadOnlySpan<byte> b)
    {
        if (b.Length != KeySize)
        {
            return false;
        }

        try
        {
            return Ed25519.ValidatePublicKeyFull(b.ToArray(), 0);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a public key is one MSFP accepts (MSFP core section 2.1): 32
    /// bytes, the canonical encoding of a point of the prime-order
    /// subgroup, not of small order.
    /// </summary>
    public static bool ValidKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != KeySize)
        {
            return false;
        }

        var text = Convert.ToBase64String(publicKey);
        if (KeyChecks.TryGetValue(text, out var ok))
        {
            return ok;
        }

        ok = PrimeOrderPoint(publicKey);
        if (KeyChecks.Count >= MaxKeyChecks)
        {
            KeyChecks.Clear();
        }

        KeyChecks[text] = ok;
        return ok;
    }

    /// <summary>
    /// Checks a signature as MSFP core section 2.1 says: a valid key, R a point
    /// of the prime-order subgroup, S below L, then RFC 8032's equation
    /// (with A and R in that subgroup, the cofactored and cofactorless
    /// checks accept the same signatures).
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureSize || !ValidKey(publicKey) || !PrimeOrderPoint(signature[..32]) ||
            !ScalarBelowL(signature[32..]))
        {
            return false;
        }

        try
        {
            var m = message.ToArray();
            return Ed25519.Verify(signature.ToArray(), 0, publicKey.ToArray(), 0, m, 0, m.Length);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // S, little-endian, below L.
    private static bool ScalarBelowL(ReadOnlySpan<byte> s)
    {
        for (var i = 31; i >= 0; i--)
        {
            if (s[i] != L[i])
            {
                return s[i] < L[i];
            }
        }

        return false; // S == L
    }
}
