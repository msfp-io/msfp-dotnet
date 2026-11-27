using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// Groups, what travels (MSFP groups sections 4 to 8): events and mail
// received and passed on, the calls group-sync, group-push, group-pair and
// group-join, joining, and what a change of this server's key tells its
// groups.
public sealed partial class MsfpEngine
{
    private static readonly string[] GroupSyncMembers = ["group", "have", "mail"];
    private static readonly string[] GroupPushMembers = ["group", "events", "mail"];
    private static readonly string[] GroupPairMembers = ["group", "answers", "address", "nextCommitment", "inbound"];
    private static readonly string[] GroupJoinMembers = ["secretHash", "proof"];

    // A mail ID already used by a group-pair: its 409.
    private sealed class MailUsedException() : Exception("msfp: a mail already used");

    // ---- receiving events and mail ------------------------------------------

    // Keeps what a member passed on: events (in any order, counted or not:
    // the state is their replay) and mail; what was new is passed on to the
    // others, but from.
    internal async Task ReceiveAsync(string id, IReadOnlyList<GroupEnvelope> events, IReadOnlyList<GroupEnvelope> mail, string from, CancellationToken ct)
    {
        var newEvents = new List<StoredEvent>();
        var newMail = new List<StoredMail>();
        var effects = new List<Action>();
        var told = new List<MsfpEvent>();
        await _store.UpdateAsync(async tx =>
        {
            newEvents.Clear();
            newMail.Clear();
            effects.Clear();
            told.Clear();
            var types = new List<string>();
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            var wasMember = StateOf(g).Members.ContainsKey(g.Me);
            var now = Now();

            // Every event that passes section 3.2's checks is kept and
            // passed on, counted or not: the state is what they all give,
            // whatever order they came in (groups section 3.5).
            var all = KeptEvents(g).ToList();
            var known = all.Select(StoredId).ToHashSet(StringComparer.Ordinal);
            var added = new List<StoredEvent>();
            foreach (var e in events)
            {
                if (GroupEvents.Check(e, id, now) is { Kept: true } c && known.Add(c.Id))
                {
                    added.Add(new StoredEvent { Event = e.Event, Signature = e.Signature, Received = now });
                }
            }

            all.AddRange(added);
            var (s, results) = GroupState.Compute(id, all.Select(Envelope));
            GroupVerdict? VerdictOf(StoredEvent e) => results.GetValueOrDefault(StoredId(e))?.Verdict;

            // Dropped: events not their member's, and those that waited
            // too long for what they need.
            g.Events = all.Where(e => VerdictOf(e) != GroupVerdict.Forged &&
                !(VerdictOf(e) == GroupVerdict.Pending && now - e.Received >= GroupEvents.HeldFor)).ToList();
            g.Held = null;
            foreach (var e in added)
            {
                var r = results.GetValueOrDefault(StoredId(e));
                if (r?.Verdict == GroupVerdict.Forged)
                {
                    continue;
                }

                newEvents.Add(e);
                if (r?.Verdict == GroupVerdict.Counted && !types.Contains(r.Event.Type))
                {
                    types.Add(r.Event.Type);
                }
            }
            foreach (var env in mail)
            {
                if (KeepMail(g, s, env) is { } m)
                {
                    newMail.Add(m);
                }
            }

            if (g.Mail is { } kept)
            {
                kept.RemoveAll(m => m.Expires < now.ToUnixTimeSeconds());
                if (kept.Count > MaxKeptMail)
                {
                    // The soonest to expire go first (groups section 5).
                    g.Mail = kept.OrderByDescending(m => m.Expires).Take(MaxKeptMail).ToList();
                }
            }

            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
            await AfterEventsAsync(tx, g, s, effects).ConfigureAwait(false);
            if (types.Count > 0 && wasMember)
            {
                told.Add(new GroupChanged(id, s.Name, types));
            }

            if (!wasMember && s.Members.ContainsKey(g.Me))
            {
                told.Add(new GroupJoined(id, s.Name));
                // The list says this server joined: its profile, then its
                // requests (groups section 4, steps 5 and 6).
                effects.Add(() => Background(c => JoinedAsync(id, c)));
            }

            foreach (var m in newMail)
            {
                if (MailTo(g, s, m) is { } mine)
                {
                    effects.Add(() => Background(c => GotMailAsync(id, mine, c)));
                }
            }
        }, ct).ConfigureAwait(false);
        Notify([.. told]);
        foreach (var f in effects)
        {
            f();
        }

        if (newEvents.Count > 0 || newMail.Count > 0)
        {
            await PushSoonAsync(id, newEvents, newMail, from, ct).ConfigureAwait(false);
        }
    }

    // Checks a mail and keeps it when it is new (groups section 5): from a
    // member, to a member, signed by the sender's current key, not expired.
    private StoredMail? KeepMail(GroupRecord g, GroupState s, GroupEnvelope env)
    {
        if (GroupEvents.PeekMail(env) is not { } head)
        {
            return null;
        }

        var now = Now();
        if (!s.Members.TryGetValue(head.From, out var from) || head.Expires < now.ToUnixTimeSeconds() ||
            head.Expires > (now + GroupEvents.MailLifetime + TimeSpan.FromHours(1)).ToUnixTimeSeconds())
        {
            _log.LogDebug("msfp: a mail from a non-member or out of date: {From}", head.From);
            return null;
        }

        if (g.Mail?.Any(m => m.Id == head.Id) == true)
        {
            return null;
        }

        if (Base64UrlText.Decode(from.Key, 32) is not { } key || GroupEvents.ReadMail(env, key) is not { } m ||
            m.Group != g.Id || !s.Members.ContainsKey(m.To))
        {
            _log.LogDebug("msfp: a mail refused");
            return null;
        }

        var sm = new StoredMail { Id = m.Id, Mail = env.Mail, Signature = env.Signature, Expires = m.Expires };
        (g.Mail ??= []).Add(sm);
        return sm;
    }

