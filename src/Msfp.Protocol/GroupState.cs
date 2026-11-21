using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// What a group's kept events give (groups section 3.5): members, roles,
/// settings, bans. <see cref="Compute"/> replays them in one order that
/// doesn't depend on how they arrived, so copies that kept the same events
/// have the same state.
/// </summary>
public sealed class GroupState
{
    // At most this many replays while the cuts change (section 3.5, rule 3).
    private const int MaxRounds = 8;

    private readonly Dictionary<string, long> _seq = new(StringComparer.Ordinal); // member -> last replayed seq
    private readonly List<(long OwnerSeq, string Target, bool On)> _adminLog = [];
    private readonly Dictionary<string, long> _removedAt = new(StringComparer.Ordinal); // admin -> the last seq that counts
    private readonly Dictionary<string, long> _bannedAt = new(StringComparer.Ordinal);  // banned -> the last seq that counts
    private readonly Dictionary<string, (long Seq, string Key)> _revoked = new(StringComparer.Ordinal); // the key revoked, its last good seq
    private readonly Dictionary<string, List<(long Seq, string Key)>> _keys = new(StringComparer.Ordinal); // keys in force from a seq
    private readonly HashSet<string> _everMember = new(StringComparer.Ordinal); // named by a counted founded or joined
    private readonly Dictionary<string, long> _highest = new(StringComparer.Ordinal); // member -> its highest seq kept, whatever its verdict
    private Dictionary<string, int> _capRank = new(StringComparer.Ordinal); // joined event ID -> its rank in its inviter's UTC day

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

    /// <summary>A member's last replayed seq (0 for none).</summary>
    public long Seq(string member) => _seq.GetValueOrDefault(member);

    /// <summary>
    /// A member's highest seq kept, counted or not: the member signs its
    /// next event with the one after it, never twice the same (section 3.2).
    /// </summary>
    public long Highest(string member) => Math.Max(_highest.GetValueOrDefault(member), Seq(member));

    /// <summary>Every member's last replayed seq, for group-sync.</summary>
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

    // An event in a replay.
    private sealed class Entry(GroupEvent ev, string id)
    {
        public GroupEvent Event { get; } = ev;

        public string Id { get; } = id;

        public bool Done { get; set; }
    }

    // What bans, removals of admins and revocations take away from a
    // member's events after a seq: applied from the start of a replay, so
    // that the events a cut covers never count, whenever the cutting event
    // comes (section 3.5, rule 3).
    private sealed class Cuts
    {
        public Dictionary<string, long> Banned { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long> Removed { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, (long Seq, string Key)> Revoked { get; } = new(StringComparer.Ordinal);

        public bool SameAs(Cuts o) => Same(Banned, o.Banned) && Same(Removed, o.Removed) && Same(Revoked, o.Revoked);

        private static bool Same<T>(Dictionary<string, T> a, Dictionary<string, T> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && EqualityComparer<T>.Default.Equals(v, kv.Value));

        // The cut a counted event makes, after it was replayed on s; with
        // two on one member, the lower lastSeq holds.
        public void Add(GroupEvent ev, GroupState s)
        {
            switch (ev.Type)
            {
                case GroupEvents.Banned:
                    Lower(Banned, ev.Target!, ev.LastSeq!.Value);
                    break;
                case GroupEvents.Admin when ev.On == false && ev.LastSeq is { } last:
                    Lower(Removed, ev.Target!, last);
                    break;
                case GroupEvents.Revoked:
                    Revoked[ev.Member] = s._revoked[ev.Member];
                    break;
            }
        }

        private static void Lower(Dictionary<string, long> d, string member, long seq)
        {
            if (!d.TryGetValue(member, out var old) || seq < old)
            {
                d[member] = seq;
            }
        }
    }

    /// <summary>
    /// The state a set of kept events gives (groups section 3.5), the same
    /// whatever order they arrived in, with each event's verdict by its ID.
    /// group is the group's ID (its founded event's). Envelopes that don't
    /// read, or are another group's, are left out (<see cref="GroupEvents.Check"/>
    /// says why).
    /// </summary>
    /// <remarks>
    /// The events are replayed in one order: at each step, the ready event
    /// with the lowest ID whose rules hold counts; when none's hold, the
    /// ready one with the lowest ID is replayed without counting. An event
    /// is ready once its member's earlier events, the owner event it follows
    /// or names, its member's joined and, for a ban or an admin's removal,
    /// its target's events up to its lastSeq have been replayed. Bans,
    /// removals of admins and revocations take away events of their target
    /// after a seq whenever they come: the replay starts again with them
    /// applied from the start, until they no longer change.
    /// </remarks>
    public static (GroupState State, IReadOnlyDictionary<string, GroupEventResult> Results) Compute(string group, IEnumerable<GroupEnvelope> envelopes)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(envelopes);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<Entry>();
        foreach (var e in envelopes)
        {
            if (GroupEvents.Read(e) is not var (ev, _, id) || (ev.Type == GroupEvents.Founded ? id != group : ev.Group != group) || !seen.Add(id))
            {
                continue;
            }

            events.Add(new Entry(ev, id));
        }

        events.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        // The daily cap: each inviter's joined events of a UTC day, in date
        // then ID order (section 3.5); the ID order is the list's, and
        // OrderBy is stable.
        var capRank = new Dictionary<string, int>(StringComparer.Ordinal);
        var days = events.Where(k => k.Event.Type == GroupEvents.Joined)
            .GroupBy(k => k.Event.Member + " " + GroupEvents.Day(k.Event.Date), StringComparer.Ordinal);
        foreach (var day in days)
        {
            var rank = 0;
            foreach (var k in day.OrderBy(k => k.Event.Date))
            {
                capRank[k.Id] = rank++;
            }
        }

        var cuts = new Cuts();
        var (s, results, next) = Replay(events, cuts, capRank);
        for (var round = 1; round < MaxRounds && !next.SameAs(cuts); round++)
        {
            cuts = next;
            (s, results, next) = Replay(events, cuts, capRank);
        }

        return (s, results);
    }

