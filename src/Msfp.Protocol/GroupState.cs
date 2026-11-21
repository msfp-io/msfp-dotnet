using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// What a copy's events give (groups sections 3.2 to 3.5): members, roles,
/// settings, bans. Built by applying the events in the order the copy
/// keeps them; two copies with the same events give the same state.
/// </summary>
public sealed class GroupState
{
    private readonly Dictionary<string, long> _seq = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<long, string>> _payloads = new(StringComparer.Ordinal);
    private readonly List<(long OwnerSeq, string Target, bool On)> _adminLog = [];
    private readonly Dictionary<string, long> _removedAt = new(StringComparer.Ordinal); // admin -> the last seq that counts
    private readonly Dictionary<string, long> _bannedAt = new(StringComparer.Ordinal);  // banned -> the last seq that counts
    private readonly Dictionary<string, (long Seq, string Key)> _revoked = new(StringComparer.Ordinal); // the key revoked, its last good seq
    private readonly Dictionary<string, List<(long Seq, string Key)>> _keys = new(StringComparer.Ordinal); // keys in force from a seq
    private readonly Dictionary<string, long> _capCount = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <summary>The group's ID: its founded event's; "" before it.</summary>
    public string Id { get; private set; } = "";

    /// <summary>The group's name.</summary>
    public string Name { get; private set; } = "";

    /// <summary>The settings in force.</summary>
    public GroupSettings Settings { get; private set; } = GroupSettings.Default;

    /// <summary>The owner's member ID.</summary>
    public string Owner { get; private set; } = "";

    /// <summary>The heir's member ID; "" for none.</summary>
    public string Heir { get; private set; } = "";

    /// <summary>The members, by member ID.</summary>
    public Dictionary<string, GroupMember> Members { get; } = new(StringComparer.Ordinal);

    /// <summary>The banned, by member ID: the banned event's ID.</summary>
    public Dictionary<string, string> Banned { get; } = new(StringComparer.Ordinal);

    /// <summary>The bans' reasons, by member ID.</summary>
    public Dictionary<string, string> Reasons { get; } = new(StringComparer.Ordinal);

    /// <summary>A banned server as it was a member, for the panels.</summary>
    public Dictionary<string, GroupMember> WasBanned { get; } = new(StringComparer.Ordinal);

    /// <summary>The members excluded for equivocation (groups section 3.2).</summary>
    public List<string> Excluded { get; } = [];

    /// <summary>Whether the group is dissolved.</summary>
    public bool Dissolved { get; private set; }

    /// <summary>Dissolved with keepPairings.</summary>
    public bool Keep { get; private set; }

    /// <summary>The group's count of owner events.</summary>
    public long OwnerSeq { get; private set; }

    /// <summary>A member's last kept seq (0 for none).</summary>
    public long Seq(string member) => _seq.GetValueOrDefault(member);

    /// <summary>Every member's last kept seq, for group-sync.</summary>
    public Dictionary<string, long> Have() => new(_seq, StringComparer.Ordinal);

    /// <summary>Whether a member is the owner or an admin now.</summary>
    public bool IsAdmin(string member) => member == Owner || (Members.TryGetValue(member, out var m) && m.Admin);

    private bool AdminAsOf(string member, long k)
    {
        var on = false;
        foreach (var c in _adminLog)
        {
            if (c.Target == member && c.OwnerSeq <= k)
            {
                on = c.On;
            }
        }

        return on;
    }

    // The member's key in force at seq: its member ID, then each rotation's
    // new key from the seq of its rotated event on (section 3.4).
    private string KeyAt(string member, long seq)
    {
        var k = member;
        foreach (var f in _keys.GetValueOrDefault(member) ?? [])
        {
            if (f.Seq <= seq)
            {
                k = f.Key;
            }
        }

        return k;
    }

    private static GroupApplied Refuse(GroupEvent? ev, string id, string why) => new(GroupVerdict.Refused, ev, id, why);

