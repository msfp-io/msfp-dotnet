using Microsoft.Extensions.Logging;

namespace Msfp;

// Relations (MSFP core section 7.7): the operator manages why two servers
// are linked, not the link. The individual relation holds while both sides
// want it (NotByHand false here, and the peer's last hello said individual
// true); a group relation for each group in PeerRecord.Groups. A link lasts
// while one relation holds.
public sealed partial class MsfpEngine
{
    // Whether the peer wants the individual relation: what it last said, or,
    // never said, what this side wants.
    internal static bool TheirIndividual(PeerRecord p) => p.TheirIndividual ?? !p.NotByHand;

    // Whether the individual relation holds: both sides want it.
    internal static bool Individual(PeerRecord p) => !p.NotByHand && TheirIndividual(p);

    private static bool Asked(PeerRecord p) => !p.NotByHand && !TheirIndividual(p);

    private static bool Asks(PeerRecord p) => p.NotByHand && TheirIndividual(p) && !p.IndividualRefused;

    // Keeps a hello's individual: a peer that stops wanting the relation
    // while it held ends it for both (core section 7.7).
    private static List<MsfpEvent> KeepIndividual(PeerRecord p, bool? theirs)
    {
        if (theirs is not { } t)
        {
            return [];
        }

        var (held, asked, asks) = (Individual(p), Asked(p), Asks(p));
        // Ended by them only if they said they wanted it: a peer never heard
        // saying it, whose first word is false, never had it.
        var saidHeld = held && p.TheirIndividual == true;
        p.TheirIndividual = t;
        if (saidHeld && !t)
        {
            p.NotByHand = true; // it ended the relation: ended here too
        }

        if (!t)
        {
            p.IndividualRefused = false;
        }

        return held != Individual(p) || asked != Asked(p) || asks != Asks(p) ? [new RelationsChanged(p.Id, p.Name)] : [];
    }

    // Whether a paired link still has a reason to be: a group relation, a
    // group it is being joined through, an individual relation either side
    // wants (an ask waiting for its answer included), or our operator
    // choosing whether to keep it.
    private static bool Holds(PeerRecord p, bool joiningVia) =>
        p.Groups is { Count: > 0 } || joiningVia || !string.IsNullOrEmpty(p.JoiningGroup) || !p.NotByHand ||
        (TheirIndividual(p) && !p.IndividualRefused) || p.KeepChoice;

    // Whether the link with this peer, paired, has no relation left (core
    // section 7.7, "When the last relation goes").
    private static async ValueTask<bool> UnheldAsync(IStoreTx tx, PeerRecord p)
    {
        if (p.State != PeerState.Paired)
        {
            return false;
        }

        var via = tx is IGroupsTx gt && (await gt.GroupsAsync().ConfigureAwait(false)).Any(g => g.Via == p.Id);
        return !Holds(p, via);
    }

    // After a hello kept the peer's individual: when the relation changed,
    // what the peer is offered changed; a link with no relation left ends.
    private async Task RelationsKeptAsync(string id, List<MsfpEvent> events, CancellationToken ct)
    {
        if (!events.OfType<RelationsChanged>().Any())
        {
            return;
        }

        await _store.UpdateAsync(async tx =>
        {
            if (await tx.PeerAsync(id).ConfigureAwait(false) is { } q)
            {
                q.OffersSeq = await OffersSeqAsync(tx, Interlocked.Read(ref _seqSeen)).ConfigureAwait(false);
                await tx.PutPeerAsync(q).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
        EndIfUnheld(id);
    }

    // Ends the link now when no relation holds it.
    private async Task EndNowIfUnheldAsync(string id, CancellationToken ct)
    {
        var end = false;
        await _store.ReadAsync(async tx =>
        {
            var p = await tx.PeerAsync(id).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
            end = await UnheldAsync(tx, p).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (end)
        {
            _log.LogInformation("msfp: no relation holds the link any more: it ends: {Peer}", id);
            await EndAsync(id, ct).ConfigureAwait(false);
        }
    }

    // Ends, in the background, the link with this peer when no relation
    // holds it any more.
    private void EndIfUnheld(string id) => Background(async ct =>
    {
        try
        {
            await EndNowIfUnheldAsync(id, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
        }
    });

    // Sets whether this side wants the individual relation, and tells the
    // peer at once (a peer that can't be called learns at its next hello).
    private async Task<PeerView> SetIndividualAsync(string id, bool want, CancellationToken ct)
    {
        PeerRecord? p = null;
        await _store.UpdateAsync(async tx =>
        {
            var q = await tx.PeerAsync(id).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
            if (q.State != PeerState.Paired)
            {
                throw new MsfpException(MsfpError.NotPaired);
            }

            var was = Individual(q);
            q.NotByHand = !want;
            q.KeepChoice = false;
            if (want)
            {
                q.IndividualRefused = false;
            }

            if (was != Individual(q))
            {
                // What it is offered changes (its individual relation's offers).
                q.OffersSeq = await OffersSeqAsync(tx, Interlocked.Read(ref _seqSeen)).ConfigureAwait(false);
            }

            p = q;
            await tx.PutPeerAsync(q).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        Notify(new RelationsChanged(p!.Id, p.Name));
        if (p.Inbound == Inbound.Yes && p.Suspended is null)
        {
            try
            {
                await CheckAsync(id, ct).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogInformation(e, "msfp: the peer couldn't be told of the individual relation yet; it learns at the next hello: {Peer}", id);
            }
        }

        return await PeerAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks a peer linked through a group for the individual relation, or
    /// accepts its ask (MSFP core section 7.7); also keeps a link a
    /// dissolution left to the operator's choice.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotPaired"/>.</exception>
    public Task<PeerView> AskIndividualAsync(string id, CancellationToken ct = default) => SetIndividualAsync(id, true, ct);

    /// <summary>
    /// Ends the individual relation with a peer, or withdraws this server's
    /// ask: the link stays while a group holds it, and ends otherwise,
    /// without keeping the peer away (MSFP core section 7.7).
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public async Task EndIndividualAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await SetIndividualAsync(id, false, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotPaired)
        {
            await EndAsync(id, ct).ConfigureAwait(false);
            return;
        }

        await EndNowIfUnheldAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a peer's ask for the individual relation, or ends a link a
    /// dissolution left to the operator's choice: privately, nothing sent
    /// (MSFP core section 7.7); a link nothing else holds ends.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public async Task RefuseIndividualAsync(string id, CancellationToken ct = default)
    {
        var p = await UpdatePeerAsync(id, q =>
        {
            q.IndividualRefused = true;
            q.KeepChoice = false;
        }, ct).ConfigureAwait(false);
        Notify(new RelationsChanged(p.Id, p.Name));
        await EndNowIfUnheldAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the link with a peer whatever its relations, and keeps it away
    /// from every group: its keys are recorded blocked (MSFP core section
    /// 7.4). <see cref="BlockAsync"/> does it for a group's member.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public Task BlockPeerAsync(string id, CancellationToken ct = default) => EndLinkAsync(id, blocked: true, ct);
}
