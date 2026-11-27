using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Msfp.Protocol;

/// <summary>
/// A pairing code (MSFP core section 5.1): what an operator sends another,
/// however they like, to pair their two servers. It carries the address of
/// this server, its public key, a secret used once, when the code stops
/// working, and for a group's code the group it invites to (groups section
/// 2). As text: "msfp-pair-0." then the rest in base64url. How many servers
/// may use it isn't in the code: its maker counts.
/// </summary>
/// <param name="Address">Where the server that made it answers.</param>
/// <param name="Key">Its public key, 32 bytes.</param>
/// <param name="Secret">The secret, 32 bytes.</param>
/// <param name="Expires">When it stops working, a whole second.</param>
public sealed record PairingCode(string Address, byte[] Key, byte[] Secret, DateTimeOffset Expires)
{
    /// <summary>The text's prefix: the format and the core version it pairs in.</summary>
    public const string Prefix = "msfp-pair-0.";

    /// <summary>The secret's length.</summary>
    public const int SecretSize = 32;

    /// <summary>A group's ID length (groups section 3.1).</summary>
    public const int GroupIdSize = 32;

    /// <summary>A group name's length at most, in code points once cleaned; in bytes, a code carries at most 255 of it.</summary>
    public const int MaxGroupName = 80;

    /// <summary>How long a pairing code works by default.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>How long a pairing code works at most (MSFP core section 5.1).</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);

    // A code's text after its prefix: 73 bytes, a group's 288 at most and
    // the longest address, in base64url (MSFP core section 5.1).
    private const int MaxText = (((32 + SecretSize + 8 + 1 + GroupIdSize + 1 + 255 + Protocol.Address.MaxLength) * 8) + 5) / 6; // 1,164

    /// <summary>The group's ID (32 bytes) for a group's code, null for none.</summary>
    public byte[]? Group { get; init; }

    /// <summary>The group's name, cleaned, for a group's code ("" for none).</summary>
    public string GroupName { get; init; } = "";

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
    /// the secret (32), when it expires (8, Unix seconds, big-endian), a
    /// flag (1: a group follows, 0: none), for a group its ID (32), its
    /// name's length (1) and its name, then the address.
    /// </summary>
    public override string ToString()
    {
        var b = new List<byte>(160);
        b.AddRange(Key);
        b.AddRange(Secret);
        var expires = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(expires, (ulong)Expires.ToUnixTimeSeconds());
        b.AddRange(expires);
        var name = CodeName(GroupName);
        if (Group is { Length: GroupIdSize } && name.Length > 0)
        {
            b.Add(1);
            b.AddRange(Group);
            b.Add((byte)name.Length);
            b.AddRange(name);
        }
        else
        {
            b.Add(0);
        }

        b.AddRange(Encoding.UTF8.GetBytes(Address));
        return Prefix + Base64UrlText.Encode(b.ToArray());
    }

    // A group's name as a code carries it: cleaned, 80 code points at most,
    // and cut further, a whole code point at a time, to fit 255 bytes.
    private static byte[] CodeName(string name)
    {
        var s = Music.CleanText(name ?? "", MaxGroupName);
        var b = Encoding.UTF8.GetBytes(s);
        while (b.Length > 255)
        {
            var runes = s.EnumerateRunes().ToList();
            s = string.Concat(runes.Take(runes.Count - 1).Select(r => r.ToString())).Trim();
            b = Encoding.UTF8.GetBytes(s);
        }

        return b;
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

        var strict = new UTF8Encoding(false, true);
        var at = Fixed + 1;
        byte[]? group = null;
        var groupName = "";
        switch (b[Fixed])
        {
            case 0:
                break;
            case 1:
                // A group: its ID, its name's length, its name (core 5.1).
                if (b.Length < Fixed + 1 + GroupIdSize + 1)
                {
                    throw bad;
                }

                var n = b[Fixed + 1 + GroupIdSize];
                var nameAt = Fixed + 1 + GroupIdSize + 1;
                if (n == 0 || b.Length < nameAt + n)
                {
                    throw bad;
                }

                string raw;
                try
                {
                    raw = strict.GetString(b, nameAt, n);
                }
                catch (ArgumentException)
                {
                    throw bad;
                }

                groupName = Music.CleanText(raw, int.MaxValue);
                if (groupName.EnumerateRunes().Count() > MaxGroupName)
                {
                    throw bad;
                }

                group = b[(Fixed + 1)..(Fixed + 1 + GroupIdSize)];
                at = nameAt + n;
                break;
            default:
                throw bad;
        }

        if (b.Length <= at)
        {
            throw bad;
        }

        string address;
        try
        {
            address = strict.GetString(b, at, b.Length - at);
        }
        catch (ArgumentException)
        {
            throw bad;
        }

        var code = new PairingCode(address, b[..32], b[32..(32 + SecretSize)], DateTimeOffset.FromUnixTimeSeconds((long)expires))
        {
            Group = group,
            GroupName = groupName,
        };
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
        Secret.AsSpan().SequenceEqual(other.Secret) && Expires == other.Expires &&
        (Group ?? []).AsSpan().SequenceEqual(other.Group ?? []) && GroupName == other.GroupName;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Address, Expires);
}