    // Replays the events once, with the cuts applied from the start, and
    // gives the cuts its counted events make.
    private static (GroupState State, Dictionary<string, GroupEventResult> Results, Cuts Next) Replay(List<Entry> events, Cuts cuts, Dictionary<string, int> capRank)
    {
        var s = new GroupState { _capRank = capRank };
        foreach (var (m, seq) in cuts.Banned)
        {
            s._bannedAt[m] = seq;
        }

        foreach (var (m, seq) in cuts.Removed)
        {
            s._removedAt[m] = seq;
        }

        foreach (var (m, r) in cuts.Revoked)
        {
            s._revoked[m] = r;
        }

        var next = new Cuts();
        var bySeq = events.ToLookup(k => (k.Event.Member, k.Event.Seq));
        foreach (var k in events)
        {
            k.Done = false;
        }

        var results = new Dictionary<string, GroupEventResult>(StringComparer.Ordinal);
        for (var final = false; ;)
        {
            var progressed = false;
            foreach (var k in events)
            {
                if (k.Done || s.Step(k, bySeq, final) is not { } r)
                {
                    continue;
                }

                final = false;
                k.Done = progressed = true;
                results[k.Id] = new GroupEventResult(k.Event, r.Verdict, r.Reason);
                if (r.Verdict == GroupVerdict.Counted)
                {
                    next.Add(k.Event, s);
                }

                break; // again from the lowest ID
            }

            if (!progressed)
            {
                if (final)
                {
                    break;
                }

                // No ready event can count: the lowest ready one is refused.
                final = true;
            }
        }

        foreach (var k in events)
        {
            if (!k.Done)
            {
                results[k.Id] = new GroupEventResult(k.Event, GroupVerdict.Pending, "waiting for events it needs");
            }

            if (results[k.Id].Verdict != GroupVerdict.Forged && k.Event.Seq > s._highest.GetValueOrDefault(k.Event.Member))
            {
                s._highest[k.Event.Member] = k.Event.Seq;
            }
        }

        return (s, results, next);
    }

