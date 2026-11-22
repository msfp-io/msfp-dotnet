using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Msfp.Protocol;

// Groups (MSFP groups part): events, the state they give, mail and the
// join proof. Nothing here stores or calls anything: the engine does.

/// <summary>
/// An event or a mail as it travels (groups sections 3.1 and 5): the
/// payload's bytes in base64url, and the signature. An event has
/// <see cref="Event"/>, a mail <see cref="Mail"/>.
/// </summary>
public sealed record GroupEnvelope
{
    /// <summary>An event's payload, base64url ("" for a mail).</summary>
    public string Event { get; init; } = "";

    /// <summary>A mail's payload, base64url ("" for an event).</summary>
    public string Mail { get; init; } = "";

    /// <summary>The signature, base64url.</summary>
    public string Signature { get; init; } = "";

    /// <summary>Writes the envelope's members: event or mail, then signature.</summary>
    public void WriteTo(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.String("event", Event.Length > 0 ? Event : null).String("mail", Mail.Length > 0 ? Mail : null).String("signature", Signature);
    }

    /// <summary>Reads an envelope from a body's entry; null when it isn't one (an object of strings).</summary>
    public static GroupEnvelope? Read(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return null;
        }

        try
        {
            return new GroupEnvelope { Event = o.GetString("event") ?? "", Mail = o.GetString("mail") ?? "", Signature = o.GetString("signature") ?? "" };
        }
        catch (ProtocolException)
        {
            return null;
        }
    }
}

/// <summary>A group's settings (groups section 3.3).</summary>
/// <param name="WaitDays">Days a newcomer waits before members that accept by themselves accept it, 0 to 30.</param>
/// <param name="CapPerDay">Invitations a day per inviter, 1 to 100.</param>
public sealed record GroupSettings(long WaitDays, long CapPerDay)
{
    /// <summary>The settings by default: 3 days before automatic acceptance, 5 invitations a day per inviter.</summary>
    public static GroupSettings Default { get; } = new(3, 5);

    /// <summary>Whether both are in their ranges.</summary>
    public bool Valid => WaitDays is >= 0 and <= 30 && CapPerDay is >= 1 and <= 100;
}

/// <summary>An event's payload (groups sections 3.1 and 3.3). Members a type doesn't carry stay null.</summary>
public sealed record GroupEvent
{
    /// <summary>The group's ID; null in founded.</summary>
    public string? Group { get; init; }

    /// <summary>One of <see cref="GroupEvents"/>' types.</summary>
    public string Type { get; init; } = "";

    /// <summary>The key that signed, base64url.</summary>
    public string Signer { get; init; } = "";

    /// <summary>The signer's member ID.</summary>
    public string Member { get; init; } = "";

    /// <summary>The signer's count of its events in this group, from 1.</summary>
    public long Seq { get; init; }

    /// <summary>When it was signed, Unix seconds.</summary>
    public long Date { get; init; }

    /// <summary>An owner event's ownerSeq.</summary>
    public long? OwnerSeq { get; init; }

    /// <summary>An admin event's asOf.</summary>
    public long? AsOf { get; init; }

    /// <summary>A name: the group's, the member's or the newcomer's.</summary>
    public string? Name { get; init; }

    /// <summary>founded's and settings' settings.</summary>
    public GroupSettings? Settings { get; init; }

    /// <summary>founded's nonce, 16 random bytes, base64url.</summary>
    public string? Nonce { get; init; }

    /// <summary>profile's X25519 public key, base64url.</summary>
    public string? EncryptionKey { get; init; }

    /// <summary>profile's next key commitment, base64url.</summary>
    public string? NextCommitment { get; init; }

    /// <summary>joined's newcomer, its key.</summary>
    public string? NewMember { get; init; }

    /// <summary>The member an owner or admin event acts on.</summary>
    public string? Target { get; init; }

    /// <summary>admin's on.</summary>
    public bool? On { get; init; }

    /// <summary>The last of the target's (or, for revoked, the signer's) events that counts.</summary>
    public long? LastSeq { get; init; }

