using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// Groups (MSFP groups part): servers whose operators chose each other. A
// group is the list of signed events every member keeps (GroupState
// computes what they give); members pass events and mail to the members
// they are paired with through the group, ask each other to pair through
// it, and pair by group-pair. Groups.cs holds the views and the
// operator's actions; GroupCalls.cs what travels: receiving, mail, the
// calls, joining.

/// <summary>A group as the panel shows it.</summary>
public sealed record GroupView
{
    /// <summary>The group's ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>Its name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Its settings.</summary>
    public GroupSettings Settings { get; init; } = GroupSettings.Default;

    /// <summary>This server's role: "owner", "admin" or "member".</summary>
    public string Role { get; init; } = "";

    /// <summary>This server's member ID.</summary>
    public string Me { get; init; } = "";

    /// <summary>The owner's member ID.</summary>
    public string Owner { get; init; } = "";

    /// <summary>The heir's member ID ("" for none).</summary>
    public string Heir { get; init; } = "";

    /// <summary>The members, oldest first.</summary>
    public IReadOnlyList<GroupMemberView> Members { get; init; } = [];

    /// <summary>The banned servers.</summary>
    public IReadOnlyList<GroupBanView> Banned { get; init; } = [];

    /// <summary>Members' requests waiting for this server's operator.</summary>
    public IReadOnlyList<GroupRequestView> Requests { get; init; } = [];

    /// <summary>This server accepts members by itself once they waited the group's days ("accept members").</summary>
    public bool Accept { get; init; }

    /// <summary>The host libraries this server offers the group.</summary>
    public IReadOnlyList<string> Offers { get; init; } = [];

    /// <summary>Its local titles are shared with the group (SetGroupLocalAsync).</summary>
    public bool Local { get; init; }

    /// <summary>The pool this server lends the group.</summary>
    public Limits Pool { get; init; } = new();

    /// <summary>The group is dissolved.</summary>
    public bool Dissolved { get; init; }

    /// <summary>The list hasn't arrived from the inviter yet.</summary>
    public bool Joining { get; init; }

    /// <summary>Peers already paired with this server that used its code for the group, waiting for AcceptJoinAsync or RefuseJoinAsync.</summary>
    public IReadOnlyList<GroupJoinView> Joins { get; init; } = [];

    /// <summary>
    /// This server has no pairing through the group, and the group has other
    /// members: nothing reaches them from here, nor here from them (MSFP
    /// groups section 6, "No way in").
    /// </summary>
    public bool NoWayIn { get; init; }
}

/// <summary>A group, by its ID and name.</summary>
/// <param name="Id">The group's ID.</param>
/// <param name="Name">Its name.</param>
public sealed record GroupRef(string Id, string Name);

/// <summary>A peer waiting to join a group through this server.</summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Fingerprint">Its key's fingerprint.</param>
/// <param name="Received">When it called group-join.</param>
public sealed record GroupJoinView(string Peer, string Name, string Fingerprint, DateTimeOffset Received);

/// <summary>A member, as the panel lists it (MSFP groups section 10).</summary>
public sealed record GroupMemberView
{
    /// <summary>Its member ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>Its name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Its current key's fingerprint.</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>"owner", "admin" or "member".</summary>
    public string Role { get; init; } = "";

    /// <summary>The member that invited it ("" for the founder).</summary>
    public string InvitedBy { get; init; } = "";

    /// <summary>When it joined.</summary>
    public DateTimeOffset Joined { get; init; }

    /// <summary>The link with it; "" for none.</summary>
    public string Peer { get; init; } = "";

    /// <summary>This server refused its requests (LiftRefusalAsync undoes it).</summary>
    public bool Refused { get; init; }

    /// <summary>This server's request to it waits for its answer.</summary>
    public bool Asked { get; init; }
}

/// <summary>A banned server: its member ID, name and fingerprint as it was a member, and the ban's reason ("" for none).</summary>
/// <param name="Id">Its member ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Fingerprint">Its key's fingerprint.</param>
/// <param name="Reason">The ban's reason.</param>
public sealed record GroupBanView(string Id, string Name, string Fingerprint, string Reason);

/// <summary>A member's request waiting for this server's operator.</summary>
/// <param name="Mail">The request's mail ID, for AcceptRequestAsync and RefuseRequestAsync.</param>
/// <param name="From">The requester's member ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Fingerprint">Its key's fingerprint.</param>
/// <param name="Received">When the request arrived.</param>
/// <param name="Waited">It has been in the group the group's days.</param>
public sealed record GroupRequestView(string Mail, string From, string Name, string Fingerprint, DateTimeOffset Received, bool Waited);

public sealed partial class MsfpEngine
{
    // The calls of the part groups.
    internal const string CallGroupSync = "group-sync";
    internal const string CallGroupPush = "group-push";
    internal const string CallGroupPair = "group-pair";
    internal const string CallGroupJoin = "group-join";

    // How often a member reads the list from another (groups section 6),
    // give or take a fifth; for a server that can't be called, which is
    // never pushed to, more often.
    private static readonly TimeSpan GroupSyncEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GroupSyncUncallable = TimeSpan.FromMinutes(5);
    private const int MaxSyncEvents = 500;
    private const int MaxPushMail = 100;
    private const int MaxKeptMail = 1000;

    // How long a server waits to join through group-join, and a join waits
    // for the operator: a code works 30 days at most.
    private static readonly TimeSpan JoinWait = PairingCode.MaxLifetime;

    private static string Role(GroupState s, string member) =>
        member == s.Owner ? "owner" : s.Members.TryGetValue(member, out var m) && m.Admin ? "admin" : "member";

    // ---- reading a group ---------------------------------------------------

    private static IGroupsTx GroupsOf(IStoreTx tx) => tx as IGroupsTx ?? throw new MsfpException(MsfpError.NoGroups);