    // Whether an event is its member's: signed with the member's key in
    // force at its seq, or, for rotated, a statement from that key to the
    // signer that verifies (section 3.2).
    private bool SignedByMember(GroupEvent ev)
    {
        var cur = KeyAt(ev.Member, ev.Seq);
        if (ev.Type != GroupEvents.Rotated)
        {
            return ev.Signer == cur;
        }

        return ev.Statement is { } st && Text(st, "oldKey") == cur && Text(st, "newKey") == ev.Signer && VerifiedRotation(st) is not null;
    }

    // Replays one event: null when it isn't replayed at this turn, because
    // it isn't ready, or its rules don't hold now while other events may
    // count (with final, it is refused instead).
    private (GroupVerdict Verdict, string Reason)? Step(Entry k, ILookup<(string, long), Entry> bySeq, bool final)
    {
        var (ev, id) = (k.Event, k.Id);
        if (ev.Type == GroupEvents.Founded)
        {
            if (Id.Length > 0)
            {
                return (GroupVerdict.Refused, "a second founded event");
            }

            if (ev.Seq != 1 || ev.Member != ev.Signer || ev.OwnerSeq != 1 || !string.IsNullOrEmpty(ev.Group))
            {
                return (GroupVerdict.Forged, "founded: seq 1, ownerSeq 1, by the founder's own key");
            }

            Id = id;
            Owner = ev.Member;
            OwnerSeq = 1;
            Name = Music.CleanText(ev.Name ?? "", PairingCode.MaxGroupName);
            ApplySettings(ev.Settings);
            Members[ev.Member] = new GroupMember { Id = ev.Member, Key = ev.Member, Joined = ev.Date, JoinedId = id };
            _everMember.Add(ev.Member);
            KeepEvent(ev);
            return (GroupVerdict.Counted, "");
        }

        if (Id.Length == 0 || ev.Seq > Seq(ev.Member) + 1)
        {
            return null; // pending: the founded event, or the member's earlier events
        }

        if (!SignedByMember(ev))
        {
            return (GroupVerdict.Forged, "not signed by its member's key in force at its seq");
        }

        if (ev.Seq <= Seq(ev.Member))
        {
            // Its seq was replayed: another payload of the member's with
            // that seq, met below.
            return (GroupVerdict.Refused, "equivocation: two events with one seq");
        }

        if (!_everMember.Contains(ev.Member))
        {
            return null; // pending: its joined hasn't counted yet
        }

        (GroupVerdict, string)? Refuse(string why)
        {
            KeepEvent(ev);
            // An owner event replayed without counting takes its ownerSeq
            // all the same, as any replayed event its seq (section 3.5): the
            // owner's next one is ready after it.
            if (ev.Member == Owner && IsOwnerEvent(ev.Type) && ev.OwnerSeq is { } os && os == OwnerSeq + 1)
            {
                OwnerSeq = os;
            }

            return (GroupVerdict.Refused, why);
        }

        // What may hold later: the event waits while others can count.
        (GroupVerdict, string)? Fail(string why) => final ? Refuse(why) : null;

        // Equivocation (section 3.2): another payload of the member's own
        // with this seq: neither counts, the member is excluded.
        foreach (var other in bySeq[(ev.Member, ev.Seq)])
        {
            if (other.Id != id && SignedByMember(other.Event))
            {
                if (!Excluded.Contains(ev.Member))
                {
                    Excluded.Add(ev.Member);
                    Remove(ev.Member);
                }

                return Refuse("equivocation: two events with one seq");
            }
        }

        if (Dissolved)
        {
            return Refuse("the group is dissolved");
        }

        if (Excluded.Contains(ev.Member))
        {
            return Refuse("its member was excluded");
        }

        if (!Members.ContainsKey(ev.Member) && ev.Type != GroupEvents.Left)
        {
            return Fail("its signer isn't a member");
        }

        if (_removedAt.TryGetValue(ev.Member, out var removed) && ev.Seq > removed && ev.Type is GroupEvents.Joined or GroupEvents.Banned)
        {
            return Refuse("signed after its signer's role was removed");
        }

        if (_bannedAt.TryGetValue(ev.Member, out var banned) && ev.Seq > banned)
        {
            return Refuse("signed after its signer was banned");
        }

        // A revoked event is exempt from its own key's cut: it is how the
        // member says the key is gone.
        if (_revoked.TryGetValue(ev.Member, out var r) && ev.Signer == r.Key && ev.Seq > r.Seq && ev.Type != GroupEvents.Revoked)
        {
            return Refuse("signed with a revoked key after its last good event");
        }

        var owner = ev.Member == Owner;
        var ownerEvent = owner && IsOwnerEvent(ev.Type);
        if (ownerEvent)
        {
            var ownerSeq = ev.OwnerSeq ?? 0;
            if (ownerSeq > OwnerSeq + 1)
            {
                return null; // pending: the owner event before it
            }

            if (ownerSeq != OwnerSeq + 1)
            {
                return Refuse("an owner event whose ownerSeq was taken");
            }
        }

        var asOf = ev.AsOf ?? 0;
        if (!owner && asOf > OwnerSeq)
        {
            return null; // pending: the owner event its asOf names
        }

        // A ban and the removal of an admin wait for their target's events
        // up to their lastSeq: those count (section 3.5).
        if ((ev.Type == GroupEvents.Banned || (ev.Type == GroupEvents.Admin && ev.On == false)) && ev.LastSeq is { } upTo && Seq(ev.Target ?? "") < upTo)
        {
            return null;
        }

        var admin = !owner && asOf > 0 && asOf <= OwnerSeq && AdminAsOf(ev.Member, asOf);
        if (ApplyType(ev, id, owner, admin) is { } why)
        {
            return Fail(why);
        }

        if (ownerEvent)
        {
            OwnerSeq = ev.OwnerSeq!.Value;
        }

        KeepEvent(ev);
        return (GroupVerdict.Counted, "");
    }