    /// <summary>A ban's reason.</summary>
    public string? Reason { get; init; }

    /// <summary>unbanned's ban: the banned event's ID.</summary>
    public string? Ban { get; init; }

    /// <summary>dissolved's keepPairings.</summary>
    public bool? KeepPairings { get; init; }

    /// <summary>rotated's and revoked's statement, an object.</summary>
    public JsonObject? Statement { get; init; }

    // The members this version reads; any other is ignored, whatever it holds.
    private static readonly string[] Known =
    [
        "group", "type", "signer", "member", "seq", "date", "ownerSeq", "asOf", "name", "settings", "nonce", "encryptionKey",
        "nextCommitment", "newMember", "target", "on", "lastSeq", "reason", "ban", "keepPairings", "statement",
    ];

    /// <summary>The payload's bytes: compact JSON, the members in the order of groups section 3.1's and 3.3's tables.</summary>
    public byte[] Payload() => JsonOut.Object(o =>
    {
        o.String("group", Group).String("type", Type).String("signer", Signer).String("member", Member)
            .Integer("seq", Seq).Integer("date", Date).Integer("ownerSeq", OwnerSeq).Integer("asOf", AsOf)
            .String("name", Name);
        if (Settings is { } s)
        {
            o.Object("settings", w => w.Integer("waitDays", s.WaitDays).Integer("capPerDay", s.CapPerDay));
        }

        o.String("nonce", Nonce).String("encryptionKey", EncryptionKey).String("nextCommitment", NextCommitment)
            .String("newMember", NewMember).String("target", Target).Bool("on", On).Integer("lastSeq", LastSeq)
            .String("reason", Reason).String("ban", Ban).Bool("keepPairings", KeepPairings).Node("statement", Statement);
    });

    /// <summary>
    /// Reads a payload: I-JSON, an object, each member this version knows
    /// of its type and never null; unknown members ignored (core section
    /// 2.3). Absent settings members are their defaults (groups section 3.3).
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.Json"/>.</exception>
    public static GroupEvent Read(ReadOnlySpan<byte> payload)
    {
        var o = StrictJson.ParseObject(payload, nulls: true);
        foreach (var name in Known)
        {
            if (o.TryGetPropertyValue(name, out var v) && (v is null || StrictJson.HoldsNull(v)))
            {
                throw StrictJson.Bad();
            }
        }

        GroupSettings? settings = null;
        if (o.GetObject("settings") is { } s)
        {
            settings = new GroupSettings(s.GetInteger("waitDays") ?? GroupSettings.Default.WaitDays,
                s.GetInteger("capPerDay") ?? GroupSettings.Default.CapPerDay);
        }

        return new GroupEvent
        {
            Group = o.GetString("group"), Type = o.GetString("type") ?? "", Signer = o.GetString("signer") ?? "",
            Member = o.GetString("member") ?? "", Seq = o.GetInteger("seq") ?? 0, Date = o.GetInteger("date") ?? 0,
            OwnerSeq = o.GetInteger("ownerSeq"), AsOf = o.GetInteger("asOf"), Name = o.GetString("name"), Settings = settings,
            Nonce = o.GetString("nonce"), EncryptionKey = o.GetString("encryptionKey"), NextCommitment = o.GetString("nextCommitment"),
            NewMember = o.GetString("newMember"), Target = o.GetString("target"), On = o.GetBool("on"), LastSeq = o.GetInteger("lastSeq"),
            Reason = o.GetString("reason"), Ban = o.GetString("ban"), KeepPairings = o.GetBool("keepPairings"),
            Statement = o.GetObject("statement"),
        };
    }
}

/// <summary>A mail's payload (groups section 5).</summary>
public sealed record GroupMail
{
    /// <summary>The group's ID.</summary>
    public string Group { get; init; } = "";

    /// <summary>16 random bytes, base64url.</summary>
    public string Id { get; init; } = "";

    /// <summary>request or answer.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The sender's member ID.</summary>
    public string From { get; init; } = "";

    /// <summary>The recipient's member ID.</summary>
    public string To { get; init; } = "";