    private static async ValueTask<GroupRecord> GroupOfAsync(IStoreTx tx, string id) =>
        await GroupsOf(tx).GroupAsync(id).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);

    private static GroupEnvelope Envelope(StoredEvent e) => new() { Event = e.Event, Signature = e.Signature };

    private static GroupEnvelope Envelope(StoredMail m) => new() { Mail = m.Mail, Signature = m.Signature };

    // A group's kept events: its list, and those a former version held
    // aside (read as kept ones, groups section 3.2).
    private static IEnumerable<StoredEvent> KeptEvents(GroupRecord g) => g.Events.Concat(g.Held ?? []);

    // A stored event's ID: SHA-256 of its payload, base64url.
    private static string StoredId(StoredEvent e) => GroupEvents.Id(Base64UrlText.Decode(e.Event) ?? []);

    // The state a group's kept events give (groups section 3.5), whatever
    // order they came in.
    private static GroupState StateOf(GroupRecord g) => GroupState.Compute(g.Id, KeptEvents(g).Select(Envelope)).State;

    private static string Fingerprint(string key) =>
        Base64UrlText.Decode(key, 32) is { } k ? Keys.Fingerprint(k) : "";

    private GroupView GroupViewOf(GroupRecord g, IReadOnlyList<PeerRecord> peers)
    {
        var s = StateOf(g);
        var now = Now();
        var members = s.Members.Values.Select(m => new GroupMemberView
        {
            Id = m.Id, Name = m.Name, Fingerprint = Fingerprint(m.Key), Role = Role(s, m.Id), InvitedBy = m.InvitedBy,
            Joined = DateTimeOffset.FromUnixTimeSeconds(m.Joined), Refused = g.Refused?.Contains(m.Id) == true,
            Asked = g.Sent?.Any(x => x.To == m.Id && x.Kind == "request" && x.Expires > now) == true,
            Peer = peers.LastOrDefault(p => IsMember(m, p))?.Id ?? "",
        }).OrderBy(m => m.Joined).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        var banned = s.Banned.Keys.Select(id => s.WasBanned.TryGetValue(id, out var m)
                ? new GroupBanView(id, m.Name, Fingerprint(m.Key), s.Reasons.GetValueOrDefault(id, ""))
                : new GroupBanView(id, "", "", s.Reasons.GetValueOrDefault(id, "")))
            .OrderBy(b => b.Name + b.Id, StringComparer.Ordinal).ToList();
        var joins = (g.Joins ?? []).Select(j =>
        {
            var p = peers.FirstOrDefault(p => p.Id == j.Peer);
            return new GroupJoinView(j.Peer, p?.Name ?? "", p is null ? "" : Keys.Fingerprint(p.Key), j.Received);
        }).ToList();
        var requests = (g.Requests ?? []).Select(r => new GroupRequestView(r.Mail, r.From, r.Name ?? "",
            Fingerprint(s.Members.TryGetValue(r.From, out var m) ? m.Key : r.From), r.Received, Waited(s, r.From))).ToList();
        return new GroupView
        {
            Id = g.Id, Name = s.Name, Settings = s.Settings, Me = g.Me, Owner = s.Owner, Heir = s.Heir, Role = Role(s, g.Me),
            Accept = g.Accept, Offers = [.. g.Offers ?? []], Local = g.Local, Pool = g.Pool with { }, Dissolved = s.Dissolved,
            Joining = !s.Members.ContainsKey(g.Me) && g.Events.Count == 0,
            Members = members, Banned = banned, Joins = joins, Requests = requests,
            NoWayIn = CutOff(g, s, peers, ""),
        };
    }

    // Whether this server has a pairing through the group, but for the peer
    // except: the links events and mail travel on (groups section 6).
    private bool WayIn(string id, IEnumerable<PeerRecord> peers, string except) =>
        peers.Any(p => p.Id != except && p.State == PeerState.Paired && p.Groups?.Contains(id) == true && On(p, Versions.PartGroups));

    // Whether this server, a member of the group with others in it, has no
    // way in (without the peer except).
    private bool CutOff(GroupRecord g, GroupState s, IEnumerable<PeerRecord> peers, string except) =>
        !s.Dissolved && s.Members.ContainsKey(g.Me) && s.Members.Count >= 2 && !WayIn(g.Id, peers, except);

    /// <summary>
    /// The groups that ending the pairing with this peer would leave this
    /// server with no way in: the panel says so before it ends (MSFP groups
    /// section 6, "No way in"). Empty when the store keeps no groups.
    /// </summary>
    public async Task<IReadOnlyList<GroupRef>> GroupsCutByAsync(string peer, CancellationToken ct = default)
    {
        List<GroupRef> output = [];
        await _store.ReadAsync(async tx =>
        {
            if (tx is not IGroupsTx groups)
            {
                return;
            }

            var peers = await tx.PeersAsync().ConfigureAwait(false);
            foreach (var g in await groups.GroupsAsync().ConfigureAwait(false))
            {
                var s = StateOf(g);
                if (WayIn(g.Id, peers, "") && CutOff(g, s, peers, peer))
                {
                    output.Add(new GroupRef(g.Id, s.Name));
                }
            }
        }, ct).ConfigureAwait(false);
        return output;
    }

    // A member automatically accepted is accepted at once: the group's
    // waitDays changes nothing (groups section 4.1).
    private static bool Waited(GroupState s, string member) => s.Members.ContainsKey(member);

    /// <summary>The groups this server is in.</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NoGroups"/>: the store keeps none.</exception>
    public async Task<IReadOnlyList<GroupView>> GroupsAsync(CancellationToken ct = default)
    {
        List<GroupView> output = [];
        await _store.ReadAsync(async tx =>
        {
            var gs = await GroupsOf(tx).GroupsAsync().ConfigureAwait(false);
            var peers = await tx.PeersAsync().ConfigureAwait(false);
            output = gs.Select(g => GroupViewOf(g, peers)).ToList();
        }, ct).ConfigureAwait(false);
        return output;
    }

    /// <summary>One group.</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NoGroups"/>.</exception>
    public async Task<GroupView> GroupAsync(string id, CancellationToken ct = default)
    {
        GroupView? output = null;
        await _store.ReadAsync(async tx =>
        {
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            output = GroupViewOf(g, await tx.PeersAsync().ConfigureAwait(false));
        }, ct).ConfigureAwait(false);
        return output!;
    }

    // Reads a group and its state.
    private async Task<T> WithStateAsync<T>(string id, Func<GroupRecord, GroupState, T> fn, CancellationToken ct)
    {
        T output = default!;
        await _store.ReadAsync(async tx =>
        {
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            output = fn(g, StateOf(g));
        }, ct).ConfigureAwait(false);
        return output;
    }

    // ---- signing this server's events ------------------------------------

    // Makes this server's next event in a group, from its state: its seq,
    // and ownerSeq or asOf as its role asks. It must count with the events
    // kept, or it isn't sent: NoRight. Gives the event and the state with it.
    private (StoredEvent Event, GroupState State) SignEvent(GroupRecord g, GroupState s, GroupEvent ev)
    {
        var key = Current();
        var now = Now();
        ev = ev with
        {
            Group = ev.Type == GroupEvents.Founded ? null : g.Id, Signer = Base64UrlText.Encode(key.Public()), Member = g.Me,
            // Never a seq it signed already, counted or not.
            Seq = s.Highest(g.Me) + 1, Date = now.ToUnixTimeSeconds(),
        };
        if (s.Owner == g.Me && ev.Type is not (GroupEvents.Profile or GroupEvents.Left or GroupEvents.Rotated or GroupEvents.Revoked))
        {
            ev = ev with { OwnerSeq = s.OwnerSeq + 1 };
        }
        else if (ev.Type is GroupEvents.Joined or GroupEvents.Banned)
        {
            ev = ev with { AsOf = s.OwnerSeq };
        }

        var (env, id) = GroupEvents.Sign(key, ev);
        var (next, results) = GroupState.Compute(ev.Type == GroupEvents.Founded ? id : g.Id, KeptEvents(g).Select(Envelope).Append(env));
        if (results.GetValueOrDefault(id) is not { Verdict: GroupVerdict.Counted })
        {
            throw new MsfpException(MsfpError.NoRight, results.GetValueOrDefault(id)?.Reason ?? "");
        }

        return (new StoredEvent { Event = env.Event, Signature = env.Signature, Received = now }, next);
    }

    // Signs one event of this server in a group, keeps it, and passes it
    // on. Who is told: the members it is paired with through the group now,
    // fixed before the event changes who they are; a banned server too
    // (groups section 7). What the event changes beyond the list runs once
    // it is committed. Errors: NotFound, NotMember, NoRight.
    private async Task ActAsync(string id, GroupEvent ev, CancellationToken ct)
    {
        var to = await GroupPeersAsync(id, "", ct).ConfigureAwait(false);
        PeerRecord? banned = null;
        if (ev.Type == GroupEvents.Banned && Base64UrlText.Decode(ev.Target, 32) is { } target)
        {
            await _store.ReadAsync(async tx => banned = await tx.PeerByKeyAsync(target).ConfigureAwait(false), ct).ConfigureAwait(false);
            if (banned is not null)
            {
                to.RemoveAll(p => p.Id == banned.Id);
            }
        }

        StoredEvent? sent = null;
        var effects = new List<Action>();
        await _store.UpdateAsync(async tx =>
        {
            effects.Clear();
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            var s = StateOf(g);
            if (!s.Members.ContainsKey(g.Me))
            {
                throw new MsfpException(MsfpError.NotMember);
            }

            (sent, s) = SignEvent(g, s, ev);
            g.Events.Add(sent);
            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
            await AfterEventsAsync(tx, g, s, effects).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (banned is not null && banned.Inbound == Inbound.Yes)
        {
            // The banned server hears of it before its link here ends, which
            // it does when nothing else holds it (core section 7.7).
            try
            {
                await CallAsync(banned, CallGroupPush, PushBody(id, [sent!], []), ct, background: true).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogDebug(e, "msfp: the banned server couldn't be told: {Peer}", banned.Id);
            }
        }

        PushTo(to, id, [sent!], []);
        foreach (var f in effects)
        {
            f();
        }
    }

    // ---- the operator's actions ------------------------------------------

    /// <summary>Founds a group, this server its owner, with a name and settings (null: the defaults).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.CodeOptions"/> (no name, or settings out of range), <see cref="MsfpError.NoGroups"/>.</exception>
    public async Task<GroupView> CreateGroupAsync(string name, GroupSettings? settings = null, CancellationToken ct = default)
    {
        settings ??= GroupSettings.Default;
        name = Music.CleanText(name ?? "", PairingCode.MaxGroupName);
        if (!settings.Valid || name.Length == 0)
        {
            throw new MsfpException(MsfpError.CodeOptions);
        }

        var g = new GroupRecord { Me = Base64UrlText.Encode(Current().Public()), Pool = _cfg.NewPairing! with { } };
        await NewMailKeyAsync(g, ct).ConfigureAwait(false);
        var (founded, s) = SignEvent(g, new GroupState(), new GroupEvent
        {
            Type = GroupEvents.Founded, Name = name, Settings = settings, Nonce = Base64UrlText.Encode(RandomBytes(16)), OwnerSeq = 1,
        });
        g.Id = s.Id;
        g.Events = [founded];
        g.Events.Add(await ProfileAsync(g, s, ct).ConfigureAwait(false));
        await _store.UpdateAsync(tx => GroupsOf(tx).PutGroupAsync(g), ct).ConfigureAwait(false);
        return GroupViewOf(g, []);
    }

    // An empty record of a group, as a newcomer keeps before the list comes
    // (for the tests, which then pass it events).
    internal async Task SeedGroupAsync(string id, CancellationToken ct = default)
    {
        var g = new GroupRecord { Id = id, Me = Base64UrlText.Encode(Current().Public()), Pool = _cfg.NewPairing! with { } };
        await NewMailKeyAsync(g, ct).ConfigureAwait(false);
        await _store.UpdateAsync(async tx => await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    // A new X25519 key pair for a group's mail, its private part sealed in g.
    private async Task NewMailKeyAsync(GroupRecord g, CancellationToken ct)
    {
        var (priv, _) = GroupEvents.MailKeyPair(RandomBytes(32));
        g.MailKey = await _sealer.SealAsync(priv, SealContext.MailKey, ct).ConfigureAwait(false);
    }

    // This server's profile: its name, mail key and next key commitment.
    private async Task<StoredEvent> ProfileAsync(GroupRecord g, GroupState s, CancellationToken ct)
    {
        var priv = await _sealer.OpenAsync(g.MailKey, SealContext.MailKey, ct).ConfigureAwait(false);
        var (_, pub) = GroupEvents.MailKeyPair(priv);
        return SignEvent(g, s, new GroupEvent
        {
            Type = GroupEvents.Profile, Name = Name(), EncryptionKey = Base64UrlText.Encode(pub),
            NextCommitment = Base64UrlText.Encode(OurKeys().Commitment),
        }).Event;
    }

    // A group's ID and name for a code inviting to it: this server must be
    // its owner or an admin.
    private Task<(byte[] Id, string Name)> GroupForCodeAsync(string id, CancellationToken ct)
    {
        var gid = Base64UrlText.Decode(id, 32) ?? throw new MsfpException(MsfpError.NotFound);
        return WithStateAsync(id, (g, s) =>
            s.Dissolved || !s.IsAdmin(g.Me) ? throw new MsfpException(MsfpError.NoRight) : (gid, s.Name), ct);
    }

    /// <summary>Chooses how this server answers requests in a group: accept by itself once a member waited the group's days, or ask.</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NoGroups"/>.</exception>
    public Task SetGroupAnswerAsync(string id, bool accept, CancellationToken ct = default) =>
        UpdateGroupAsync(id, g => g.Accept = accept, ct);

    /// <summary>The host's libraries, those offered to a group marked Offered (as OffersAsync does for a peer).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NoGroups"/>.</exception>
    public async Task<IReadOnlyList<Library>> GroupOffersAsync(string id, CancellationToken ct = default)
    {
        var g = await GroupAsync(id, ct).ConfigureAwait(false);
        var libs = await HostLibrariesAsync(ct).ConfigureAwait(false);
        return libs.Select(l => l with { Offered = g.Offers.Contains(l.Id) }).ToList();
    }

    /// <summary>
    /// Sets the host libraries this server offers a group (MSFP groups
    /// section 9; IDs that aren't the host's are left out): every link
    /// through it is offered them, and their playbacks from libraries no
    /// longer offered end.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NoGroups"/>.</exception>
    public async Task SetGroupOffersAsync(string id, IEnumerable<string> libraries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var asked = libraries.ToHashSet(StringComparer.Ordinal);
        var ours = await HostLibrariesAsync(ct).ConfigureAwait(false);
        var keep = ours.Select(l => l.Id).Where(asked.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var mark = await SeqMarkAsync(ct).ConfigureAwait(false);
        await _store.UpdateAsync(async tx =>
        {
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            await PublicIdsAsync(tx, keep).ConfigureAwait(false);
            g.Offers = keep;
            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
            await RefreshLinksAsync(tx, mark).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        await BumpLogAsync(ct).ConfigureAwait(false);
        await EndPlaybacksNotOfferedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Chooses whether this server shares its local titles (MSFP catalog
    /// section 4.3) with a group, in the libraries it offers it: off by
    /// default. A change forces a whole reading on every link through it.
    /// </summary>
    /// <param name="id">The group's ID.</param>
    /// <param name="on">Share them.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NoGroups"/>.</exception>
    public async Task SetGroupLocalAsync(string id, bool on, CancellationToken ct = default)
    {
        var mark = await SeqMarkAsync(ct).ConfigureAwait(false);
        var changed = false;
        await _store.UpdateAsync(async tx =>
        {
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            if (!(changed = g.Local != on))
            {
                return;
            }

            g.Local = on;
            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
            await RefreshLinksAsync(tx, mark).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (changed)
        {
            await BumpLogAsync(ct).ConfigureAwait(false);
        }
    }

    // Ends the playbacks from libraries their link is no longer offered
    // (playback section 6.3).
    private async Task EndPlaybacksNotOfferedAsync(CancellationToken ct)
    {
        var offers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await _store.ReadAsync(async tx =>
        {
            foreach (var p in await tx.PeersAsync().ConfigureAwait(false))
            {
                offers[p.Id] = Offered(p);
            }
        }, ct).ConfigureAwait(false);
        EndPlaybacks(pb => offers.TryGetValue(pb.Peer, out var o) && !OfferedIn(pb.Libraries, o), EndReason.NotOffered);
    }

    /// <summary>Sets the pool this server lends a group: streams, transcodes and a bitrate cap shared by its members.</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.InvalidLimits"/>, <see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NoGroups"/>.</exception>
    public Task SetGroupPoolAsync(string id, Limits pool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        pool.Check();
        var l = pool with { };
        return UpdateGroupAsync(id, g => g.Pool = l, ct);
    }

    // Changes a group's record, and what its links get from it.
    private Task UpdateGroupAsync(string id, Action<GroupRecord> fn, CancellationToken ct) =>
        _store.UpdateAsync(async tx =>
        {
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            fn(g);
            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
            await RefreshLinksAsync(tx).ConfigureAwait(false);
        }, ct).AsTask();

    /// <summary>Names a member admin, or removes its role (the owner only).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotMember"/>, <see cref="MsfpError.NoRight"/>.</exception>
    public async Task SetAdminAsync(string id, string member, bool on, CancellationToken ct = default)
    {
        var ev = new GroupEvent { Type = GroupEvents.Admin, Target = member, On = on };
        if (!on)
        {
            ev = ev with { LastSeq = await LastSeqAsync(id, member, ct).ConfigureAwait(false) };
        }

        await ActAsync(id, ev, ct).ConfigureAwait(false);
    }

    /// <summary>Names the member that takes the group when this owner steps down (null or "" for none).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotMember"/>, <see cref="MsfpError.NoRight"/>.</exception>
    public Task SetHeirAsync(string id, string? member, CancellationToken ct = default) =>
        ActAsync(id, new GroupEvent { Type = GroupEvents.Heir, Target = string.IsNullOrEmpty(member) ? null : member }, ct);

    /// <summary>Gives the group to a member; null or "" steps down to the successor (the heir, else the oldest admin, else the oldest member).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotMember"/>, <see cref="MsfpError.NoRight"/> (nobody to give it to too).</exception>
    public async Task GiveGroupAsync(string id, string? member = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(member))
        {
            member = await WithStateAsync(id, (_, s) => s.Successor(), ct).ConfigureAwait(false);
        }

        if (member.Length == 0)
        {
            throw new MsfpException(MsfpError.NoRight, "nobody to give the group to");
        }

        await ActAsync(id, new GroupEvent { Type = GroupEvents.Owner, Target = member }, ct).ConfigureAwait(false);
    }

    /// <summary>Changes the group's name and settings (the owner only); an empty name keeps the name.</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.CodeOptions"/> (days 0 to 30, invitations 1 to 100 a day),
    /// <see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NoRight"/>.</exception>
    public Task SetGroupSettingsAsync(string id, string? name, GroupSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Valid)
        {
            throw new MsfpException(MsfpError.CodeOptions);
        }

        var clean = Music.CleanText(name ?? "", PairingCode.MaxGroupName);
        return ActAsync(id, new GroupEvent { Type = GroupEvents.Settings, Name = clean.Length > 0 ? clean : null, Settings = settings }, ct);
    }

    /// <summary>Bans a member, with an optional reason (the owner, or an admin for a member that isn't an admin).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotMember"/>, <see cref="MsfpError.NoRight"/>.</exception>
    public async Task BanAsync(string id, string member, string? reason = null, CancellationToken ct = default)
    {
        var last = await LastSeqAsync(id, member, ct).ConfigureAwait(false);
        var clean = Music.CleanText(reason ?? "", GroupEvents.MaxReason);
        await ActAsync(id, new GroupEvent { Type = GroupEvents.Banned, Target = member, LastSeq = last, Reason = clean.Length > 0 ? clean : null }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Lifts a member's ban (the owner only).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/> (no such group or ban), <see cref="MsfpError.NoRight"/>.</exception>
    public async Task UnbanAsync(string id, string member, CancellationToken ct = default)
    {
        var ban = await WithStateAsync(id, (_, s) => s.Banned.GetValueOrDefault(member) ?? throw new MsfpException(MsfpError.NotFound), ct)
            .ConfigureAwait(false);
        await ActAsync(id, new GroupEvent { Type = GroupEvents.Unbanned, Ban = ban }, ct).ConfigureAwait(false);
    }

    private Task<long> LastSeqAsync(string id, string member, CancellationToken ct) =>
        WithStateAsync(id, (_, s) => s.Members.ContainsKey(member) ? s.Seq(member) : throw new MsfpException(MsfpError.NotMember), ct);

    /// <summary>
    /// Leaves a group, or gives up joining it. Every link through it loses
    /// it as a relation; one with no relation left ends, but those in
    /// <paramref name="ask"/>, asked for the individual relation first, which
    /// stay until their operators answer (MSFP groups section 7, core section
    /// 7.7).
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotMember"/>, <see cref="MsfpError.NoRight"/> (the owner gives the group away first).</exception>
    public async Task LeaveGroupAsync(string id, IReadOnlyList<string>? ask = null, CancellationToken ct = default)
    {
        foreach (var p in ask ?? [])
        {
            try
            {
                await AskIndividualAsync(p, ct).ConfigureAwait(false);
            }
            catch (MsfpException e) when (e.Code != MsfpError.Internal)
            {
                _log.LogInformation(e, "msfp: a server couldn't be asked for the individual relation before leaving: {Peer}", p);
            }
        }

        var g = await GroupAsync(id, ct).ConfigureAwait(false);
        if (!g.Joining)
        {
            await ActAsync(id, new GroupEvent { Type = GroupEvents.Left }, ct).ConfigureAwait(false);
        }

        // Not a member yet: giving up joining (groups section 2).
        await DropGroupAsync(id, keep: false, "left", "", ct).ConfigureAwait(false);
    }

    /// <summary>Dissolves the group (the owner only): every member removes it; links it alone held end, or, with keepPairings, wait for each side's operator to keep (AskIndividualAsync) or end them (RefuseIndividualAsync).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NoRight"/>.</exception>
    public Task DissolveGroupAsync(string id, bool keepPairings, CancellationToken ct = default) =>
        ActAsync(id, new GroupEvent { Type = GroupEvents.Dissolved, KeepPairings = keepPairings }, ct);

    // Removes a group from this server: its record, and from every link's
    // relations. A link with no relation left ends, or, with keep (a
    // dissolution with keepPairings), waits for the operator's choice.
    private async Task DropGroupAsync(string id, bool keep, string why, string reason, CancellationToken ct)
    {
        var ending = new List<string>();
        GroupLeft? left = null;
        await _store.UpdateAsync(async tx =>
        {
            ending.Clear();
            left = null;
            var gt = GroupsOf(tx);
            var g = await gt.GroupAsync(id).ConfigureAwait(false);
            if (g is not null)
            {
                left = new GroupLeft(id, StateOf(g).Name, why, reason);
            }

            foreach (var p in await tx.PeersAsync().ConfigureAwait(false))
            {
                if (p.Groups?.Remove(id) != true)
                {
                    continue;
                }

                var alone = p.State == PeerState.Paired && !Holds(p, false);
                if (alone && !keep)
                {
                    ending.Add(p.Id);
                    continue;
                }

                if (alone)
                {
                    // Kept or ended by our operator; its offers and limits, if
                    // kept, are the group's until changed.
                    p.KeepChoice = true;
                    if (g is not null)
                    {
                        p.Offers = [.. g.Offers ?? []];
                        p.Local = g.Local;
                        p.Limits = g.Pool with { };
                    }
                }

                await tx.PutPeerAsync(p).ConfigureAwait(false);
            }

            await gt.DeleteGroupAsync(id).ConfigureAwait(false);
            await RefreshLinksAsync(tx).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (left is not null)
        {
            Notify(left);
        }

        foreach (var p in ending)
        {
            try
            {
                await EndAsync(p, ct).ConfigureAwait(false);
            }
            catch (MsfpException e) when (e.Code != MsfpError.NotFound)
            {
                _log.LogInformation(e, "msfp: a link through a group couldn't be ended: {Peer}", p);
            }
            catch (MsfpException)
            {
            }
        }
    }

    /// <summary>
    /// Ends the link with a member, whatever its sources, and forgets its
    /// key, so that its requests are dropped in every group (MSFP groups
    /// section 7); with invitees, the members it invited too, and theirs.
    /// Nothing goes to the group.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NoGroups"/>.</exception>
    public async Task BlockAsync(string id, string member, bool invitees = false, CancellationToken ct = default)
    {
        var keys = await WithStateAsync(id, (_, s) =>
        {
            var found = new List<byte[]>();
            var todo = new Queue<string>([member]);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (todo.TryDequeue(out var m))
            {
                if (!seen.Add(m))
                {
                    continue;
                }

                if (Base64UrlText.Decode(s.Members.TryGetValue(m, out var gm) ? gm.Key : m, 32) is { } k)
                {
                    found.Add(k);
                }

                if (invitees)
                {
                    foreach (var x in s.Members.Values.Where(x => x.InvitedBy == m))
                    {
                        todo.Enqueue(x.Id);
                    }
                }
            }

            return found;
        }, ct).ConfigureAwait(false);
        foreach (var k in keys)
        {
            string? peer = null;
            await _store.UpdateAsync(async tx =>
            {
                if (await tx.PeerByKeyAsync(k).ConfigureAwait(false) is { } p)
                {
                    peer = p.Id;
                    return;
                }

                await tx.ForgetAsync(new ForgottenKey { KeyHash = SHA256.HashData(k), At = NowSecond(), Blocked = true }).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (peer is not null)
            {
                try
                {
                    await BlockPeerAsync(peer, ct).ConfigureAwait(false);
                }
                catch (MsfpException e) when (e.Code == MsfpError.NotFound)
                {
                }
            }
        }
    }

    // ---- requests ---------------------------------------------------------

    /// <summary>Accepts a member's request: the two pair through the group (MSFP groups section 4.2).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotMember"/>, <see cref="MsfpError.Unreachable"/>
    /// and the other errors of a call.</exception>
    public async Task AcceptRequestAsync(string id, string mail, CancellationToken ct = default)
    {
        GroupRequest? r = null;
        await _store.UpdateAsync(async tx =>
        {
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            r = g.Requests?.FirstOrDefault(x => x.Mail == mail) ?? throw new MsfpException(MsfpError.NotFound);
            g.Requests!.Remove(r);
            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        await AnswerRequestAsync(id, r!, ct).ConfigureAwait(false);
    }

    /// <summary>Refuses a member's request: nothing is sent, and its requests are dropped until LiftRefusalAsync (MSFP groups section 4.1).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public Task RefuseRequestAsync(string id, string mail, CancellationToken ct = default) =>
        UpdateGroupAsync(id, g =>
        {
            if (g.Requests?.FirstOrDefault(x => x.Mail == mail) is not { } r)
            {
                return;
            }

            g.Refused ??= [];
            if (!g.Refused.Contains(r.From))
            {
                g.Refused.Add(r.From);
            }

            g.Requests.Remove(r);
        }, ct);

    /// <summary>Takes a member off this server's refusals.</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public Task LiftRefusalAsync(string id, string member, CancellationToken ct = default) =>
        UpdateGroupAsync(id, g => g.Refused?.RemoveAll(x => x == member), ct);

    /// <summary>Sends a member this server's request, as a newcomer does to every member (MSFP groups section 4).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>; <see cref="MsfpError.NoWayIn"/>: this server has no pairing through the group.</exception>
    public Task RequestMemberAsync(string id, string member, CancellationToken ct = default) =>
        SendRequestsAsync(id, [member], ct);

    // Makes the link a request asks for: group-pair at the requester's
    // address, or an answer mail when it can't be called.
    private Task AnswerRequestAsync(string id, GroupRequest r, CancellationToken ct) =>
        r.Inbound
            ? GroupPairAsync(id, r.From, r.Mail, r.Address, r.NextCommitment ?? [], true, r.Pins ?? [], ct)
            : SendMailAsync(id, r.From, "answer", r.Mail, ct);

    // Sends this server's request to members (all those it isn't linked
    // with, for null) that have a profile and no live mail from it.
    private async Task SendRequestsAsync(string id, IReadOnlyList<string>? to, CancellationToken ct)
    {
        var targets = new List<string>();
        var now = Now();
        await _store.ReadAsync(async tx =>
        {
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            var s = StateOf(g);
            // Asked for by the operator: no way in is said first, whatever
            // else would skip the member (groups section 6).
            if (to is not null && !WayIn(id, await tx.PeersAsync().ConfigureAwait(false), ""))
            {
                throw new MsfpException(MsfpError.NoWayIn);
            }

            foreach (var m in s.Members.Values)
            {
                if (m.Id == g.Me || (to is not null && !to.Contains(m.Id)) || m.EncryptionKey is null ||
                    Base64UrlText.Decode(m.Key, 32) is not { } key)
                {
                    continue;
                }

                if (await tx.PeerByKeyAsync(key).ConfigureAwait(false) is not null ||
                    g.Sent?.Any(x => x.To == m.Id && x.Expires > now) == true)
                {
                    continue;
                }

                targets.Add(m.Id);
            }
        }, ct).ConfigureAwait(false);
        _log.LogDebug("msfp: group requests: {Group} to {Count}", id, targets.Count);
        foreach (var m in targets)
        {
            try
            {
                await SendMailAsync(id, m, "request", null, ct).ConfigureAwait(false);
            }
            catch (MsfpException e) when (to is null)
            {
                // Asked for by the operator (to set), it goes up: the panel says why.
                _log.LogInformation(e, "msfp: a group request couldn't be made: {Group}", id);
            }
        }
    }

    // Seals and sends one mail to a member: a request, or an answer to one.
    private async Task SendMailAsync(string id, string to, string kind, string? answers, CancellationToken ct)
    {
        List<string> pins = SaysUncallable() ? [] : await OwnPinsAsync(false, ct).ConfigureAwait(false);
        StoredMail? env = null;
        await _store.UpdateAsync(async tx =>
        {
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            var s = StateOf(g);
            if (!s.Members.TryGetValue(to, out var m) || m.EncryptionKey is null)
            {
                throw new MsfpException(MsfpError.NotMember);
            }

            if (!WayIn(id, await tx.PeersAsync().ConfigureAwait(false), ""))
            {
                throw new MsfpException(MsfpError.NoWayIn);
            }

            var mid = GroupEvents.MailId(RandomBytes(16));
            var expires = Now() + GroupEvents.MailLifetime;
            var mail = new GroupMail
            {
                Group = id, Id = mid, Kind = kind, From = g.Me, To = to, Answers = answers, Expires = expires.ToUnixTimeSeconds(),
            };
            var content = new SealedContent
            {
                Address = Named(), Name = Name(), NextCommitment = Base64UrlText.Encode(OurKeys().Commitment),
                Inbound = !SaysUncallable(), Certificates = pins.Count > 0 ? pins : null,
            };
            var sealedMail = GroupEvents.SealMail(Current(), mail, m.EncryptionKey, content, _random);
            env = new StoredMail { Id = mid, Mail = sealedMail.Mail, Signature = sealedMail.Signature, Expires = expires.ToUnixTimeSeconds() };
            (g.Mail ??= []).Add(env);
            (g.Sent ??= []).Add(new SentMail { Id = mid, To = to, Kind = kind, Expires = DateTimeOffset.FromUnixTimeSeconds(expires.ToUnixTimeSeconds()) });
            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        await PushSoonAsync(id, [], [env!], "", ct).ConfigureAwait(false);
    }

    // ---- what kept events change (groups section 7) -------------------------

    // Applies what kept events change beyond the list: bans, a dissolution,
    // members gone, roles and offers, in tx, the transaction they were kept
    // in. Effects are run once it is committed.
    private async ValueTask AfterEventsAsync(IStoreTx tx, GroupRecord g, GroupState s, List<Action> effects)
    {
        var id = g.Id;
        if (s.Dissolved)
        {
            var keep = s.Keep;
            effects.Add(() => Background(ct => DropGroupAsync(id, keep, "dissolved", "", ct)));
            return;
        }

        if (s.Banned.ContainsKey(g.Me))
        {
            var reason = s.Reasons.GetValueOrDefault(g.Me, "");
            _log.LogInformation("msfp: this server was banned from a group: {Group} {Reason}", id, reason);
            effects.Add(() => Background(ct => DropGroupAsync(id, false, "banned", reason, ct)));
            return;
        }

        // Links with servers no longer members lose this group as a source.
        foreach (var p in await tx.PeersAsync().ConfigureAwait(false))
        {
            if (p.Groups?.Contains(id) != true || s.Members.Values.Any(m => IsMember(m, p)))
            {
                continue;
            }

            p.Groups.Remove(id);
            if (p.State == PeerState.Paired && !Holds(p, false))
            {
                // Banned, or left: a link nothing else holds ends (core
                // section 7.7); one the leaver asked to keep is held by its ask.
                var pid = p.Id;
                effects.Add(() => Background(ct => EndAsync(pid, ct)));
            }

            await tx.PutPeerAsync(p).ConfigureAwait(false);
        }

        await RefreshLinksAsync(tx).ConfigureAwait(false);
    }

    // Copies onto every link what its groups give it: their offers and
    // pools (groups section 9), and moves its offers' change number when
    // they change; mark is the change number the offers were set after
    // (SeqMarkAsync), the highest seen by default.
    private async ValueTask RefreshLinksAsync(IStoreTx tx, long? mark = null)
    {
        if (tx is not IGroupsTx gt)
        {
            return;
        }

        var byId = (await gt.GroupsAsync().ConfigureAwait(false)).ToDictionary(g => g.Id, StringComparer.Ordinal);
        var at = mark ?? Interlocked.Read(ref _seqSeen);
        foreach (var p in await tx.PeersAsync().ConfigureAwait(false))
        {
            var offers = new List<string>();
            var local = new List<string>();
            var pools = new List<GroupPool>();
            foreach (var id in p.Groups ?? [])
            {
                if (!byId.TryGetValue(id, out var g))
                {
                    continue;
                }

                offers.AddRange((g.Offers ?? []).Where(l => !offers.Contains(l)));
                if (g.Local)
                {
                    local.AddRange((g.Offers ?? []).Where(l => !local.Contains(l)));
                }

                pools.Add(new GroupPool { Group = id, Limits = g.Pool });
            }

            offers.Sort(StringComparer.Ordinal);
            local.Sort(StringComparer.Ordinal);
            // What it is offered, local titles included: a whole reading
            // follows (MSFP catalog section 5).
            var offersChanged = !offers.SequenceEqual(p.GroupOffers ?? []) || !local.SequenceEqual(p.GroupLocal ?? []);
            if (!offersChanged && pools.SequenceEqual(p.Pools ?? []))
            {
                continue;
            }

            p.GroupOffers = offers.Count > 0 ? offers : null;
            p.GroupLocal = local.Count > 0 ? local : null;
            p.Pools = pools.Count > 0 ? pools : null;
            if (offersChanged)
            {
                p.OffersSeq = await OffersSeqAsync(tx, at).ConfigureAwait(false);
            }

            await tx.PutPeerAsync(p).ConfigureAwait(false);
        }
    }

    // ---- what a link is given (groups section 9) ---------------------------

    // The host libraries a link is offered: by hand, and through its groups
    // (their union).
    internal static List<string> Offered(PeerRecord p)
    {
        var output = new List<string>(p.GroupOffers ?? []);
        if (Individual(p))
        {
            output.AddRange((p.Offers ?? []).Where(l => !output.Contains(l)));
        }

        return output;
    }

    // The host libraries whose local titles a link is shared: those its
    // individual relation offers when it shares local titles, and those of
    // its groups that do (catalog section 5, groups section 9: a relation
    // that allows it, and offers the library).
    internal static List<string> LocalOffered(PeerRecord p)
    {
        var output = new List<string>(p.GroupLocal ?? []);
        if (Individual(p) && p.Local)
        {
            output.AddRange((p.Offers ?? []).Where(l => !output.Contains(l)));
        }

        return output;
    }

    // The pools a link's playbacks may count against, in order: its own
    // limits when paired by hand, then its groups'.
    private static List<GroupPool> PoolsOf(PeerRecord p)
    {
        var output = new List<GroupPool>();
        if (Individual(p))
        {
            output.Add(new GroupPool { Group = "", Limits = p.Limits });
        }

        output.AddRange(p.Pools ?? []);
        return output;
    }

    // The key a pool's playbacks are counted under.
    private static string PoolKey(PeerRecord p, GroupPool pool) => pool.Group.Length == 0 ? p.Id : "group:" + pool.Group;

    // Whether one of a link's pools has room now for a playback of purpose
    // (Room), and whether one allows any at all (Open).
    private (bool Room, bool Open) PoolRoom(PeerRecord p, string purpose, bool audio)
    {
        bool room = false, open = false;
        foreach (var pool in PoolsOf(p))
        {
            var (r, o) = RoomIn(PoolKey(p, pool), pool.Limits, purpose, audio);
            room |= r;
            open |= o;
        }

        return (room, open);
    }

    // Admits a playback of purpose in the first of a link's pools with room
    // (groups section 9, playback section 5): each playback counts against
    // exactly one, and a day's counts are kept in the pool it counted
    // against.
    private CountSlot AdmitPools(PeerRecord p, string purpose, bool audio, string method)
    {
        var error = new MsfpException(MsfpError.StreamLimitReached);
        foreach (var pool in PoolsOf(p))
        {
            try
            {
                return AdmitIn(PoolKey(p, pool), pool.Limits, purpose, audio, method);
            }
            catch (MsfpException e) when (e.Code is MsfpError.StreamLimitReached or MsfpError.ServerBusy)
            {
                error = e;
            }
        }

        throw error;
    }

    // The most generous of a link's pools (what libraries tells it, groups
    // section 9): the most streams, transcodes, downloads and scans, and the
    // highest caps (none, 0, above all; a daily download cap only among pools
    // that allow downloads).
    internal static Limits Generous(PeerRecord p)
    {
        var ps = PoolsOf(p);
        if (ps.Count == 0)
        {
            return new Limits();
        }

        static int Most(int a, int b) => a == 0 || b == 0 ? 0 : Math.Max(a, b);
        var output = ps[0].Limits with { };
        foreach (var l in ps.Skip(1).Select(x => x.Limits))
        {
            output.Streams = Math.Max(output.Streams, l.Streams);
            output.Transcodes = Math.Max(output.Transcodes, l.Transcodes);
            output.AudioStreams = Math.Max(output.AudioStreams, l.AudioStreams);
            output.AudioTranscodes = Math.Max(output.AudioTranscodes, l.AudioTranscodes);
            output.Audio |= l.Audio;
            output.BitrateKbps = Most(output.BitrateKbps, l.BitrateKbps);
            output.DownloadKbps = Most(output.DownloadKbps, l.DownloadKbps);
            if (l.Downloads != 0)
            {
                output.DownloadsPerDay = output.Downloads == 0 ? l.DownloadsPerDay : Most(output.DownloadsPerDay, l.DownloadsPerDay);
            }

            output.Downloads = Math.Max(output.Downloads, l.Downloads);
            output.ScansPerDay = Math.Max(output.ScansPerDay, l.ScansPerDay);
            output.ScanBytes = Math.Max(output.ScanBytes, l.ScanBytes);
        }

        return output;
    }

    // Whether a peer is this member: its current key, its member ID, or,
    // while either side is between keys, the peer's previous key (a
    // rotation the copy hasn't kept yet, or the peer record has).
    private static bool IsMember(GroupMember m, PeerRecord p)
    {
        foreach (var k in new[] { p.Key, p.PreviousKey })
        {
            if (k is { Length: > 0 } && Base64UrlText.Encode(k) is var e && (e == m.Key || e == m.Id))
            {
                return true;
            }
        }

        return false;
    }
}
