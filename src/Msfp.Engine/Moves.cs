using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// Changes of address (MSFP core section 13), this server's and its peers'.
//
// This server moves when the host gives it another address
// (SetAddressAsync, or OpenAsync with another MsfpConfig.Address), and when
// it turns "can't be called" on or off (SetUncallableAsync). From A1 to A2:
// requests signed for either host are accepted, for 30 days or until every
// peer that isn't gone signs for A2's; A2 is announced only once this
// server reaches itself there (a hello at A2 signed with its own key, which
// only this server answers, with the signed 404 it gives that key alone),
// tried at once, then from 5 seconds to every 5 minutes; until then its
// hellos name A1. Once reached, every peer is sent a hello at once, and
// again at each contact while it hasn't moved. A move to or from an
// address of the .invalid form keeps no former address: nothing calls it
// there.
//
// A peer whose signed hello names another address than the one stored is
// still called at the stored one until a hello at the new address, signed
// for its host, is answered under the peer's key (after its discovery
// document is fetched there): then the new address, its base and the pins
// of that answer are stored. Failing, the old one stays; the confirmation
// is tried again at most once an hour, and the operator is warned after a
// day. A hello naming the stored address again drops the move. A suspended
// peer's move waits. A peer that can't be called moves at once: nothing
// calls it.
public sealed partial class MsfpEngine
{
    // When a peer that hasn't moved to our new address is pointed out (MSFP
    // core section 13.1: "after some days").
    internal static readonly TimeSpan MoveLate = TimeSpan.FromDays(3);

    // The least time between two hellos announcing our address to a peer
    // that hasn't moved.
    internal static readonly TimeSpan AnnounceSpacing = TimeSpan.FromMinutes(1);

    // How often this server tries its new address before announcing it: at
    // once, then after ReachFirst, twice as long each time, ReachMost at most.
    internal static readonly TimeSpan ReachFirst = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ReachMost = TimeSpan.FromMinutes(5);

    // The address the host configured last (MsfpConfig.Address, or the
    // last SetAddressAsync): where this server goes back to once it can be
    // called again.
    private string _configured = "";

    // Whether this server says it can't be called: its address named is of
    // the .invalid form (MSFP core section 7.5). After it turns the setting off,
    // it still says so until its real address is reached.
    internal bool SaysUncallable() => Uncallables.Valid(Named());

    // The host of this server's former address while it is accepted (MSFP
    // core section 13.1, step 1): during a move, for 30 days once announced (and
    // until it is), never for one of the .invalid form; null otherwise.
    internal string? FormerHost()
    {
        var a = OurAddress();
        if (a.Former is null || Uncallables.Valid(a.Former) ||
            (a.Reached && a.MovedAt is { } at && Now() - at >= Address.Overlap))
        {
            return null;
        }

        return Address.Host(a.Former);
    }

    /// <summary>
    /// The host's address changing (its settings, a new name or port): this
    /// server moves there (MSFP core section 13.1). It tries the new address at
    /// once; once it reaches itself there, every peer is told, and requests
    /// signed for the former address are still accepted for 30 days or until
    /// every peer moved. While this server can't be called, the address is
    /// only kept, for when it can again.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.InvalidAddress"/>, <see cref="MsfpError.PlainHttp"/>.</exception>
    public async Task SetAddressAsync(string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        try
        {
            Address.Check(address);
        }
        catch (ProtocolException e) when (e.Error == ProtocolError.PlainHttp)
        {
            throw new MsfpException(MsfpError.PlainHttp);
        }
        catch (ProtocolException e)
        {
            throw new MsfpException(MsfpError.InvalidAddress, e.Message, e);
        }

        if (Uncallables.Valid(address))
        {
            throw new MsfpException(MsfpError.InvalidAddress,
                "the address of a server that can't be called is made by the engine (SetUncallableAsync)");
        }

        lock (_keysLock)
        {
            _configured = address;
        }

        await MoveToAsync(r => r.Uncallable ? r.Address : address, ct).ConfigureAwait(false);
    }