    private static bool IsOwnerEvent(string type) =>
        type is not (GroupEvents.Profile or GroupEvents.Rotated or GroupEvents.Revoked or GroupEvents.Left);

    private void KeepEvent(GroupEvent ev) => _seq[ev.Member] = ev.Seq;

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

                if (Banned.ContainsKey(ev.NewMember!) || Excluded.Contains(ev.NewMember!))
                {
                    return "the newcomer is banned, or excluded";
                }

                if (Member(ev.NewMember))
                {
                    return "the newcomer is a member already";
                }

                // The inviter's joined events of a UTC day beyond the cap,
                // by date then ID, don't count (section 3.5).
                if (_capRank.GetValueOrDefault(id) >= Settings.CapPerDay)
                {
                    return "over the inviter's daily cap";
                }

                _everMember.Add(ev.NewMember!);
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

        if (VerifiedRotation(st) is not var (nu, commitment))
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

        list.Add((ev.Seq, newText!));
        if (m is not null)
        {
            m.Key = newText;
        }

        return null;
    }

    // A rotation statement that verifies as core section 12.2 says: its new
    // key and next commitment; null when it doesn't.
    private static (byte[] NewKey, byte[]? Commitment)? VerifiedRotation(JsonObject st)
    {
        var old = Base64UrlText.Decode(Text(st, "oldKey"), 32);
        var nu = Base64UrlText.Decode(Text(st, "newKey"), 32);
        var commitmentText = Text(st, "nextCommitment");
        var commitment = string.IsNullOrEmpty(commitmentText) ? null : Base64UrlText.Decode(commitmentText, 32);
        var os = Base64UrlText.Decode(Text(st, "oldSignature"), 64);
        var ns = Base64UrlText.Decode(Text(st, "newSignature"), 64);
        if (old is null || nu is null || os is null || ns is null || Integer(st, "date") is not { } date || !Ed25519Strict.ValidKey(nu))
        {
            return null;
        }

        var msg = Rotations.RotationStatement(old, nu, commitment, date);
        return Ed25519Strict.Verify(old, msg, os) && Ed25519Strict.Verify(nu, msg, ns) ? (nu, commitment) : null;
    }

    // revoked (section 3.3): the statement verifies as core section 12.6
    // says, and its key is one of the member's; the member's events signed
    // with the revoked key after lastSeq don't count. With two, the lower
    // lastSeq holds.
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

        // Only one of the member's own keys: the one it joined with, or
        // one it rotated to.
        if (keyText != ev.Member && !(_keys.GetValueOrDefault(ev.Member) ?? []).Exists(f => f.Key == keyText))
        {
            return "the revoked key isn't one of the member's keys";
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