    /// <summary>
    /// Keeps an event if the rules allow it (groups sections 3.2 to 3.5),
    /// now being the copy's clock: <see cref="GroupVerdict.Held"/> when it
    /// must wait, <see cref="GroupVerdict.Had"/> when it is there already,
    /// <see cref="GroupVerdict.Refused"/> when the rules refuse it.
    /// </summary>
    public GroupApplied Apply(GroupEnvelope e, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (GroupEvents.Read(e) is not var (ev, b, id))
        {
            return Refuse(null, "", "the envelope, the payload or its signature isn't valid");
        }

        if (_ids.Contains(id))
        {
            return new(GroupVerdict.Had, ev, id, "");
        }

        if (ev.Seq < 1 || ev.Date < 1 || ev.Type.Length == 0 || ev.Member.Length == 0)
        {
            return Refuse(ev, id, "a common member is missing");
        }

        if (ev.Date > now.ToUnixTimeSeconds() + 300)
        {
            return Refuse(ev, id, "dated more than 5 minutes in the future");
        }

        var text = Convert.ToBase64String(b);
        if (ev.Type == GroupEvents.Founded)
        {
            if (Id.Length > 0)
            {
                return Refuse(ev, id, "a second founded event");
            }

            if (ev.Seq != 1 || ev.Member != ev.Signer || ev.OwnerSeq != 1 || ev.Group is not null)
            {
                return Refuse(ev, id, "founded: seq 1, ownerSeq 1, by the founder's own key");
            }

            Id = id;
            Owner = ev.Member;
            OwnerSeq = 1;
            Name = Music.CleanText(ev.Name ?? "", PairingCode.MaxGroupName);
            ApplySettings(ev.Settings);
            Members[ev.Member] = new GroupMember { Id = ev.Member, Key = ev.Member, Joined = ev.Date, JoinedId = id };
            KeepEvent(ev, text, id);
            return new(GroupVerdict.Kept, ev, id, "");
        }

        if (Id.Length == 0 || ev.Group != Id)
        {
            return Refuse(ev, id, "another group's event");
        }

        if (Dissolved)
        {
            return Refuse(ev, id, "the group is dissolved");
        }

        if (Excluded.Contains(ev.Member))
        {
            return Refuse(ev, id, "its member was excluded");
        }

        if (_payloads.TryGetValue(ev.Member, out var kept) && kept.TryGetValue(ev.Seq, out var prev) && prev != text)
        {
            // Equivocation (groups section 3.2): the member excluded.
            Excluded.Add(ev.Member);
            Remove(ev.Member);
            return Refuse(ev, id, "equivocation: two events with one seq");
        }

        var last = Seq(ev.Member);
        if (ev.Seq > last + 1)
        {
            return new(GroupVerdict.Held, ev, id, "");
        }

        if (ev.Seq <= last)
        {
            return new(GroupVerdict.Had, ev, id, "");
        }

        if (!Members.ContainsKey(ev.Member) && ev.Type != GroupEvents.Left)
        {
            return Refuse(ev, id, "its signer isn't a member");
        }

        if (KeyAt(ev.Member, ev.Seq) != ev.Signer && ev.Type != GroupEvents.Rotated)
        {
            return Refuse(ev, id, "signer isn't the member's key at that seq");
        }

        if (_removedAt.TryGetValue(ev.Member, out var removed) && ev.Seq > removed && ev.Type is GroupEvents.Joined or GroupEvents.Banned)
        {
            return Refuse(ev, id, "signed after its signer's role was removed");
        }

        if (_bannedAt.TryGetValue(ev.Member, out var banned) && ev.Seq > banned)
        {
            return Refuse(ev, id, "signed after its signer was banned");
        }

        if (_revoked.TryGetValue(ev.Member, out var r) && ev.Signer == r.Key && ev.Seq > r.Seq)
        {
            return Refuse(ev, id, "signed with a revoked key after its last good event");
        }

        var owner = ev.Member == Owner;
        var ownerEvent = owner && IsOwnerEvent(ev.Type);
        if (ownerEvent && ev.OwnerSeq != OwnerSeq + 1)
        {
            return ev.OwnerSeq > OwnerSeq + 1 ? new(GroupVerdict.Held, ev, id, "") : Refuse(ev, id, "an owner event whose ownerSeq was taken");
        }

        var asOf = ev.AsOf ?? 0;
        if (!owner && asOf > OwnerSeq)
        {
            return new(GroupVerdict.Held, ev, id, "");
        }

        var admin = !owner && asOf > 0 && AdminAsOf(ev.Member, asOf);
        if (ApplyType(ev, id, owner, admin) is { } why)
        {
            return Refuse(ev, id, why);
        }

        if (ownerEvent)
        {
            OwnerSeq = ev.OwnerSeq!.Value;
        }

        KeepEvent(ev, text, id);
        return new(GroupVerdict.Kept, ev, id, "");
    }