    /// <summary>For an answer, the ID of the request it answers.</summary>
    public string? Answers { get; init; }

    /// <summary>When it expires, Unix seconds.</summary>
    public long Expires { get; init; }

    /// <summary>The HPKE encapsulated key and the ciphertext, base64url.</summary>
    public string Sealed { get; init; } = "";

    /// <summary>The payload's bytes, members in the order of groups section 5's table.</summary>
    public byte[] Payload() => JsonOut.Object(o => o.String("group", Group).String("id", Id).String("kind", Kind)
        .String("from", From).String("to", To).String("answers", Answers).Integer("expires", Expires).String("sealed", Sealed));
}

/// <summary>What a mail carries, once opened (groups section 5): no name, a member's being its latest profile's, or its joined's until its profile arrives.</summary>
public sealed record SealedContent
{
    /// <summary>The sender's address (core section 5.2).</summary>
    public string Address { get; init; } = "";

    /// <summary>Its next key commitment, base64url.</summary>
    public string NextCommitment { get; init; } = "";

    /// <summary>Whether it can be called (core section 7.5).</summary>
    public bool Inbound { get; init; }

    /// <summary>The pins of the certificates its address serves (core section 3.2), for group-pair to check; null for none.</summary>
    public IReadOnlyList<string>? Certificates { get; init; }

    /// <summary>The JSON sealed.</summary>
    public byte[] Bytes() => JsonOut.Object(o => o.String("address", Address)
        .String("nextCommitment", NextCommitment).Bool("inbound", Inbound).Strings("certificates", Certificates));

    /// <inheritdoc/>
    public bool Equals(SealedContent? other) =>
        other is not null && Address == other.Address && NextCommitment == other.NextCommitment &&
        Inbound == other.Inbound && (Certificates ?? []).SequenceEqual(other.Certificates ?? []);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Address, NextCommitment, Inbound);
}

/// <summary>A member as a group's state knows it (groups section 3.5).</summary>
public sealed record GroupMember
{
    /// <summary>Its member ID: the key it joined with, base64url.</summary>
    public string Id { get; init; } = "";

    /// <summary>Its current key.</summary>
    public string Key { get; set; } = "";

    /// <summary>Its name, from its latest profile (or its joined event).</summary>
    public string Name { get; set; } = "";

    /// <summary>Whether it is an admin now.</summary>
    public bool Admin { get; set; }

    /// <summary>Who invited it (its joined event's member); "" for the founder.</summary>
    public string InvitedBy { get; init; } = "";

    /// <summary>Its joined event's date (the founder: founded's), Unix seconds.</summary>
    public long Joined { get; init; }

    /// <summary>Its encryption key, from its latest profile; null before one.</summary>
    public byte[]? EncryptionKey { get; set; }

    /// <summary>Its next key commitment, from its latest profile and rotations; null for none.</summary>
    public byte[]? NextCommitment { get; set; }

    // The ownerSeq that named it admin, for "oldest admin"; its joined
    // event's ID (founded's for the founder), for "oldest member".
    internal long AdminAt { get; set; }

    internal string JoinedId { get; init; } = "";
}

/// <summary>
/// What a group's state makes of an event a member keeps (groups sections
/// 3.2 and 3.5), given by <see cref="GroupState.Compute"/>.
/// </summary>
public enum GroupVerdict
{
    /// <summary>It counts in the state.</summary>
    Counted,

    /// <summary>
    /// Its rules don't hold. It is kept and passed on all the same,
    /// changing nothing: with events that arrive later, it may count.
    /// </summary>
    Refused,

    /// <summary>
    /// What it needs isn't there yet: its member's earlier events, the
    /// owner event it names, its member's joined, a ban's target's events
    /// up to its lastSeq. Kept, and dropped after <see cref="GroupEvents.HeldFor"/>.
    /// </summary>
    Pending,

    /// <summary>Not signed by its member's key in force at its seq (section 3.2). Not kept.</summary>
    Forged,
}

