using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace Msfp.Protocol;

/// <summary>
/// HPKE (RFC 9180) as group mail uses it (groups section 5): mode base,
/// DHKEM(X25519, HKDF-SHA256), HKDF-SHA256, AES-128-GCM, one message per
/// context. X25519 is BouncyCastle's (.NET has none); HKDF and AES-GCM
/// are .NET's.
/// </summary>
internal static class Hpke
{
    private const ushort KemId = 0x0020, KdfId = 0x0001, AeadId = 0x0001;
    private const int Nsecret = 32, Nk = 16, Nn = 12, Nt = 16, Npk = 32;

    private static readonly byte[] KemSuite = [.. "KEM"u8, (byte)(KemId >> 8), (byte)KemId];

    private static readonly byte[] HpkeSuite =
        [.. "HPKE"u8, (byte)(KemId >> 8), (byte)KemId, (byte)(KdfId >> 8), (byte)KdfId, (byte)(AeadId >> 8), (byte)AeadId];

    /// <summary>The X25519 public key of a private one.</summary>
    internal static byte[] PublicKey(ReadOnlySpan<byte> priv)
    {
        var pub = new byte[Npk];
        X25519.ScalarMultBase(priv.ToArray(), 0, pub, 0);
        return pub;
    }

    // X25519(k, u); null for an all-zero result (a small-order u).
    private static byte[]? Dh(ReadOnlySpan<byte> k, ReadOnlySpan<byte> u)
    {
        if (k.Length != 32 || u.Length != 32)
        {
            return null;
        }

        var r = new byte[32];
        return X25519.CalculateAgreement(k.ToArray(), 0, u.ToArray(), 0, r, 0) ? r : null;
    }

    private static byte[] LabeledExtract(byte[] suite, ReadOnlySpan<byte> salt, string label, ReadOnlySpan<byte> ikm) =>
        HKDF.Extract(HashAlgorithmName.SHA256, [.. "HPKE-v1"u8, .. suite, .. Encoding.ASCII.GetBytes(label), .. ikm], salt.ToArray());

    private static byte[] LabeledExpand(byte[] suite, byte[] prk, string label, ReadOnlySpan<byte> info, int length)
    {
        var l = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(l, (ushort)length);
        return HKDF.Expand(HashAlgorithmName.SHA256, prk, length, [.. l, .. "HPKE-v1"u8, .. suite, .. Encoding.ASCII.GetBytes(label), .. info]);
    }

    // DHKEM's ExtractAndExpand (RFC 9180 section 4.1).
    private static byte[] SharedSecret(byte[] dh, ReadOnlySpan<byte> enc, ReadOnlySpan<byte> pkR) =>
        LabeledExpand(KemSuite, LabeledExtract(KemSuite, [], "eae_prk", dh), "shared_secret", [.. enc, .. pkR], Nsecret);

    // The key schedule of mode base (RFC 9180 section 5.1): the key and the nonce.
    private static (byte[] Key, byte[] Nonce) Schedule(byte[] shared, ReadOnlySpan<byte> info)
    {
        byte[] context = [0, .. LabeledExtract(HpkeSuite, [], "psk_id_hash", []), .. LabeledExtract(HpkeSuite, [], "info_hash", info)];
        var secret = LabeledExtract(HpkeSuite, shared, "secret", []);
        return (LabeledExpand(HpkeSuite, secret, "key", context, Nk), LabeledExpand(HpkeSuite, secret, "base_nonce", context, Nn));
    }

    /// <summary>Seals pt to pkR with the ephemeral private key given: the encapsulated key (32 bytes), then the ciphertext and its tag. Null for a key that isn't one.</summary>
    internal static byte[]? Seal(ReadOnlySpan<byte> pkR, ReadOnlySpan<byte> info, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> pt, ReadOnlySpan<byte> ephemeral)
    {
        if (pkR.Length != Npk || Dh(ephemeral, pkR) is not { } dh)
        {
            return null;
        }

        var enc = PublicKey(ephemeral);
        var (key, nonce) = Schedule(SharedSecret(dh, enc, pkR), info);
        var output = new byte[Npk + pt.Length + Nt];
        enc.CopyTo(output, 0);
        using var gcm = new AesGcm(key, Nt);
        gcm.Encrypt(nonce, pt, output.AsSpan(Npk, pt.Length), output.AsSpan(Npk + pt.Length, Nt), aad);
        return output;
    }

    /// <summary>Opens what <see cref="Seal"/> made with the recipient's private key; null when it doesn't open.</summary>
    internal static byte[]? Open(ReadOnlySpan<byte> skR, ReadOnlySpan<byte> info, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> sealedBytes)
    {
        if (skR.Length != 32 || sealedBytes.Length < Npk + Nt)
        {
            return null;
        }

        var enc = sealedBytes[..Npk];
        if (Dh(skR, enc) is not { } dh)
        {
            return null;
        }

        var (key, nonce) = Schedule(SharedSecret(dh, enc, PublicKey(skR)), info);
        var ct = sealedBytes[Npk..^Nt];
        var pt = new byte[ct.Length];
        try
        {
            using var gcm = new AesGcm(key, Nt);
            gcm.Decrypt(nonce, ct, sealedBytes[^Nt..], pt, aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }

        return pt;
    }
}