    private static bool IsOwnerEvent(string type) =>
        type is not (GroupEvents.Profile or GroupEvents.Rotated or GroupEvents.Revoked or GroupEvents.Left);

    private void KeepEvent(GroupEvent ev, string text, string id)
    {
        _seq[ev.Member] = ev.Seq;
        if (!_payloads.TryGetValue(ev.Member, out var kept))
        {
            _payloads[ev.Member] = kept = [];
        }

        kept[ev.Seq] = text;
        _ids.Add(id);
    }

    private void Remove(string member)
    {
        Members.Remove(member);
        if (Heir == member)
        {
            Heir = "";
        }
    }

    // Each setting in its range replaces the one in force; one out of it
    // is ignored.
    private void ApplySettings(GroupSettings? s)
    {
        if (s is null)
        {
            return;
        }

        Settings = new GroupSettings(s.WaitDays is >= 0 and <= 30 ? s.WaitDays : Settings.WaitDays,
            s.CapPerDay is >= 1 and <= 100 ? s.CapPerDay : Settings.CapPerDay);
    }

    // The type's rules (section 3.3) on the state so far; null when they
    // hold (and the state changed), why not otherwise.
    private string? ApplyType(GroupEvent ev, string id, bool owner, bool admin)
    {
        bool Member(string? m) => m is not null && Members.ContainsKey(m);
        switch (ev.Type)
        {
            case GroupEvents.Profile:
            {
                var m = Members[ev.Member];
                m.Name = Music.CleanText(ev.Name ?? "", PairingCode.MaxGroupName);
                if (Base64UrlText.Decode(ev.EncryptionKey, 32) is { } k)
                {
                    m.EncryptionKey = k;
                }

                if (Base64UrlText.Decode(ev.NextCommitment, 32) is { } c)
                {
                    m.NextCommitment = c;
                }

                return null;
            }

            case GroupEvents.Joined:
            {
                if (!owner && !admin)
                {
                    return "joined by a member without the right to invite";
                }

                if (Base64UrlText.Decode(ev.NewMember, 32) is not { } k || !Ed25519Strict.ValidKey(k))
                {
                    return "newMember isn't a key";
                }

                if (Banned.ContainsKey(ev.NewMember!))
                {
                    return "the newcomer is banned";
                }

                if (Member(ev.NewMember))
                {
                    return "the newcomer is a member already";
                }

                var day = ev.Member + " " + GroupEvents.Day(ev.Date);
                if (_capCount.GetValueOrDefault(day) >= Settings.CapPerDay)
                {
                    return "over the inviter's daily cap";
                }

                _capCount[day] = _capCount.GetValueOrDefault(day) + 1;
                Members[ev.NewMember!] = new GroupMember
                {
                    Id = ev.NewMember!, Key = ev.NewMember!, Name = Music.CleanText(ev.Name ?? "", PairingCode.MaxGroupName),
                    InvitedBy = ev.Member, Joined = ev.Date, JoinedId = id,
                };
                // A member that left and comes back keeps counting from its seq.
                _removedAt.Remove(ev.NewMember!);
                return null;
            }

            case GroupEvents.Admin:
            {
                if (!owner)
                {
                    return "admin events are the owner's";
                }

                if (!Member(ev.Target) || ev.On is not { } on)
                {
                    return "the target isn't a member";
                }

                _adminLog.Add((ev.OwnerSeq ?? 0, ev.Target!, on));
                var m = Members[ev.Target!];
                m.Admin = on;
                if (on)
                {
                    m.AdminAt = ev.OwnerSeq ?? 0;
                    _removedAt.Remove(ev.Target!);
                }
                else if (ev.LastSeq is { } lastSeq)
                {
                    _removedAt[ev.Target!] = lastSeq;
                }

                return null;
            }

            case GroupEvents.Heir:
                if (!owner)
                {
                    return "heir events are the owner's";
                }

                if (!string.IsNullOrEmpty(ev.Target) && !Member(ev.Target))
                {
                    return "the target isn't a member";
                }

                Heir = ev.Target ?? "";
                return null;

            case GroupEvents.Owner:
                if (!owner)
                {
                    return "owner events are the owner's";
                }

                if (!Member(ev.Target))
                {
                    return "the target isn't a member";
                }

                Owner = ev.Target!;
                if (Heir == ev.Target)
                {
                    Heir = "";
                }

                return null;

            case GroupEvents.Settings:
            {
                if (!owner)
                {
                    return "settings are the owner's";
                }

                if (Music.CleanText(ev.Name ?? "", PairingCode.MaxGroupName) is { Length: > 0 } n)
                {
                    Name = n;
                }

                ApplySettings(ev.Settings);
                return null;
            }

            case GroupEvents.Banned:
            {
                if (!owner && !admin)
                {
                    return "banned by a member without the right to ban";
                }

                if (ev.Target == Owner)
                {
                    return "the owner can't be banned";
                }

                if (!owner && ev.Target is not null && (AdminAsOf(ev.Target, ev.AsOf ?? 0) || (Members.TryGetValue(ev.Target, out var t) && t.Admin)))
                {
                    return "an admin can't ban an admin";
                }

                if (!Member(ev.Target) || ev.LastSeq is not { } lastSeq)
                {
                    return "the target isn't a member";
                }

                if ((ev.Reason ?? "").EnumerateRunes().Count() > GroupEvents.MaxReason)
                {
                    return "a reason over 280 code points";
                }

                Banned[ev.Target!] = id;
                Reasons[ev.Target!] = Music.CleanText(ev.Reason ?? "", GroupEvents.MaxReason);
                _bannedAt[ev.Target!] = lastSeq;
                WasBanned[ev.Target!] = Members[ev.Target!] with { };
                Remove(ev.Target!);
                return null;
            }

            case GroupEvents.Unbanned:
                if (!owner)
                {
                    return "unbanned is the owner's";
                }

                foreach (var (m, ban) in Banned)
                {
                    if (ban == ev.Ban)
                    {
                        Banned.Remove(m);
                        Reasons.Remove(m);
                        WasBanned.Remove(m);
                        return null;
                    }
                }

                return "no such ban";

            case GroupEvents.Left:
                if (owner)
                {
                    return "the owner gives the group away before leaving";
                }

                if (!Member(ev.Member))
                {
                    return "not a member";
                }

                Remove(ev.Member);
                return null;

            case GroupEvents.Dissolved:
                if (!owner)
                {
                    return "dissolved is the owner's";
                }

                Dissolved = true;
                Keep = ev.KeepPairings == true;
                return null;

            case GroupEvents.Rotated:
                return ApplyRotated(ev);

            case GroupEvents.Revoked:
                return ApplyRevoked(ev);

            default:
                // A type of a later version: kept and passed on, changing nothing.
                return null;
        }
    }