/// <summary>One event's verdict in a group's state.</summary>
/// <param name="Event">The event read.</param>
/// <param name="Verdict">What the state makes of it.</param>
/// <param name="Reason">Why it doesn't count, for logs ("" when it counts).</param>
public sealed record GroupEventResult(GroupEvent Event, GroupVerdict Verdict, string Reason);

/// <summary>What <see cref="GroupEvents.Check"/> made of an envelope: whether a member keeps it (groups section 3.2).</summary>
/// <param name="Event">The event read; null when its envelope, payload or signature isn't valid.</param>
/// <param name="Id">Its ID ("" when it didn't read).</param>
/// <param name="Reason">Why it isn't kept ("" when it is).</param>
public sealed record GroupCheck(GroupEvent? Event, string Id, string Reason)
{
    /// <summary>Whether the event passes section 3.2's checks: a member keeps it and passes it on.</summary>
    public bool Kept => Reason.Length == 0;
}

/// <summary>Events, mail and the join proof of the groups part.</summary>
public static class GroupEvents
{
    /// <summary>The event types (groups section 3.3).</summary>
    public const string Founded = "founded", Profile = "profile", Joined = "joined", Admin = "admin", Heir = "heir", Owner = "owner",
        Settings = "settings", Banned = "banned", Unbanned = "unbanned", Left = "left", Dissolved = "dissolved", Rotated = "rotated",
        Revoked = "revoked";

    /// <summary>An event's payload at most, in bytes (groups section 3.1).</summary>
    public const int MaxEvent = 4096;

    /// <summary>A mail's payload at most, in bytes (groups section 5).</summary>
    public const int MaxMail = 2048;

    /// <summary>A ban's reason at most, in code points.</summary>
    public const int MaxReason = 280;

    /// <summary>How long a mail is carried at most.</summary>
    public static readonly TimeSpan MailLifetime = TimeSpan.FromDays(30);

    /// <summary>How long an event stays <see cref="GroupVerdict.Pending"/> before it is dropped (groups section 3.2).</summary>
    public static readonly TimeSpan HeldFor = TimeSpan.FromDays(30);

    private const string EventLabel = "msfp-group-event-1\n";
    private const string MailLabel = "msfp-group-mail-1\n";
    private const string MailInfo = "msfp-group-mail-1";
    private const string JoinLabel = "msfp-group-join-1\n";

    private static byte[] Labeled(string label, ReadOnlySpan<byte> b) => [.. Encoding.ASCII.GetBytes(label), .. b];

    /// <summary>An event's or a group's ID: SHA-256 of the payload, base64url.</summary>
    public static string Id(ReadOnlySpan<byte> payload) => Base64UrlText.Encode(SHA256.HashData(payload));

    /// <summary>Signs an event with key, and gives its envelope and ID.</summary>
    /// <exception cref="ArgumentException">The payload is over 4,096 bytes.</exception>
    public static (GroupEnvelope Envelope, string Id) Sign(MsfpKey key, GroupEvent ev)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(ev);
        var b = ev.Payload();
        if (b.Length > MaxEvent)
        {
            throw new ArgumentException("msfp: an event over 4,096 bytes", nameof(ev));
        }