    // Moves this server to the address to gives from the record, in one
    // Update; nothing when it is there already. A move to be announced is
    // tried at once, and announced when this server reaches itself.
    private async Task MoveToAsync(Func<ServerRecord, string> to, CancellationToken ct)
    {
        string from = "", now = "";
        await ChangeServerAsync(async (tx, r) =>
        {
            from = r.Address;
            now = to(r);
            if (now != from)
            {
                await StartMoveAsync(tx, r, now).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
        if (from == now)
        {
            return;
        }

        _log.LogInformation("msfp: this server's address changed; peers are told once it answers there: {From} {To}", from, now);
        ResetHellos();
        var a = OurAddress();
        if (a.Former is not null && !a.Reached)
        {
            ReachNow();
            await ReachAndAnnounceAsync(ct).ConfigureAwait(false);
        }
    }

    // Moves r to the address to, in tx: the address before it the former
    // one, accepted while the move lasts. A move to the .invalid form keeps
    // no former (nothing calls it; peers are told by the scheduled hellos);
    // a move every peer has made already (none to make) is over at once.
    private async Task StartMoveAsync(IStoreTx tx, ServerRecord r, string to)
    {
        var from = r.Address;
        r.Address = to;
        r.Former = null;
        r.MovedAt = null;
        r.Reached = false;
        if (from.Length == 0 || Uncallables.Valid(to))
        {
            return;
        }

        // From the .invalid form, the move lasts until the real address is
        // reached, whoever has moved: until then this server still says it
        // can't be called (MSFP core section 7.5, "Turning it off").
        var peers = await tx.PeersAsync().ConfigureAwait(false);
        if (NotMovedCount(peers, Address.Host(to)) > 0 || Uncallables.Valid(from))
        {
            r.Former = from;
            r.MovedAt = NowSecond();
        }
    }

    // Whether r's move ends now that n peers haven't moved: none left, and,
    // from the .invalid form, the real address reached.
    private static bool MoveOver(ServerRecord r, int n) => n == 0 && (r.Reached || !Uncallables.Valid(r.Former));

    // Whether p counts among the peers that must move to our address of
    // host: it isn't gone, and its requests weren't last signed for host
    // (MSFP core section 13.1, step 1).
    internal static bool NotMoved(PeerRecord p, string host) => p.State != PeerState.Gone && p.OurAddress != host;

    private static int NotMovedCount(IEnumerable<PeerRecord> peers, string host) => peers.Count(p => NotMoved(p, host));

    // Ends r's move: its former address no longer accepted.
    private static MoveEnded EndMove(ServerRecord r, int notMoved)
    {
        r.Former = null;
        r.MovedAt = null;
        r.Reached = false;
        return new MoveEnded(notMoved);
    }

    // Has this server try its new address at the next chance.
    private void ReachNow()
    {
        lock (_sched)
        {
            _sched.ReachAt = Now();
            _sched.ReachWait = TimeSpan.Zero;
        }
    }

    // Tries this server's new address, if that is due, and once it reaches
    // itself there, announces it: the move is noted as reached, and every
    // peer that can be called is sent a hello naming it, at once (MSFP
    // core section 13.1, step 2). Failing, it tries again after 5 seconds, twice
    // as long each time, every 5 minutes at most.
    private async Task ReachAndAnnounceAsync(CancellationToken ct)
    {
        var a = OurAddress();
        if (a.Former is null || a.Reached)
        {
            return;
        }

        var now = Now();
        lock (_sched)
        {
            if (_sched.Reaching || _sched.ReachAt is not { } at || now < at)
            {
                return;
            }

            _sched.Reaching = true;
        }

        Exception? failed = null;
        try
        {
            await AnswersAtAsync(a.Address, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is MsfpException or NotOwnAnswerException)
        {
            failed = e;
        }

        bool first;
        lock (_sched)
        {
            _sched.Reaching = false;
            first = _sched.ReachWait == TimeSpan.Zero;
            if (failed is not null)
            {
                var twice = _sched.ReachWait * 2;
                _sched.ReachWait = twice < ReachFirst ? ReachFirst : twice > ReachMost ? ReachMost : twice;
                _sched.ReachAt = now + _sched.ReachWait;
            }
            else
            {
                // A new move: every peer may be told at once again.
                _sched.ReachAt = null;
                _sched.ReachWait = TimeSpan.Zero;
                _sched.Announced.Clear();
            }
        }

        if (failed is not null)
        {
            if (first)
            {
                _log.LogInformation(failed, "msfp: this server doesn't answer at its new address yet; peers are told once it does: {Address}", a.Address);
            }

            return;
        }

        var reached = false;
        await ChangeServerAsync((_, r) =>
        {
            if (r.Address == a.Address && !string.IsNullOrEmpty(r.Former) && !r.Reached)
            {
                // The 30 days of the former address run from now (MSFP
                // core section 13.1, step 1).
                r.Reached = true;
                r.MovedAt = NowSecond();
                reached = true;
            }

            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
        if (!reached)
        {
            return;
        }

        _log.LogInformation("msfp: this server answers at its new address; peers are told: {Address}", a.Address);
        Notify(new AddressAnnounced(a.Address));
        ResetHellos();
        await AnnounceAllAsync(ct).ConfigureAwait(false);
    }

    // This server's request to its own address wasn't answered under its
    // own key: another server, or none, answers there.
    private sealed class NotOwnAnswerException(int status) : Exception($"msfp: not this server's own answer: it answered {status}");

    // Checks that address reaches this server, not another one: a hello
    // there, signed with this server's key for that address's host, as any
    // call to a peer (refused destinations refused, any certificate
    // accepted). This server answers its own key, alone, with the signed 404
    // of a key it doesn't know (forgotten); the signature covers the
    // request's nonce, so only this server, reached there, can have made it.
    private async Task AnswersAtAsync(string address, CancellationToken ct)
    {
        var key = Current();
        var self = new PeerRecord { Address = address, Key = key.Public(), Base = _cfg.Prefix + Discovery.DefaultBase };
        var body = JsonOut.Object(o =>
        {
            o.String("state", Hello.StateWaiting);
            OurVersions().WriteTo(o);
        });
        var r = await ExchangeAsync(self, CallHello, body, key, ct).ConfigureAwait(false);
        if (r.Unsigned || r.Status != 404)
        {
            throw new NotOwnAnswerException(r.Status);
        }
    }

    // Sends a hello naming our address to every peer that hasn't moved and
    // can be called, at once, and waits.
    private Task AnnounceAllAsync(CancellationToken ct)
    {
        var host = OurHost();
        return EachPeerAsync(p => NotMoved(p, host) && p.Inbound == Inbound.Yes && p.Suspended is null,
            p => AnnounceAsync(p.Id, ct), ct);
    }

    // Tells peer id of our address in a hello now, unless one went within
    // AnnounceSpacing.
    private async Task AnnounceAsync(string id, CancellationToken ct)
    {
        if (AnnounceDue(id))
        {
            await QuietCheckAsync(id, ct).ConfigureAwait(false);
        }
    }

    // AnnounceAsync in the background, for p: a peer that hasn't moved, at a
    // contact (MSFP core section 13.1, step 2).
    private void AnnounceSoon(PeerRecord p)
    {
        if (p.Inbound != Inbound.Yes || p.Suspended is not null || !AnnounceDue(p.Id))
        {
            return;
        }

        var id = p.Id;
        Background(ct => QuietCheckAsync(id, ct));
    }

    // A hello the engine sends by itself: its failure is only logged.
    private async Task QuietCheckAsync(string id, CancellationToken ct)
    {
        try
        {
            await CheckAsync(id, background: true, ct).ConfigureAwait(false);
        }
        catch (MsfpException e)
        {
            _log.LogDebug(e, "msfp: a hello failed: {Peer}", id);
        }
    }

    // Whether a hello announcing our address may go to peer id now, and
    // notes it goes: only during a move announced.
    private bool AnnounceDue(string id)
    {
        var a = OurAddress();
        if (a.Former is null || !a.Reached)
        {
            return false;
        }

        var now = Now();
        lock (_sched)
        {
            if (_sched.Announced.TryGetValue(id, out var last) && now - last < AnnounceSpacing)
            {
                return false;
            }

            _sched.Announced[id] = now;
            return true;
        }
    }

    // Notes which of our addresses a known peer's request was signed for
    // (former: our former one): one still on the former is told of the new
    // one again; the last to move to the new one ends the move.
    private async Task HeardAtAsync(PeerRecord p, bool former, CancellationToken ct)
    {
        var host = OurHost();
        if (former)
        {
            host = FormerHost();
            AnnounceSoon(p);
        }

        if (string.IsNullOrEmpty(host) || p.OurAddress == host)
        {
            return;
        }

        var ended = false;
        await ChangeServerAsync(async (tx, r) =>
        {
            ended = false;
            var q = await tx.PeerAsync(p.Id).ConfigureAwait(false);
            if (q is null)
            {
                return;
            }

            q.OurAddress = host;
            await tx.PutPeerAsync(q).ConfigureAwait(false);
            if (former || string.IsNullOrEmpty(r.Former) || host != Address.Host(r.Address))
            {
                return;
            }

            if (MoveOver(r, NotMovedCount(await tx.PeersAsync().ConfigureAwait(false), host)))
            {
                EndMove(r, 0);
                ended = true;
            }
        }, ct).ConfigureAwait(false);
        p.OurAddress = host;
        if (ended)
        {
            _log.LogInformation("msfp: every peer moved to this server's new address; the former one is no longer accepted");
            Notify(new MoveEnded(0));
        }
    }

    // What time does to addresses, in RunOnceAsync: our move over after 30
    // days (or once every peer moved), the peers still on our former address
    // pointed out after 3 days and told again; peers' new addresses that
    // failed tried again, at most hourly.
    private async Task MaintainAddressesAsync(CancellationToken ct)
    {
        var now = Now();
        if (OurAddress().Former is not null)
        {
            var events = new List<MsfpEvent>();
            await ChangeServerAsync(async (tx, r) =>
            {
                events.Clear();
                if (string.IsNullOrEmpty(r.Former))
                {
                    return;
                }

                var peers = await tx.PeersAsync().ConfigureAwait(false);
                var host = Address.Host(r.Address);
                var n = NotMovedCount(peers, host);
                if (MoveOver(r, n))
                {
                    events.Add(EndMove(r, 0));
                }
                else if (r.Reached && r.MovedAt is { } at && now - at >= Address.Overlap)
                {
                    _log.LogWarning("msfp: 30 days since this server's address changed; peers that didn't follow can't reach it: {Peers}", n);
                    events.Add(EndMove(r, n));
                }
                else
                {
                    foreach (var p in peers)
                    {
                        if (r.Reached && NotMoved(p, host) && r.MovedAt is { } since && now - since >= MoveLate && LateOnce(p.Id))
                        {
                            events.Add(new NotMovedLate(p.Id, p.Name));
                        }
                    }
                }
            }, ct).ConfigureAwait(false);
            Notify([.. events]);
            await AnnounceAllAsync(ct).ConfigureAwait(false);
        }

        await EachPeerAsync(p => p.Move is not null && p.Suspended is null && MoveDue(p.Move, now),
            p => ConfirmMoveAsync(p.Id, ct), ct).ConfigureAwait(false);
    }

    // Whether a peer late to move is to be pointed out: once per process.
    private bool LateOnce(string id)
    {
        lock (_sched)
        {
            return _sched.Late.Add(id);
        }
    }

    // A peer's change of address, here.

    // Whether m's confirming hello may go now: the first at once, then at
    // most once an hour after a failure.
    private static bool MoveDue(PeerMove m, DateTimeOffset now) => new AddressMove(m.Address, m.Tried, m.FailingSince).Due(now);

    // Keeps what a peer's signed hello, request or answer, says of where it
    // is, after KeepInbound (MSFP core sections 7.5 and 13.2): another address
    // than the one stored is to be confirmed (confirm), or, for a peer that
    // can't be called, stored at once; the stored one again drops a move not
    // confirmed. A peer that says it can be called again is confirmed where
    // it says it is now, or where it was. An address that isn't one is
    // ignored, the stored one kept.
    private (List<MsfpEvent> Events, bool Confirm) KeepAddress(PeerRecord p, string? address)
    {
        string? next;
        try
        {
            next = Address.Announced(p.Address, address);
        }
        catch (ProtocolException)
        {
            _log.LogInformation("msfp: a peer named an address that isn't one; its stored address stays: {Peer}", p.Id);
            return ([], false);
        }

        var now = NowSecond();
        if (p.Inbound == Inbound.No)
        {
            // Nothing calls it: nothing to confirm, no former kept, and no
            // pins of it to drop (MSFP core section 13.2).
            if (next is null)
            {
                return ([], false);
            }

            var from = p.Address;
            p.Address = next;
            p.Base = null;
            p.Move = null;
            _log.LogInformation("msfp: a peer that can't be called moved: {Peer} {From} {To}", p.Id, from, next);
            return ([new PeerMoved(p.Id, p.Name, from, next)], false);
        }

        if (p.Inbound == Inbound.Confirming)
        {
            next ??= p.Address;
        }

        if (next is null)
        {
            if (address == p.Address && p.Move is not null)
            {
                p.Move = null;
            }

            return ([], false);
        }

        if (p.Move is null || p.Move.Address != next)
        {
            p.Move = new PeerMove { Address = next, Since = now };
            if (next != p.Address)
            {
                _log.LogInformation("msfp: a peer says it moved; its new address is checked: {Peer} {From} {To}", p.Id, p.Address, next);
            }
        }

        return ([], p.Suspended is null && MoveDue(p.Move, now));
    }

    // Keeps whether p can be called, as its signed hello says (MSFP
    // core section 7.5). Once it can't, it is never connected to, and a move of it
    // waiting is dropped. Once it says it can again, it is "confirming":
    // connected to only after a hello at its address answers under its key
    // (KeepAddress starts that check, ConfirmMoveAsync ends it).
    private List<MsfpEvent> KeepInbound(PeerRecord p, bool uncallable)
    {
        if (uncallable && p.Inbound != Inbound.No)
        {
            p.Inbound = Inbound.No;
            p.Move = null;
            ForgetCalls(p.Id);
            _log.LogInformation("msfp: a peer says it can't be called: it can watch what this server shares with it, and shares nothing: {Peer}", p.Id);
            return [new InboundChanged(p.Id, p.Name, Inbound.No)];
        }

        if (!uncallable && p.Inbound == Inbound.No)
        {
            p.Inbound = Inbound.Confirming;
            _log.LogInformation("msfp: a peer says it can be called again; its address is checked first: {Peer}", p.Id);
            return [new InboundChanged(p.Id, p.Name, Inbound.Confirming)];
        }

        // A record from before inbound was kept: it can be called.
        if (p.Inbound.Value.Length == 0)
        {
            p.Inbound = Inbound.Yes;
        }

        return [];
    }

    // ConfirmMoveAsync in the background.
    private void ConfirmSoon(string id) => Background(ct => ConfirmMoveAsync(id, ct));

    // Confirms peer id's new address, one at a time per peer (MSFP
    // core section 13.2, step 2): its discovery document fetched there, then a hello
    // there, signed for its host, as any connection to a peer (refused
    // destinations refused, any certificate accepted). Answered under its
    // key, the new address and base are stored, with the pins that answer
    // names, the old address's dropped; a peer that said it can be called
    // again is called from then on; one whose answer says inbound false is
    // back to "can't be called". Only a 200 confirms (a signed 404 there
    // doesn't). Failing, the old address stays, and the failure is noted
    // for the next try, an hour later at least; after a day of failures the
    // operator is warned. A suspended peer's move waits: nothing is asked of
    // a revoked key.
    private async Task ConfirmMoveAsync(string id, CancellationToken ct)
    {
        lock (_sched)
        {
            if (!_sched.Confirming.Add(id))
            {
                return;
            }
        }

        // Again: it named another address while this one was checked, which
        // waited for this check to end.
        var again = false;
        try
        {
            again = await ConfirmMoveOnceAsync(id, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_sched)
            {
                _sched.Confirming.Remove(id);
            }
        }

        if (again)
        {
            ConfirmSoon(id);
        }
    }

    private async Task<bool> ConfirmMoveOnceAsync(string id, CancellationToken ct)
    {
        PeerRecord p;
        try
        {
            p = await PeerRecordAsync(id, ct).ConfigureAwait(false);
        }
        catch (MsfpException)
        {
            return false;
        }

        if (p.Move is not { } m || p.Suspended is not null || !MoveDue(m, Now()))
        {
            return false;
        }

        var there = p with { Address = m.Address, Base = await FetchBaseAsync(m.Address, ct).ConfigureAwait(false), Pins = null };
        Hello? ans = null;
        Exception? failed = null;
        try
        {
            var hello = await OurHelloAsync(ViewOf(p), p, answering: false, ct).ConfigureAwait(false);
            var r = await ExchangeAsync(there, CallHello, hello, SignerFor(p), ct).ConfigureAwait(false);
            if (r.Status != 200)
            {
                failed = new MsfpException(MsfpError.Unreachable, $"it answered {r.Status}");
            }
            else
            {
                ans = Hello.Read(r.Body, answer: true);
                Uncallables.CheckInbound(ans.Uncallable, ans.Address ?? m.Address);
            }
        }
        catch (Exception e) when (e is MsfpException or ProtocolException)
        {
            failed = e;
        }

        var now = NowSecond();
        var events = new List<MsfpEvent>();
        if (failed is not null || ans is null)
        {
            var again = false;
            try
            {
                await UpdatePeerAsync(id, q =>
                {
                    events.Clear();
                    again = false;
                    if (q.Move is null || q.Move.Address != m.Address)
                    {
                        again = q.Move is not null;
                        return;
                    }

                    q.Move.Tried = now;
                    q.Move.FailingSince ??= now;
                    if (new AddressMove(m.Address, null, q.Move.FailingSince).Warn(now) && !q.Move.Warned)
                    {
                        q.Move.Warned = true;
                        events.Add(new PeerMoveFailing(q.Id, q.Name, m.Address, q.Move.FailingSince.Value));
                    }
                }, ct).ConfigureAwait(false);
            }
            catch (MsfpException)
            {
                return false;
            }

            _log.Log(events.Count > 0 ? LogLevel.Warning : LogLevel.Information, failed,
                "msfp: a peer's new address doesn't answer as it; its old address stays: {Peer} {Address}", id, m.Address);
            Notify([.. events]);
            return again;
        }

        // A revocation in that answer: suspended, and nothing else of it
        // kept (MSFP core section 12.6).
        if (ans.Pull(p.Key, now).Revocation is not null)
        {
            try
            {
                Notify([.. await RevokedAsync(id, p.Key, ct).ConfigureAwait(false)]);
            }
            catch (MsfpException)
            {
            }

            return false;
        }

        var superseded = false;
        try
        {
            await UpdatePeerAsync(id, q =>
            {
                events.Clear();
                superseded = false;
                if (q.Move is null || !Keys.Same(q.Key, p.Key))
                {
                    return; // its stored address named again, or another key, meanwhile
                }

                // It says it can't be called after all: back to that, its move
                // dropped, the address it names (of the .invalid form) stored
                // at once, as any hello of such a peer (MSFP core section 7.5).
                if (ans.Uncallable)
                {
                    q.Move = null;
                    events.AddRange(KeepHello(q, ans).Events);
                    if (ans.State == Hello.StatePaired)
                    {
                        events.AddRange(TheyPaired(q));
                    }

                    q.Seen = now;
                    return;
                }

                // It named yet another address while this one was checked:
                // this one answered under its key, so it is stored all the same
                // (MSFP core section 13.2, step 4), and the newer one is checked next.
                superseded = q.Move.Address != m.Address;
                var from = q.Address;
                q.Address = m.Address;
                q.Base = there.Base;
                q.Pins = [.. Pins.Clean(ans.Certificates)];
                if (!superseded)
                {
                    q.Move = null;
                }

                if (from != q.Address)
                {
                    events.Add(new PeerMoved(q.Id, q.Name, from, q.Address));
                }

                if (q.Inbound == Inbound.Confirming)
                {
                    q.Inbound = Inbound.Yes;
                    events.Add(new InboundChanged(q.Id, q.Name, Inbound.Yes));
                }

                // The rest of what it says, as for any hello; an address it
                // names there waits for its next one.
                events.AddRange(KeepHello(q, ans with { Address = null }).Events);
                if (ans.State == Hello.StatePaired)
                {
                    events.AddRange(TheyPaired(q));
                }

                events.AddRange(KeepCallsFailed(q, ans.CallsFailed));
                q.Seen = now;
            }, ct).ConfigureAwait(false);
        }
        catch (MsfpException)
        {
            return false;
        }

        foreach (var ev in events)
        {
            switch (ev)
            {
                case PeerMoved moved:
                    _log.LogInformation("msfp: a peer moved, its new address confirmed: {Peer} {From} {To}", id, moved.From, moved.To);
                    break;
                case InboundChanged:
                    _log.LogInformation("msfp: a peer that couldn't be called can be again, its address confirmed: {Peer}", id);
                    break;
            }
        }

        Notify([.. events]);
        if (events.Any(e => e is InboundChanged { Inbound: var i } && i == Inbound.Yes))
        {
            // What it holds for us was dropped while it couldn't be called:
            // read again now, not at the next reading.
            ReadSoon(id);
        }

        return superseded;
    }
}