    private static string? Text(JsonObject o, string name)
    {
        try
        {
            return o.GetString(name);
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    private static long? Integer(JsonObject o, string name)
    {
        try
        {
            return o.GetInteger(name);
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    // rotated (section 3.3): the statement verifies as core section 12.2
    // says, from the member's current key to the signer; when its profile
    // holds a commitment, the new key matches it.
    private string? ApplyRotated(GroupEvent ev)
    {
        if (ev.Statement is not { } st)
        {
            return "not a rotation statement";
        }

        var cur = KeyAt(ev.Member, ev.Seq);
        var oldText = Text(st, "oldKey");
        var newText = Text(st, "newKey");
        if (oldText != cur || newText != ev.Signer)
        {
            return "the rotation isn't from the member's key to the signer";
        }

        var old = Base64UrlText.Decode(oldText, 32);
        var nu = Base64UrlText.Decode(newText, 32);
        var commitmentText = Text(st, "nextCommitment");
        var commitment = string.IsNullOrEmpty(commitmentText) ? null : Base64UrlText.Decode(commitmentText, 32);
        var os = Base64UrlText.Decode(Text(st, "oldSignature"), 64);
        var ns = Base64UrlText.Decode(Text(st, "newSignature"), 64);
        if (old is null || nu is null || os is null || ns is null || Integer(st, "date") is not { } date || !Ed25519Strict.ValidKey(nu))
        {
            return "the rotation statement doesn't verify";
        }

        var msg = Rotations.RotationStatement(old, nu, commitment, date);
        if (!Ed25519Strict.Verify(old, msg, os) || !Ed25519Strict.Verify(nu, msg, ns))
        {
            return "the rotation statement doesn't verify";
        }

        if (Members.TryGetValue(ev.Member, out var m) && m.NextCommitment is not null)
        {
            if (!Keys.Commitment(nu).AsSpan().SequenceEqual(m.NextCommitment))
            {
                return "the new key isn't the committed one";
            }

            m.NextCommitment = commitment;
        }

        if (!_keys.TryGetValue(ev.Member, out var list))
        {
            _keys[ev.Member] = list = [];
        }

        list.Add((ev.Seq, newText));
        if (m is not null)
        {
            m.Key = newText;
        }

        return null;
    }

    // revoked (section 3.3): the statement verifies as core section 12.6
    // says; the member's events signed with the revoked key after lastSeq
    // don't count. With two, the lower lastSeq holds.
    private string? ApplyRevoked(GroupEvent ev)
    {
        if (ev.Statement is not { } st || ev.LastSeq is not { } lastSeq)
        {
            return "not a revocation statement";
        }

        var keyText = Text(st, "key");
        var k = Base64UrlText.Decode(keyText, 32);
        var sig = Base64UrlText.Decode(Text(st, "signature"), 64);
        if (k is null || sig is null || Integer(st, "date") is not { } date || !Ed25519Strict.Verify(k, Rotations.RevocationStatement(k, date), sig))
        {
            return "the revocation statement doesn't verify";
        }

        if (!_revoked.TryGetValue(ev.Member, out var r) || lastSeq < r.Seq)
        {
            _revoked[ev.Member] = (lastSeq, keyText!);
        }

        return null;
    }

    /// <summary>
    /// Who an owner stepping down names (groups section 3.3): its heir if
    /// still a member, else the oldest admin (named at the lowest ownerSeq),
    /// else the oldest member (the lowest joined date, then the lowest event
    /// ID); "" when the owner is alone.
    /// </summary>
    public string Successor()
    {
        if (Heir.Length > 0 && Members.ContainsKey(Heir))
        {
            return Heir;
        }

        var others = Members.Values.Where(m => m.Id != Owner).ToList();
        var admin = others.Where(m => m.Admin).OrderBy(m => m.AdminAt).ThenBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault();
        if (admin is not null)
        {
            return admin.Id;
        }

        return others.OrderBy(m => m.Joined).ThenBy(m => m.JoinedId, StringComparer.Ordinal).FirstOrDefault()?.Id ?? "";
    }
}