    // A kept mail read, when it is to this server; null otherwise.
    private static GroupMail? MailTo(GroupRecord g, GroupState s, StoredMail sm)
    {
        var env = Envelope(sm);
        if (GroupEvents.PeekMail(env) is not { } head || !s.Members.TryGetValue(head.From, out var from) ||
            Base64UrlText.Decode(from.Key, 32) is not { } key)
        {
            return null;
        }

        return GroupEvents.ReadMail(env, key) is { } m && m.To == g.Me ? m : null;
    }

    // A mail to this server: a request waits for the operator, or is
    // accepted by the server itself; an answer leads to group-pair with the
    // member that accepted (groups section 4).
    private async Task GotMailAsync(string id, GroupMail m, CancellationToken ct)
    {
        _log.LogDebug("msfp: a mail to this server: {Kind}", m.Kind);
        SealedContent? opened = null;
        var accept = false;
        GroupRequest? req = null;
        GroupRequestWaiting? waiting = null;
        try
        {
            await _store.UpdateAsync(async tx =>
            {
                waiting = null;
                accept = false;
                req = null;
                var gt = GroupsOf(tx);
                var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
                var s = StateOf(g);
                var priv = await _sealer.OpenAsync(g.MailKey, SealContext.MailKey, ct).ConfigureAwait(false);
                opened = GroupEvents.OpenMail(m, priv);
                if (opened is null || !Address.Valid(opened.Address))
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                if (m.Kind != "request")
                {
                    return;
                }

                if (!s.Members.TryGetValue(m.From, out var from) || Base64UrlText.Decode(from.Key, 32) is not { } key)
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                if (await tx.ForgottenAsync(SHA256.HashData(key)).ConfigureAwait(false) is { Blocked: true } || g.Refused?.Contains(m.From) == true)
                {
                    throw new MsfpException(MsfpError.NotFound); // blocked or refused: dropped (MSFP core section 7.4)
                }

                if (await tx.PeerByKeyAsync(key).ConfigureAwait(false) is { } p)
                {
                    if (p.Groups?.Contains(id) != true)
                    {
                        // Already linked: the group is one more source.
                        (p.Groups ??= []).Add(id);
                        await tx.PutPeerAsync(p).ConfigureAwait(false);
                        await RefreshLinksAsync(tx).ConfigureAwait(false);
                    }

                    return;
                }

                var pins = Pins.Clean(opened.Certificates ?? []);
                req = new GroupRequest
                {
                    // The mail carries no name: the member's profile's (groups section 5).
                    Mail = m.Id, From = m.From, Name = Music.CleanText(s.Members.GetValueOrDefault(m.From)?.Name ?? "", PairingCode.MaxGroupName),
                    Address = opened.Address,
                    NextCommitment = Base64UrlText.Decode(opened.NextCommitment, 32), Inbound = opened.Inbound,
                    Pins = pins.Count > 0 ? pins : null, Received = Now(),
                };
                if (g.Accept && Waited(s, m.From))
                {
                    accept = true;
                    return;
                }

                if (g.Requests?.Any(r => r.Mail == m.Id) != true)
                {
                    (g.Requests ??= []).Add(req);
                    waiting = new GroupRequestWaiting(id, s.Name, m.Id, m.From, req.Name, Keys.Fingerprint(key));
                }

                await gt.PutGroupAsync(g).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
            return;
        }

        if (waiting is not null)
        {
            Notify(waiting);
        }

        try
        {
            if (m.Kind == "request" && accept && req is not null)
            {
                await AnswerRequestAsync(id, req, ct).ConfigureAwait(false);
            }
            else if (m.Kind == "answer" && opened is not null)
            {
                await GroupPairAsync(id, m.From, m.Id, opened.Address, Base64UrlText.Decode(opened.NextCommitment, 32) ?? [], opened.Inbound,
                    Pins.Clean(opened.Certificates ?? []), ct).ConfigureAwait(false);
            }
        }
        catch (MsfpException e)
        {
            _log.LogInformation(e, "msfp: a group's mail couldn't be acted on: {Group} {Kind}", id, m.Kind);
        }
    }

    // ---- passing on (groups section 6) ---------------------------------------

    // The members of a group this server is paired with through it, but except.
    private async Task<List<PeerRecord>> GroupPeersAsync(string id, string except, CancellationToken ct)
    {
        var output = new List<PeerRecord>();
        await _store.ReadAsync(async tx =>
            output = (await tx.PeersAsync().ConfigureAwait(false))
                .Where(p => p.State == PeerState.Paired && p.Id != except && p.Groups?.Contains(id) == true && On(p, Versions.PartGroups))
                .ToList(), ct).ConfigureAwait(false);
        return output;
    }

    // Passes new events and mail on to the group's paired members, in the background.
    private async Task PushSoonAsync(string id, IReadOnlyList<StoredEvent> events, IReadOnlyList<StoredMail> mail, string except, CancellationToken ct)
    {
        if (events.Count == 0 && mail.Count == 0)
        {
            return;
        }

        PushTo(await GroupPeersAsync(id, except, ct).ConfigureAwait(false), id, events, mail);
    }

    private static byte[] PushBody(string id, IEnumerable<StoredEvent> events, IEnumerable<StoredMail> mail)
    {
        var evs = events.ToList();
        var ms = mail.ToList();
        return JsonOut.Object(o => o.String("group", id)
            .Objects("events", evs.Count > 0 ? evs : null, (w, e) => Envelope(e).WriteTo(w))
            .Objects("mail", ms.Count > 0 ? ms : null, (w, m) => Envelope(m).WriteTo(w)));
    }

    // Pushes events and mail to these peers, in the background; a peer
    // that can't be called is never pushed to (MSFP core section 7.5).
    private void PushTo(List<PeerRecord> to, string id, IReadOnlyList<StoredEvent> events, IReadOnlyList<StoredMail> mail)
    {
        if (events.Count == 0 && mail.Count == 0)
        {
            return;
        }

        var body = PushBody(id, events, mail);
        Background(async ct =>
        {
            foreach (var p in to.Where(p => p.Inbound == Inbound.Yes))
            {
                try
                {
                    await CallAsync(p, CallGroupPush, body, ct, background: true).ConfigureAwait(false);
                }
                catch (MsfpException e)
                {
                    _log.LogDebug(e, "msfp: a group push failed: {Peer}", p.Id);
                }
            }
        });
    }

    // Pushes a group's whole list and mail to one peer: the inviter to its
    // newcomer, once paired (groups section 4, step 4).
    private async Task PushAllAsync(string id, string peer, CancellationToken ct)
    {
        List<StoredEvent> events = [];
        List<StoredMail> mail = [];
        PeerRecord? p = null;
        try
        {
            await _store.ReadAsync(async tx =>
            {
                var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
                events = g.Events;
                mail = (g.Mail ?? []).Take(MaxPushMail).ToList();
                p = await tx.PeerAsync(peer).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (MsfpException)
        {
            return;
        }

        if (p is null || p.Inbound != Inbound.Yes)
        {
            return;
        }

        for (var i = 0; i < events.Count; i += MaxSyncEvents)
        {
            var body = PushBody(id, events.Skip(i).Take(MaxSyncEvents), i == 0 ? mail : []);
            try
            {
                await CallAsync(p, CallGroupPush, body, ct, background: true).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogInformation(e, "msfp: a newcomer couldn't be sent the group's list; it reads it itself: {Peer}", peer);
                return;
            }
        }
    }

    // Reads each group's list from one of its paired members, at random
    // (from the engine's loop), and accepts the requests that waited their
    // days where this server accepts members by itself.
    private Task SyncGroupsAsync(CancellationToken ct) => SyncGroupsNowAsync(false, ct);

    // SyncGroupsAsync, every group read now with force.
    internal async Task SyncGroupsNowAsync(bool force, CancellationToken ct)
    {
        IReadOnlyList<GroupRecord> groups = [];
        await _store.ReadAsync(async tx =>
        {
            if (tx is IGroupsTx gt)
            {
                groups = await gt.GroupsAsync().ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
        var every = SaysUncallable() ? GroupSyncUncallable : GroupSyncEvery;
        foreach (var g in groups)
        {
            await AcceptWaitedAsync(g.Id, ct).ConfigureAwait(false);
            var wait = every - (every / 5) + TimeSpan.FromTicks(Random.Shared.NextInt64((every / 5 * 2).Ticks));
            if (!force && g.Synced is { } synced && Now() - synced < wait)
            {
                continue;
            }

            var peers = await GroupPeersAsync(g.Id, "", ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(g.Via))
            {
                PeerRecord? via = null;
                await _store.ReadAsync(async tx => via = await tx.PeerAsync(g.Via).ConfigureAwait(false), ct).ConfigureAwait(false);
                if (via is not null && via.State == PeerState.Paired)
                {
                    peers.Add(via);
                }
            }

            if (peers.Count == 0)
            {
                continue;
            }

            var p = peers[Random.Shared.Next(peers.Count)];
            try
            {
                await SyncWithAsync(g.Id, p, ct).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogDebug(e, "msfp: a group sync failed: {Group} {Peer}", g.Id, p.Id);
            }
        }
    }

    // The member and seq of a kept event, read without checking it again.
    private static (string Member, long Seq) Head(StoredEvent e)
    {
        try
        {
            var o = StrictJson.ParseObject(Base64UrlText.Decode(e.Event) ?? [], nulls: true);
            return (o.GetString("member") ?? "", o.GetInteger("seq") ?? 0);
        }
        catch (ProtocolException)
        {
            return ("", 0);
        }
    }

    private static List<GroupEnvelope> ReadEnvelopes(JsonArray? list) =>
        list?.Select(GroupEnvelope.Read).OfType<GroupEnvelope>().ToList() ?? [];

    private static Dictionary<string, long> ReadHave(JsonObject? have)
    {
        var output = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (member, seq) in have ?? [])
        {
            if (JsonMembers.AsInteger(seq) is { } n)
            {
                output[member] = n;
            }
        }

        return output;
    }

    private static Action<JsonOut> HaveOut(Dictionary<string, long> have) => w =>
    {
        foreach (var (member, seq) in have.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            w.Integer(member, seq);
        }
    };

    // Calls group-sync on a peer for a group, keeps what it lacked, and
    // pushes it what it lacks; again while it says there is more.
    private async Task SyncWithAsync(string id, PeerRecord p, CancellationToken ct)
    {
        for (var round = 0; round < 20; round++)
        {
            Dictionary<string, long> have = [];
            List<string> mailIds = [];
            List<StoredEvent> kept = [];
            List<StoredMail> keptMail = [];
            await _store.ReadAsync(async tx =>
            {
                var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
                have = StateOf(g).Have();
                mailIds = (g.Mail ?? []).Select(m => m.Id).ToList();
                kept = g.Events;
                keptMail = g.Mail ?? [];
            }, ct).ConfigureAwait(false);
            var body = JsonOut.Object(o => o.String("group", id).Object("have", HaveOut(have)).Strings("mail", mailIds.Count > 0 ? mailIds : null));
            var r = await CallAsync(p, CallGroupSync, body, ct, background: true).ConfigureAwait(false);
            if (r.Status != 200)
            {
                throw new MsfpException(MsfpError.Unreachable, r.Problem);
            }

            var ans = ReadAnswerBody(r.Body);
            List<GroupEnvelope> events, mail;
            Dictionary<string, long> theirs;
            bool more;
            try
            {
                events = ReadEnvelopes(ans.GetArray("events"));
                mail = ReadEnvelopes(ans.GetArray("mail"));
                theirs = ReadHave(ans.GetObject("have"));
                more = ans.GetBool("more") == true;
            }
            catch (ProtocolException e)
            {
                throw new MsfpException(MsfpError.Unreachable, e.Message, e);
            }

            await ReceiveAsync(id, events, mail, p.Id, ct).ConfigureAwait(false);
            // What it lacks: pushed back.
            var pushEvents = kept.Where(e => Head(e) is var (member, seq) && seq > theirs.GetValueOrDefault(member)).Take(MaxSyncEvents).ToList();
            if (pushEvents.Count > 0 && p.Inbound == Inbound.Yes)
            {
                try
                {
                    await CallAsync(p, CallGroupPush, PushBody(id, pushEvents, keptMail.Take(MaxPushMail)), ct, background: true).ConfigureAwait(false);
                }
                catch (MsfpException e)
                {
                    _log.LogDebug(e, "msfp: a group push after a sync failed: {Peer}", p.Id);
                }
            }

            await UpdateGroupAsync(id, g => g.Synced = Now(), ct).ConfigureAwait(false);
            if (!more)
            {
                return;
            }
        }
    }

    // ---- the calls served (groups section 8) ----------------------------------

    // A peer that is a member of a group this server is in, in its copy, or
    // the inviter of a newcomer that has no list yet; the group.
    private async Task<GroupRecord?> CoMemberAsync(CallRequest req, string? id, CancellationToken ct)
    {
        if (req.Peer is not { } peer || peer.State != PeerState.Paired || string.IsNullOrEmpty(id))
        {
            return null;
        }

        GroupRecord? g = null;
        await _store.ReadAsync(async tx => g = tx is IGroupsTx gt ? await gt.GroupAsync(id).ConfigureAwait(false) : null, ct)
            .ConfigureAwait(false);
        if (g is null)
        {
            return null;
        }

        return g.Via == peer.Id || StateOf(g).Members.Values.Any(m => IsMember(m, peer)) ? g : null;
    }

    // group-sync (groups section 8.1).
    private async Task<Answer> GroupSyncServedAsync(CallRequest req, CancellationToken ct)
    {
        string? id;
        Dictionary<string, long> have;
        List<string> mail;
        try
        {
            var body = StrictJson.ReadRequest(req.Body, GroupSyncMembers);
            id = body.GetString("group");
            have = ReadHave(body.GetObject("have"));
            mail = body.GetStrings("mail") ?? [];
        }
        catch (ProtocolException)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        if (mail.Count > MaxKeptMail)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        var g = await CoMemberAsync(req, id, ct).ConfigureAwait(false);
        if (g is null || !On(req.Peer!, Versions.PartGroups))
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        var events = new List<StoredEvent>();
        var more = false;
        foreach (var e in g.Events)
        {
            var (member, seq) = Head(e);
            if (seq <= have.GetValueOrDefault(member))
            {
                continue;
            }

            if (events.Count == MaxSyncEvents)
            {
                more = true;
                break;
            }

            events.Add(e);
        }

        var now = Now().ToUnixTimeSeconds();
        var lacking = (g.Mail ?? []).Where(m => !mail.Contains(m.Id) && m.Expires > now).ToList();
        var ours = StateOf(g).Have();
        return Answer.Ok(JsonOut.Object(o => o
            .Objects("events", events, (w, e) => Envelope(e).WriteTo(w))
            .Objects("mail", lacking, (w, m) => Envelope(m).WriteTo(w))
            .Object("have", HaveOut(ours))
            .Bool("more", more)));
    }

    // group-push (groups section 8.2).
    private async Task<Answer> GroupPushServedAsync(CallRequest req, CancellationToken ct)
    {
        string? id;
        List<GroupEnvelope> events, mail;
        try
        {
            var body = StrictJson.ReadRequest(req.Body, GroupPushMembers);
            id = body.GetString("group");
            var e = body.GetArray("events");
            var m = body.GetArray("mail");
            if (e?.Count > MaxSyncEvents || m?.Count > MaxPushMail)
            {
                return Answer.Refuse(ProblemType.Invalid);
            }

            events = ReadEnvelopes(e);
            mail = ReadEnvelopes(m);
        }
        catch (ProtocolException)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        var co = await CoMemberAsync(req, id, ct).ConfigureAwait(false);
        if (co is null || !On(req.Peer!, Versions.PartGroups))
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        try
        {
            await ReceiveAsync(id!, events, mail, req.Peer!.Id, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code != MsfpError.Internal)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        if (co.Via == req.Peer!.Id)
        {
            // Pushed by the maker this member used a code with: its yes
            // (groups section 2, "A member back in").
            await BackInIfMemberAsync(id!, ct).ConfigureAwait(false);
        }

        return Answer.Ok("{}"u8.ToArray());
    }

    // group-pair, served (groups section 8.3): the caller is a member
    // answering a request this server sent, or the member whose answer it
    // received.
    private async Task<Answer> GroupPairServedAsync(CallRequest req, CancellationToken ct)
    {
        string id, answers, address;
        byte[] commitment;
        bool inbound;
        try
        {
            var body = StrictJson.ReadRequest(req.Body, GroupPairMembers);
            id = body.GetString("group") ?? throw StrictJsonBad();
            answers = body.GetString("answers") ?? throw StrictJsonBad();
            address = body.GetString("address") ?? throw StrictJsonBad();
            Address.Check(address);
            commitment = body.GetBytes("nextCommitment", 32) ?? throw StrictJsonBad();
            inbound = body.GetBool("inbound") ?? true;
        }
        catch (ProtocolException)
        {
            return PairRefused(req);
        }

        var key = req.Verified!.Key;
        string member;
        try
        {
            member = await MatchSentAsync(id, key, answers, ct).ConfigureAwait(false);
        }
        catch (MailUsedException)
        {
            return new Answer(409, null, []);
        }
        catch (MsfpException)
        {
            return PairRefused(req);
        }

        // No name either way: each side knows the other's from its profile
        // (groups section 8.3).
        await LinkThroughAsync(id, key, address, await MemberNameAsync(id, member, ct).ConfigureAwait(false), commitment, inbound, [], ct)
            .ConfigureAwait(false);
        return Answer.Ok(JsonOut.Object(o => o.Bytes("nextCommitment", OurKeys().Commitment).Bool("inbound", !SaysUncallable())));
    }

    // Finds, for a group-pair from key, the mail it answers: one this server
    // sent to that member, not used before; it is then used. The member.
    private async Task<string> MatchSentAsync(string id, byte[] key, string mailId, CancellationToken ct)
    {
        var member = "";
        await _store.UpdateAsync(async tx =>
        {
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            if (g.Used?.Contains(mailId) == true)
            {
                throw new MailUsedException();
            }

            var k = Base64UrlText.Encode(key);
            member = StateOf(g).Members.Values.FirstOrDefault(m => m.Key == k)?.Id ?? throw new MsfpException(MsfpError.NotMember);
            if (await tx.ForgottenAsync(SHA256.HashData(key)).ConfigureAwait(false) is { Blocked: true })
            {
                throw new MsfpException(MsfpError.NotFound); // blocked: kept away from groups (MSFP core section 7.4)
            }

            var sent = g.Sent?.FirstOrDefault(x => x.Id == mailId && x.To == member) ?? throw new MsfpException(MsfpError.NotFound);
            g.Sent!.Remove(sent);
            (g.Used ??= []).Add(mailId);
            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return member;
    }

    // Whether key is the current key of a member of one of this server's
    // groups, not banned (groups section 8: who is heard on group-pair).
    private async ValueTask<bool> GroupMemberAsync(byte[] key, CancellationToken ct)
    {
        var k = Base64UrlText.Encode(key);
        var found = false;
        await _store.ReadAsync(async tx =>
        {
            if (tx is IGroupsTx gt)
            {
                found = (await gt.GroupsAsync().ConfigureAwait(false)).Any(g => StateOf(g).Members.Values.Any(m => m.Key == k));
            }
        }, ct).ConfigureAwait(false);
        return found;
    }

    // Makes (or adds the group to) the link with a member, paired at once
    // (groups section 4.2), and says hello to it.
    private async Task LinkThroughAsync(string id, byte[] key, string address, string? name, byte[] commitment, bool inbound,
        IReadOnlyList<string> pins, CancellationToken ct)
    {
        var pid = "";
        await _store.UpdateAsync(async tx =>
        {
            var p = await tx.PeerByKeyAsync(key).ConfigureAwait(false);
            if (p is null)
            {
                // Linked again: an ended key comes off the forgotten list
                // (MSFP core section 7.4); a blocked one never gets this far.
                await tx.UnforgetAsync(SHA256.HashData(key)).ConfigureAwait(false);
                var now = NowSecond();
                p = new PeerRecord
                {
                    Id = NewId("p"), Key = key, Name = CleanName(name, address), Created = now, Seen = now, State = PeerState.Paired,
                    WasPaired = true, Paired = now, Address = address, OurAddress = OurHost(), Inbound = inbound ? Inbound.Yes : Inbound.No,
                    Commitment = commitment, CommitmentFrom = "pair", CommitmentTold = true, Limits = _cfg.NewPairing! with { }, NotByHand = true,
                    Pins = pins.Count > 0 ? [.. pins] : null,
                };
            }

            p.Groups ??= [];
            if (!p.Groups.Contains(id))
            {
                p.Groups.Add(id);
            }

            pid = p.Id;
            await tx.PutPeerAsync(p).ConfigureAwait(false);
            await RefreshLinksAsync(tx).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        Notify(new PeerPaired(pid, CleanName(name, address)));
        Background(async c =>
        {
            try
            {
                await CheckAsync(pid, background: true, c).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogInformation(e, "msfp: a member paired through a group couldn't be told hello yet: {Peer}", pid);
            }
        });
    }

    // Calls group-pair on a member at address, answering mailId, and makes
    // the link. Its certificate is checked as for any call after pairing:
    // trusted, or named by the pins its mail carried (groups section 8.3).
    private async Task GroupPairAsync(string id, string member, string mailId, string address, byte[] commitment, bool inbound,
        IReadOnlyList<string> pins, CancellationToken ct)
    {
        var key = await WithStateAsync(id, (_, s) =>
            s.Members.TryGetValue(member, out var m) && Base64UrlText.Decode(m.Key, 32) is { } k ? k : throw new MsfpException(MsfpError.NotMember), ct)
            .ConfigureAwait(false);
        var body = JsonOut.Object(o => o.String("group", id).String("answers", mailId).String("address", Named())
            .Bytes("nextCommitment", OurKeys().Commitment).Bool("inbound", !SaysUncallable()));
        var tmp = new PeerRecord { Key = key, Address = address, Inbound = Inbound.Yes, Pins = [.. pins] };
        var r = await ExchangeAsync(tmp, CallGroupPair, body, Current(), ct).ConfigureAwait(false);
        if (r.Status != 200)
        {
            throw new MsfpException(MsfpError.Unreachable, $"group-pair: {r.Status} {r.Problem}");
        }

        try
        {
            ReadAnswerBody(r.Body);
        }
        catch (ProtocolException e)
        {
            throw new MsfpException(MsfpError.Unreachable, e.Message, e);
        }

        await LinkThroughAsync(id, key, address, await MemberNameAsync(id, member, ct).ConfigureAwait(false), commitment, inbound, pins, ct)
            .ConfigureAwait(false);
    }

    // A member's name in a group: its latest profile's, or its joined's
    // until its profile arrives (groups section 3.3); "" when not known.
    private Task<string> MemberNameAsync(string id, string member, CancellationToken ct) =>
        WithStateAsync(id, (_, s) => s.Members.GetValueOrDefault(member)?.Name ?? "", ct);

    // ---- joining (groups section 4) --------------------------------------------

    // Notes, on redeeming a group's code, the group this server joins
    // through the code's maker: a record without events until the list comes.
    private async ValueTask JoinViaAsync(IStoreTx tx, PairingCode c, string peer, CancellationToken ct)
    {
        if (tx is not IGroupsTx gt || c.Group is null)
        {
            return;
        }

        var id = Base64UrlText.Encode(c.Group);
        if (await gt.GroupAsync(id).ConfigureAwait(false) is { } known)
        {
            // A member back in (groups section 2): the link through the
            // maker gains the group once it's made.
            known.Via = peer;
            known.Joining = NowSecond();
            await gt.PutGroupAsync(known).ConfigureAwait(false);
            return;
        }

        var g = new GroupRecord { Id = id, Me = Base64UrlText.Encode(Current().Public()), Via = peer, Pool = _cfg.NewPairing! with { } };
        await NewMailKeyAsync(g, ct).ConfigureAwait(false);
        await gt.PutGroupAsync(g).ConfigureAwait(false);
    }

    // A pairing that became paired: the inviter, accepting a newcomer that
    // used its group's code, signs joined; a newcomer reads the list from
    // its inviter.
    private async Task PairedAsync(PeerRecord p, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(p.JoiningGroup))
        {
            var id = p.JoiningGroup;
            var ok = true;
            try
            {
                // A member back in (groups section 2): the link gains the group, nobody signs joined.
                if (!(await GroupAsync(id, ct).ConfigureAwait(false)).Members.Any(m => m.Peer == p.Id))
                {
                    await ActAsync(id, new GroupEvent { Type = GroupEvents.Joined, NewMember = Base64UrlText.Encode(p.Key), Name = p.Name }, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (MsfpException e)
            {
                ok = false;
                _log.LogInformation(e, "msfp: a server couldn't be added to the group its code invited it to: {Peer}", p.Id);
            }

            await _store.UpdateAsync(async tx =>
            {
                if (await tx.PeerAsync(p.Id).ConfigureAwait(false) is not { } q)
                {
                    return;
                }

                q.JoiningGroup = null;
                if (ok && q.Groups?.Contains(id) != true)
                {
                    (q.Groups ??= []).Add(id);
                }

                if (!ok)
                {
                    // No relation: this side asks for the individual one
                    // (groups section 2), the other side's operator answers.
                    q.NotByHand = false;
                }

                await tx.PutPeerAsync(q).ConfigureAwait(false);
                await RefreshLinksAsync(tx).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            return;
        }

        var via = new List<string>();
        await _store.ReadAsync(async tx =>
        {
            if (tx is IGroupsTx gt)
            {
                via = (await gt.GroupsAsync().ConfigureAwait(false)).Where(g => g.Via == p.Id).Select(g => g.Id).ToList();
            }
        }, ct).ConfigureAwait(false);
        foreach (var id in via)
        {
            await _store.UpdateAsync(async tx =>
            {
                if (await tx.PeerAsync(p.Id).ConfigureAwait(false) is { } q && q.Groups?.Contains(id) != true)
                {
                    (q.Groups ??= []).Add(id);
                    await tx.PutPeerAsync(q).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
            await BackInIfMemberAsync(id, ct).ConfigureAwait(false);
            try
            {
                await SyncWithAsync(id, p, ct).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogInformation(e, "msfp: the group's list couldn't be read from the inviter yet: {Group}", id);
            }
        }
    }

    // Ends a member's way back in a group it was already in (groups section
    // 2, "A member back in"): the link through which it used the code gains
    // the group, as a newcomer's does when the list names it.
    private async Task BackInIfMemberAsync(string id, CancellationToken ct)
    {
        try
        {
            await _store.UpdateAsync(async tx =>
            {
                var gt = GroupsOf(tx);
                if (await gt.GroupAsync(id).ConfigureAwait(false) is not { Via: { } via } g || !StateOf(g).Members.ContainsKey(g.Me))
                {
                    return;
                }

                if (await tx.PeerAsync(via).ConfigureAwait(false) is { } p && p.Groups?.Contains(id) != true)
                {
                    (p.Groups ??= []).Add(id);
                    await tx.PutPeerAsync(p).ConfigureAwait(false);
                }

                g.Via = null;
                g.Joining = null;
                await gt.PutGroupAsync(g).ConfigureAwait(false);
                await RefreshLinksAsync(tx).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (MsfpException e)
        {
            _log.LogInformation(e, "msfp: back in a group: the link couldn't gain it: {Group}", id);
        }
    }

    // A newcomer's next steps, once the list names it: its profile, then
    // its requests.
    private async Task JoinedAsync(string id, CancellationToken ct)
    {
        StoredEvent? sent = null;
        await _store.UpdateAsync(async tx =>
        {
            sent = null;
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            var s = StateOf(g);
            if (!s.Members.TryGetValue(g.Me, out var me) || me.EncryptionKey is not null)
            {
                return; // no profile to sign
            }

            sent = await ProfileAsync(g, s, ct).ConfigureAwait(false);
            g.Events.Add(sent);
            if (!string.IsNullOrEmpty(g.Via) && await tx.PeerAsync(g.Via).ConfigureAwait(false) is { } p && p.Groups?.Contains(id) != true)
            {
                // The inviter's link gains the group as a source (a peer
                // already, by group-join, or paired by its code).
                (p.Groups ??= []).Add(id);
                await tx.PutPeerAsync(p).ConfigureAwait(false);
            }

            g.Via = null;
            g.Joining = null;
            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
            await RefreshLinksAsync(tx).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (sent is null)
        {
            return;
        }

        await PushSoonAsync(id, [sent], [], "", ct).ConfigureAwait(false);
        await SendRequestsAsync(id, null, ct).ConfigureAwait(false);
    }

    // Accepts, where this server accepts members by itself, the requests
    // whose senders have now been in the group its days (groups section 4.1).
    private async Task AcceptWaitedAsync(string id, CancellationToken ct)
    {
        var ready = new List<GroupRequest>();
        await _store.UpdateAsync(async tx =>
        {
            ready.Clear();
            var g = await GroupsOf(tx).GroupAsync(id).ConfigureAwait(false);
            if (g is null || !g.Accept || g.Requests is not { Count: > 0 } requests)
            {
                return;
            }

            var s = StateOf(g);
            ready.AddRange(requests.Where(r => Waited(s, r.From)));
            if (ready.Count == 0)
            {
                return;
            }

            g.Requests = requests.Where(r => !ready.Contains(r)).ToList();
            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        foreach (var r in ready)
        {
            try
            {
                await AnswerRequestAsync(id, r, ct).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogInformation(e, "msfp: a request that waited its days couldn't be accepted: {Group}", id);
            }
        }
    }

    // ---- group-join (groups sections 2 and 8.4) ---------------------------------

    // Redeems a group's code from a peer already paired with this server:
    // group-join, through the pairing, instead of pair.
    private async Task<PeerView> JoinPairedAsync(PairingCode c, PeerRecord p, CancellationToken ct)
    {
        var id = Base64UrlText.Encode(c.Group);
        await _store.ReadAsync(async tx =>
        {
            // A member back in, through a link that isn't through the group
            // (groups section 2): only if this server can be called, for the
            // maker's push says yes.
            if (await GroupsOf(tx).GroupAsync(id).ConfigureAwait(false) is { } g &&
                (p.Groups?.Contains(id) == true || SaysUncallable() || !StateOf(g).Members.ContainsKey(g.Me)))
            {
                throw new MsfpException(MsfpError.AlreadyMember);
            }
        }, ct).ConfigureAwait(false);
        if (!On(p, Versions.PartGroups))
        {
            throw new MsfpException(MsfpError.NotSupported);
        }

        var body = JsonOut.Object(o => o.Bytes("secretHash", Signing.SecretHash(c.Secret))
            .String("proof", GroupEvents.JoinProof(c.Secret, Current().PublicSpan)));
        var r = await CallAsync(p, CallGroupJoin, body, ct).ConfigureAwait(false);
        if (r.Status == 409)
        {
            throw new MsfpException(MsfpError.AlreadyMember);
        }

        if (r.Status != 200)
        {
            throw new MsfpException(MsfpError.CodeRefused);
        }

        await _store.UpdateAsync(async tx =>
        {
            await JoinViaAsync(tx, c, p.Id, ct).ConfigureAwait(false);
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            g.Joining = NowSecond();
            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        _log.LogInformation("msfp: a group's code used with a peer, waiting for its operator: {Peer} {Group}", p.Id, id);
        return View(p);
    }

    // group-join, served: a peer already paired with this server using its
    // code for a group (groups section 8.4).
    private async Task<Answer> GroupJoinServedAsync(CallRequest req, CancellationToken ct)
    {
        byte[]? hash;
        string proof;
        try
        {
            var body = StrictJson.ReadRequest(req.Body, GroupJoinMembers);
            hash = body.GetBytes("secretHash", 32);
            proof = body.GetString("proof") ?? "";
        }
        catch (ProtocolException)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        if (hash is null || req.Peer is not { } peer || peer.State != PeerState.Paired || !On(peer, Versions.PartGroups))
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        GroupJoinWaiting? waiting = null;
        try
        {
            await _store.UpdateAsync(async tx =>
            {
                waiting = null;
                var gt = tx as IGroupsTx ?? throw new MsfpException(MsfpError.NotFound);
                // The code, the proof, then the right to invite.
                var c = await tx.CodeByHashAsync(hash).ConfigureAwait(false);
                if (c is null || string.IsNullOrEmpty(c.Group) || c.Expires <= Now() || c.Secret is null)
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                byte[] secret;
                try
                {
                    secret = await _sealer.OpenAsync(c.Secret, SealContext.Code, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new MsfpException(MsfpError.NotFound, null, e);
                }

                if (!GroupEvents.CheckJoinProof(secret, req.Verified!.Key, proof))
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                var g = await gt.GroupAsync(c.Group).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
                var s = StateOf(g);
                if (!s.Members.ContainsKey(g.Me) || Role(s, g.Me) == "member" || s.Dissolved)
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                // A member is back in through a link that isn't through the
                // group (section 2), if it can be called: it learns of the yes
                // from this server's push.
                var member = s.Members.Values.Any(m => IsMember(m, peer)) && (peer.Groups?.Contains(g.Id) == true || peer.Inbound != Inbound.Yes);
                if (s.Banned.ContainsKey(Base64UrlText.Encode(peer.Key)) || member || g.Joins?.Any(j => j.Peer == peer.Id) == true)
                {
                    throw new MsfpException(MsfpError.AlreadyMember);
                }

                await SpendCodeAsync(tx, c).ConfigureAwait(false);
                (g.Joins ??= []).Add(new GroupJoin { Peer = peer.Id, Received = NowSecond() });
                waiting = new GroupJoinWaiting(g.Id, s.Name, peer.Id, peer.Name, Keys.Fingerprint(peer.Key));
                await gt.PutGroupAsync(g).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }
        catch (MsfpException e) when (e.Code == MsfpError.AlreadyMember)
        {
            return new Answer(409, null, []);
        }

        Notify(waiting!);
        return Answer.Ok("{}"u8.ToArray());
    }

    /// <summary>
    /// Accepts a peer waiting to join a group through this server
    /// (<see cref="GroupView.Joins"/>): it signs joined, and sends the peer
    /// the list (MSFP groups section 2).
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/> (no such group or peer waiting), <see cref="MsfpError.NoRight"/>,
    /// <see cref="MsfpError.NotPaired"/>.</exception>
    public async Task AcceptJoinAsync(string id, string peer, CancellationToken ct = default)
    {
        var g = await GroupAsync(id, ct).ConfigureAwait(false);
        if (g.Joins.All(j => j.Peer != peer))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        if (p.State != PeerState.Paired)
        {
            throw new MsfpException(MsfpError.NotPaired);
        }

        // A member back in (groups section 2): nobody signs joined.
        if (g.Members.All(m => m.Peer != p.Id))
        {
            await ActAsync(id, new GroupEvent { Type = GroupEvents.Joined, NewMember = Base64UrlText.Encode(p.Key), Name = p.Name }, ct)
                .ConfigureAwait(false);
        }

        await _store.UpdateAsync(async tx =>
        {
            var gt = GroupsOf(tx);
            if (await gt.GroupAsync(id).ConfigureAwait(false) is { } gr)
            {
                gr.Joins?.RemoveAll(j => j.Peer == peer);
                await gt.PutGroupAsync(gr).ConfigureAwait(false);
            }

            if (await tx.PeerAsync(peer).ConfigureAwait(false) is not { } q)
            {
                return;
            }

            if (q.Groups?.Contains(id) != true)
            {
                (q.Groups ??= []).Add(id);
            }

            await tx.PutPeerAsync(q).ConfigureAwait(false);
            await RefreshLinksAsync(tx).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        Background(c => PushAllAsync(id, peer, c));
    }

    /// <summary>Refuses a peer waiting to join a group through this server: nothing is sent (MSFP groups section 2).</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public async Task RefuseJoinAsync(string id, string peer, CancellationToken ct = default)
    {
        var found = false;
        await UpdateGroupAsync(id, g => found = g.Joins?.RemoveAll(j => j.Peer == peer) > 0, ct).ConfigureAwait(false);
        if (!found)
        {
            throw new MsfpException(MsfpError.NotFound);
        }
    }

    // Drops what waited too long for group-join: this server's own joins,
    // and peers' joins waiting for its operator.
    private async Task TidyGroupsAsync(CancellationToken ct)
    {
        var drop = new List<string>();
        var cutoff = Now() - JoinWait;
        await _store.UpdateAsync(async tx =>
        {
            drop.Clear();
            if (tx is not IGroupsTx gt)
            {
                return;
            }

            foreach (var g in await gt.GroupsAsync().ConfigureAwait(false))
            {
                if (g.Joining is { } joining && joining < cutoff && g.Events.Count == 0)
                {
                    drop.Add(g.Id);
                    continue;
                }

                if (g.Joins?.RemoveAll(j => j.Received < cutoff) > 0)
                {
                    await gt.PutGroupAsync(g).ConfigureAwait(false);
                }
            }
        }, ct).ConfigureAwait(false);
        foreach (var id in drop)
        {
            _log.LogInformation("msfp: gave up joining a group: its inviter's operator didn't accept within 30 days: {Group}", id);
            await DropGroupAsync(id, keep: false, "left", "", ct).ConfigureAwait(false);
        }
    }

    // ---- keys (groups section 3.3, rotated and revoked) ------------------------

    // The groups this server is a member of.
    private async Task<List<string>> GroupIdsAsync(CancellationToken ct)
    {
        var output = new List<string>();
        await _store.ReadAsync(async tx =>
        {
            if (tx is IGroupsTx gt)
            {
                output = (await gt.GroupsAsync().ConfigureAwait(false)).Where(g => StateOf(g).Members.ContainsKey(g.Me)).Select(g => g.Id).ToList();
            }
        }, ct).ConfigureAwait(false);
        return output;
    }

    // Tells this server's groups of its new key: a rotated event in each,
    // signed with it (MSFP core section 12, groups section 3.3).
    private async Task GroupsRotatedAsync(CancellationToken ct)
    {
        var k = OurKeys();
        if (k.Old is null)
        {
            return;
        }

        var rot = Rotations.SignRotation(k.Old, k.Current, k.Commitment, Now());
        var statement = new JsonObject
        {
            ["oldKey"] = Base64UrlText.Encode(rot.Old), ["newKey"] = Base64UrlText.Encode(rot.New),
        };
        if (rot.Next is not null)
        {
            statement["nextCommitment"] = Base64UrlText.Encode(rot.Next);
        }

        statement["date"] = rot.Date;
        statement["oldSignature"] = Base64UrlText.Encode(rot.OldSignature);
        statement["newSignature"] = Base64UrlText.Encode(rot.NewSignature);
        foreach (var id in await GroupIdsAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ActAsync(id, new GroupEvent { Type = GroupEvents.Rotated, Statement = statement.DeepClone().AsObject() }, ct).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogWarning(e, "msfp: a group couldn't be told of this server's new key: {Group}", id);
            }
        }
    }

    // Tells this server's groups that its key was stolen: a revoked event
    // in each, signed with that key, its last good event the last before now.
    // A peer took this server's new key (its rotate answered rotated): a
    // group-sync with it, for each group they share, pushes the events it
    // lacks. Those signed for the change (rotated; revoked, after a stolen
    // key) were pushed while it still knew the old key, or held this server
    // suspended (MSFP core section 12.6), and may have been refused; without
    // this, they and every event after them would wait for the next round
    // of group-sync.
    private void SyncGroupsSoon(string peerId) => Background(async ct =>
    {
        PeerRecord? p = null;
        await _store.ReadAsync(async tx => p = await tx.PeerAsync(peerId).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (p is null || p.State != PeerState.Paired)
        {
            return;
        }

        foreach (var id in p.Groups?.ToList() ?? [])
        {
            try
            {
                await SyncWithAsync(id, p, ct).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogDebug(e, "msfp: a group sync after a rotation failed: {Group} {Peer}", id, peerId);
            }
        }
    });

    private async Task GroupsRevokedAsync(Revocation r, CancellationToken ct)
    {
        var statement = new JsonObject
        {
            ["key"] = Base64UrlText.Encode(r.Key), ["date"] = r.Date, ["signature"] = Base64UrlText.Encode(r.Signature),
        };
        foreach (var id in await GroupIdsAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var last = await WithStateAsync(id, (g, s) => s.Seq(g.Me), ct).ConfigureAwait(false);
                await ActAsync(id, new GroupEvent { Type = GroupEvents.Revoked, Statement = statement.DeepClone().AsObject(), LastSeq = last }, ct)
                    .ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogWarning(e, "msfp: a group couldn't be told this server's key was stolen: {Group}", id);
            }
        }
    }
}
