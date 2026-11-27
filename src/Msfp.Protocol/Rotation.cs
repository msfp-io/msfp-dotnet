using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// A rotation statement (MSFP core section 12.2): a server moving from key Old
/// to key New, committing to the key after New.
/// </summary>
/// <param name="Old">The key it moves from.</param>
/// <param name="New">The key it moves to.</param>
/// <param name="Next">The commitment to the key after New; null for none (the statement then says "-").</param>
/// <param name="Date">When it was made, Unix seconds.</param>
/// <param name="OldSignature">The statement's signature with Old.</param>
/// <param name="NewSignature">Its signature with New: proof the server holds New.</param>
public sealed record Rotation(byte[] Old, byte[] New, byte[]? Next, long Date, byte[] OldSignature, byte[] NewSignature)
{
    /// <summary>
    /// The statement as rotate sends it (MSFP core section 9.3), and as a hello
    /// answer's rotation member carries it (section 12.4): newKey,
    /// nextCommitment (when there is one), date, oldSignature,
    /// newSignature. The old key isn't in it: the request is signed with it.
    /// </summary>
    public void WriteBody(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.Bytes("newKey", New).Bytes("nextCommitment", Next).Integer("date", Date)
            .Bytes("oldSignature", OldSignature).Bytes("newSignature", NewSignature);
    }

    /// <summary>The body alone, as bytes.</summary>
    public byte[] Body() => JsonOut.Object(WriteBody);
}

/// <summary>A revocation statement (MSFP core section 12.6): a server saying its key Key is compromised.</summary>
/// <param name="Key">The key revoked.</param>
/// <param name="Date">When, Unix seconds.</param>
/// <param name="Signature">Its signature with Key.</param>
public sealed record Revocation(byte[] Key, long Date, byte[] Signature)
{
    /// <summary>The statement as revoke sends it (MSFP core section 9.4): date, signature.</summary>
    public void WriteBody(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.Integer("date", Date).Bytes("signature", Signature);
    }

    /// <summary>The body alone, as bytes.</summary>
    public byte[] Body() => JsonOut.Object(WriteBody);
}

/// <summary>
/// Key rotation and revocation (MSFP core section 12): the statements, their
/// signatures and the bodies that carry them. What a server keeps, and
/// when it sends what, is the engine's.
/// </summary>
public static class Rotations
{
    /// <summary>rotate's answer: the new key is the sender's current key here (200).</summary>
    public const string StateRotated = "rotated";

    /// <summary>rotate's answer: the receiver's operator must confirm the new key (202).</summary>
    public const string StatePending = "pending";

    /// <summary>revoke's answer: the key is revoked here (200).</summary>
    public const string StateSuspended = "suspended";

    /// <summary>The text of a next key for offline keeping (MSFP core section 12.5; decision 12): this prefix, then the seed, base64url.</summary>
    public const string NextKeyPrefix = "msfp-next-key-1.";

    private const string RotationLabel = "msfp-key-rotation-1";
    private const string RevocationLabel = "msfp-key-revocation-1";

    private static readonly string[] RotateMembers = ["newKey", "nextCommitment", "date", "oldSignature", "newSignature"];
    private static readonly string[] RevokeMembers = ["date", "signature"];

    /// <summary>What both keys sign: five lines, no final line feed.</summary>
    public static byte[] RotationStatement(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new, byte[]? next, long date) =>
        Encoding.UTF8.GetBytes(RotationLabel + "\n" + Base64UrlText.Encode(old) + "\n" + Base64UrlText.Encode(@new) + "\n" +
                               (next is null ? "-" : Base64UrlText.Encode(next)) + "\n" +
                               date.ToString(CultureInfo.InvariantCulture));

    /// <summary>What a revocation's key signs: three lines.</summary>
    public static byte[] RevocationStatement(ReadOnlySpan<byte> key, long date) =>
        Encoding.UTF8.GetBytes(RevocationLabel + "\n" + Base64UrlText.Encode(key) + "\n" + date.ToString(CultureInfo.InvariantCulture));