        return (new GroupEnvelope { Event = Base64UrlText.Encode(b), Signature = Base64UrlText.Encode(key.Sign(Labeled(EventLabel, b))) }, Id(b));
    }

    /// <summary>
    /// Checks an event's envelope: the payload, its signature under its
    /// signer (groups section 3.2, rule 1's first half). Null when it fails.
    /// </summary>
    public static (GroupEvent Event, byte[] Payload, string Id)? Read(GroupEnvelope e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var b = Base64UrlText.Decode(e.Event);
        if (b is null || b.Length == 0 || b.Length > MaxEvent)
        {
            return null;
        }

        GroupEvent ev;
        try
        {
            ev = GroupEvent.Read(b);
        }
        catch (ProtocolException)
        {
            return null;
        }

        var key = Base64UrlText.Decode(ev.Signer, 32);
        var sig = Base64UrlText.Decode(e.Signature, 64);
        if (key is null || sig is null || !Ed25519Strict.Verify(key, Labeled(EventLabel, b), sig))
        {
            return null;
        }

        return (ev, b, Id(b));
    }

    /// <summary>
    /// Reads an envelope as a member keeps it (groups section 3.2): well
    /// formed, signed under its signer, of the group (founded: its ID is
    /// group), dated at most 5 minutes after now. Whether it counts is
    /// <see cref="GroupState.Compute"/>'s.
    /// </summary>
    public static GroupCheck Check(GroupEnvelope e, string group, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Read(e) is not var (ev, _, id))
        {
            return new(null, "", "the envelope, the payload or its signature isn't valid");
        }

        if (ev.Seq < 1 || ev.Date < 1 || ev.Type.Length == 0 || ev.Member.Length == 0)
        {
            return new(ev, id, "a common member is missing");
        }

        if (ev.Date > now.ToUnixTimeSeconds() + 300)
        {
            return new(ev, id, "dated more than 5 minutes in the future");
        }

        if (ev.Type == Founded ? id != group : ev.Group != group)
        {
            return new(ev, id, ev.Type == Founded ? "another group's founded event" : "another group's event");
        }

        return new(ev, id, "");
    }

    /// <summary>Reads an envelope's event without checking it: for a held event's member and seq.</summary>
    public static GroupEvent? Peek(GroupEnvelope e) => e is null ? null : Read(e)?.Event;

    // ---- mail (groups section 5) ---------------------------------------

    // The HPKE info (msfp-group-mail-1, the group's ID, the mail's id) and
    // the associated data (from, to), decoded.
    private static (byte[] Info, byte[] Aad)? MailKeys(string group, string id, string from, string to)
    {
        var g = Base64UrlText.Decode(group, 32);
        var i = Base64UrlText.Decode(id, 16);
        var f = Base64UrlText.Decode(from, 32);
        var t = Base64UrlText.Decode(to, 32);
        if (g is null || i is null || f is null || t is null)
        {
            return null;
        }

        return ([.. Encoding.ASCII.GetBytes(MailInfo), .. g, .. i], [.. f, .. t]);
    }

    /// <summary>
    /// Seals what a mail carries to the recipient's encryption key (HPKE
    /// base mode, DHKEM(X25519, HKDF-SHA256), HKDF-SHA256, AES-128-GCM),
    /// signs the mail with key, and gives its envelope.
    /// </summary>
    /// <exception cref="ArgumentException">The mail's group, id, from or to isn't well formed, the key isn't one, or the mail is over 2,048 bytes.</exception>
    public static GroupEnvelope SealMail(MsfpKey key, GroupMail m, ReadOnlySpan<byte> to, SealedContent content, RandomNumberGenerator? random = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(content);
        var (info, aad) = MailKeys(m.Group, m.Id, m.From, m.To) ?? throw new ArgumentException("msfp: a mail's group, id, from or to isn't well formed", nameof(m));
        var ephemeral = new byte[32];
        if (random is null)
        {
            RandomNumberGenerator.Fill(ephemeral);
        }
        else
        {
            random.GetBytes(ephemeral);
        }

        var sealedBytes = Hpke.Seal(to, info, aad, content.Bytes(), ephemeral) ?? throw new ArgumentException("msfp: not an X25519 key", nameof(to));
        var b = (m with { Sealed = Base64UrlText.Encode(sealedBytes) }).Payload();
        if (b.Length > MaxMail)
        {
            throw new ArgumentException("msfp: a mail over 2,048 bytes", nameof(m));
        }

        return new GroupEnvelope { Mail = Base64UrlText.Encode(b), Signature = Base64UrlText.Encode(key.Sign(Labeled(MailLabel, b))) };
    }

    /// <summary>Checks a mail's envelope and gives the mail; its signature is checked against fromKey, the sender's current key. Null when it fails.</summary>
    public static GroupMail? ReadMail(GroupEnvelope e, ReadOnlySpan<byte> fromKey)
    {
        ArgumentNullException.ThrowIfNull(e);
        var b = Base64UrlText.Decode(e.Mail);
        var sig = Base64UrlText.Decode(e.Signature, 64);
        if (b is null || b.Length == 0 || b.Length > MaxMail || sig is null || !Ed25519Strict.Verify(fromKey, Labeled(MailLabel, b), sig))
        {
            return null;
        }

        GroupMail m;
        try
        {
            var o = StrictJson.ParseObject(b, nulls: false);
            m = new GroupMail
            {
                Group = o.GetString("group") ?? "", Id = o.GetString("id") ?? "", Kind = o.GetString("kind") ?? "",
                From = o.GetString("from") ?? "", To = o.GetString("to") ?? "", Answers = o.GetString("answers"),
                Expires = o.GetInteger("expires") ?? 0, Sealed = o.GetString("sealed") ?? "",
            };
        }
        catch (ProtocolException)
        {
            return null;
        }

        if (m.Kind is not ("request" or "answer") || MailKeys(m.Group, m.Id, m.From, m.To) is null)
        {
            return null;
        }

        return m;
    }

    /// <summary>Reads a mail's payload without checking its signature: to know whose key checks it. Null when it isn't one.</summary>
    public static GroupMail? PeekMail(GroupEnvelope e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var b = Base64UrlText.Decode(e.Mail);
        if (b is null || b.Length == 0 || b.Length > MaxMail)
        {
            return null;
        }

        try
        {
            var o = StrictJson.ParseObject(b, nulls: false);
            return new GroupMail
            {
                Group = o.GetString("group") ?? "", Id = o.GetString("id") ?? "", Kind = o.GetString("kind") ?? "",
                From = o.GetString("from") ?? "", To = o.GetString("to") ?? "", Answers = o.GetString("answers"),
                Expires = o.GetInteger("expires") ?? 0, Sealed = o.GetString("sealed") ?? "",
            };
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    /// <summary>Opens a mail to this server with its encryption key's private part; null when it doesn't open.</summary>
    public static SealedContent? OpenMail(GroupMail m, ReadOnlySpan<byte> priv)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (MailKeys(m.Group, m.Id, m.From, m.To) is not var (info, aad))
        {
            return null;
        }

        var sealedBytes = Base64UrlText.Decode(m.Sealed);
        if (sealedBytes is null || sealedBytes.Length < 32 + 16 || Hpke.Open(priv, info, aad, sealedBytes) is not { } pt)
        {
            return null;
        }

        try
        {
            var o = StrictJson.ParseObject(pt, nulls: false);
            return new SealedContent
            {
                Address = o.GetString("address") ?? "", NextCommitment = o.GetString("nextCommitment") ?? "",
                Inbound = o.GetBool("inbound") ?? false, Certificates = o.GetStrings("certificates"),
            };
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    /// <summary>A mail's ID from 16 random bytes.</summary>
    public static string MailId(ReadOnlySpan<byte> random) => Base64UrlText.Encode(random);

    /// <summary>An X25519 key pair for a profile from 32 random bytes: the private part (to seal and keep) and the public one.</summary>
    public static (byte[] Private, byte[] Public) MailKeyPair(ReadOnlySpan<byte> random)
    {
        if (random.Length != 32)
        {
            throw new ArgumentException("msfp: an X25519 private key is 32 bytes", nameof(random));
        }

        return (random.ToArray(), Hpke.PublicKey(random));
    }

    // ---- group-join (groups section 8.4) -------------------------------

    /// <summary>
    /// group-join's proof: HMAC-SHA256, keyed with a group code's secret,
    /// over msfp-group-join-1, a line feed and the redeemer's public key,
    /// base64url.
    /// </summary>
    public static string JoinProof(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> redeemer) =>
        Base64UrlText.Encode(HMACSHA256.HashData(secret, Labeled(JoinLabel, redeemer)));

    /// <summary>Checks a <see cref="JoinProof"/> in constant time.</summary>
    public static bool CheckJoinProof(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> redeemer, string? proof)
    {
        var got = Base64UrlText.Decode(proof, 32);
        return got is not null && CryptographicOperations.FixedTimeEquals(got, Base64UrlText.Decode(JoinProof(secret, redeemer))!);
    }

    internal static string Day(long date) =>
        DateTimeOffset.FromUnixTimeSeconds(date).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