    /// <summary>The statement of a server moving from old to new, committing to next (a commitment, or null), dated at.</summary>
    public static Rotation SignRotation(MsfpKey old, MsfpKey @new, byte[]? next, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(@new);
        var date = at.ToUnixTimeSeconds();
        var msg = RotationStatement(old.PublicSpan, @new.PublicSpan, next, date);
        return new Rotation(old.Public(), @new.Public(), next, date, old.Sign(msg), @new.Sign(msg));
    }

    /// <summary>The statement revoking key, dated at.</summary>
    public static Revocation SignRevocation(MsfpKey key, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(key);
        var date = at.ToUnixTimeSeconds();
        return new Revocation(key.Public(), date, key.Sign(RevocationStatement(key.PublicSpan, date)));
    }

    /// <summary>
    /// Reads a rotate body sent signed with old, at now, and checks it as
    /// MSFP core section 12.3, step 1 says: the statement rebuilt with old, both
    /// signatures (the new key valid under section 2.1, and not old), the
    /// date within MaxSkew. Whether the new key may be accepted is the
    /// receiver's to decide.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.Statement"/>.</exception>
    public static Rotation ReadRotate(ReadOnlySpan<byte> body, ReadOnlySpan<byte> old, DateTimeOffset now)
    {
        try
        {
            var m = StrictJson.ReadRequest(body, RotateMembers);
            var newKey = m.GetBytes("newKey", Ed25519Strict.KeySize);
            var next = m.GetBytes("nextCommitment", 32);
            var date = ReadDate(m, now);
            var oldSig = m.GetBytes("oldSignature", Ed25519Strict.SignatureSize);
            var newSig = m.GetBytes("newSignature", Ed25519Strict.SignatureSize);
            if (newKey is null || date is null || oldSig is null || newSig is null ||
                !Ed25519Strict.ValidKey(newKey) || Keys.Same(newKey, old))
            {
                throw Bad();
            }

            var msg = RotationStatement(old, newKey, next, date.Value);
            if (!Ed25519Strict.Verify(old, msg, oldSig) || !Ed25519Strict.Verify(newKey, msg, newSig))
            {
                throw Bad();
            }

            return new Rotation(old.ToArray(), newKey, next, date.Value, oldSig, newSig);
        }
        catch (ProtocolException e) when (e.Error != ProtocolError.Statement)
        {
            throw Bad();
        }
    }

    /// <summary>
    /// Reads a revoke body sent signed with key, at now, and checks its
    /// signature and its date (within MaxSkew).
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.Statement"/>.</exception>
    public static Revocation ReadRevoke(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key, DateTimeOffset now)
    {
        try
        {
            var m = StrictJson.ReadRequest(body, RevokeMembers);
            var date = ReadDate(m, now);
            var sig = m.GetBytes("signature", Ed25519Strict.SignatureSize);
            if (date is null || sig is null || !Ed25519Strict.Verify(key, RevocationStatement(key, date.Value), sig))
            {
                throw Bad();
            }

            return new Revocation(key.ToArray(), date.Value, sig);
        }
        catch (ProtocolException e) when (e.Error != ProtocolError.Statement)
        {
            throw Bad();
        }
    }

    /// <summary>The text of a next key's seed, to keep offline (MSFP core section 12.5): "msfp-next-key-1." and the seed in base64url.</summary>
    public static string ExportNextKey(MsfpKey next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return NextKeyPrefix + Base64UrlText.Encode(next.Seed());
    }

    /// <summary>Reads the text <see cref="ExportNextKey"/> writes; white space around it is ignored.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.Code"/> for any other text.</exception>
    public static MsfpKey ParseNextKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.Trim();
        if (!t.StartsWith(NextKeyPrefix, StringComparison.Ordinal) ||
            Base64UrlText.Decode(t[NextKeyPrefix.Length..], 32) is not { } seed)
        {
            throw new ProtocolException(ProtocolError.Code, "federation: not a next key's text");
        }

        return MsfpKey.FromSeed(seed);
    }

    // A statement's date: an integer, Unix seconds, within MaxSkew of now.
    private static long? ReadDate(JsonObject m, DateTimeOffset now)
    {
        if (m.GetInteger("date") is not { } date)
        {
            return null;
        }

        return UnixTime.Within(now, date, Signing.MaxSkew) ? date : null;
    }

    private static ProtocolException Bad() => new(ProtocolError.Statement);
}
