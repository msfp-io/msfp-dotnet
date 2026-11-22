using Msfp.Protocol;

namespace Msfp;

/// <summary>What the panel shows of a peer: a copy the host may keep.</summary>
public sealed record PeerView
{
    /// <summary>The engine's ID of the peer: random, stable, never its key.</summary>
    public required string Id { get; init; }

    /// <summary>Its name, cleaned: text, never markup.</summary>
    public required string Name { get; init; }

    /// <summary>Its address.</summary>
    public required string Address { get; init; }

    /// <summary>Its current key's fingerprint, for the operators to compare (core section 4.2).</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Where the pairing stands.</summary>
    public PeerState State { get; init; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>When it was paired; null for never.</summary>
    public DateTimeOffset? Paired { get; init; }

    /// <summary>When it was last heard from; null for never.</summary>
    public DateTimeOffset? Seen { get; init; }

    /// <summary>When it revoked its key; null when it didn't.</summary>
    public DateTimeOffset? Suspended { get; init; }

    /// <summary>The core version on with it; -1 when there is none in common (the two can't talk).</summary>
    public int Core { get; init; }

    /// <summary>The parts on with it, by name, with their versions.</summary>
    public IReadOnlyDictionary<string, int> Parts { get; init; } = new Dictionary<string, int>();

    /// <summary>Whether it can be called.</summary>
    public Inbound Inbound { get; init; }

    /// <summary>Its latest hello answer said our calls to it couldn't connect.</summary>
    public bool CallsFailed { get; init; }

    /// <summary>Where its next key commitment came from: "pair", "rotation", or "" for none.</summary>
    public string CommitmentFrom { get; init; } = "";

    /// <summary>What it knows this server by: <see cref="MsfpEngine.KeyCurrent"/>, <see cref="MsfpEngine.KeyOld"/> or <see cref="MsfpEngine.KeyLost"/>.</summary>
    public string OurKey { get; init; } = "";

    /// <summary>A new key of its waiting for the operator to confirm (<see cref="MsfpEngine.ConfirmRotationAsync"/>), with the statement's date; null for none.</summary>
    public KeyNotice? Pending { get; init; }

    /// <summary>A rotation of its refused, and when (<see cref="MsfpEngine.DismissRefusedAsync"/>); null for none.</summary>
    public KeyNotice? Refused { get; init; }

    /// <summary>What it may use here.</summary>
    public Limits Limits { get; init; } = new();

    /// <summary>What it lets this server use of it, as its last libraries answer said; null until it did.</summary>
    public Limits? TheirLimits { get; init; }

    /// <summary>During this server's move, announced: it still signs for our former address.</summary>
    public bool NotMoved { get; init; }

    /// <summary>It hasn't moved after some days (core section 13.1).</summary>
    public bool MoveLate { get; init; }

    /// <summary>Its new address being confirmed; null for none.</summary>
    public MoveView? Moving { get; init; }

    /// <summary>Its last answer to our rotate while it is on our old key: "pending" or "refused"; "" for none.</summary>
    public string RotationAnswer { get; init; } = "";

    /// <summary>How many of the host's libraries are offered to it.</summary>
    public int Offers { get; init; }

    /// <summary>The individual relation shares the host's local titles too (SetLocalAsync).</summary>
    public bool Local { get; init; }

    /// <summary>How many of its libraries this server reads.</summary>
    public int Wants { get; init; }

    /// <summary>How many of those it no longer offers (WantsGone).</summary>
    public int WantsGone { get; init; }

    /// <summary>The group relations: the groups this link was made or kept through (MSFP core section 7.7).</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>The individual relation holds: both sides want it (MSFP core section 7.7).</summary>
    public bool Individual { get; init; } = true;

    /// <summary>This side asks for the individual relation, and the peer hasn't accepted.</summary>
    public bool IndividualAsked { get; init; }

    /// <summary>The peer asks for the individual relation, waiting for this side's operator (AskIndividualAsync accepts, RefuseIndividualAsync refuses).</summary>
    public bool IndividualAsks { get; init; }

    /// <summary>A dissolution left the link to this side's operator: to keep (AskIndividualAsync) or end (RefuseIndividualAsync).</summary>
    public bool KeepChoice { get; init; }

    /// <summary><see cref="Individual"/>, under its former name.</summary>
    public bool ByHand { get; init; } = true;

    /// <summary>It used one of our codes for this group and waits for our operator (state ours): a request to join that group.</summary>
    public string JoiningGroup { get; init; } = "";
}

/// <summary>A peer's key the operator is shown: its fingerprint, and when.</summary>
/// <param name="Fingerprint">The key's fingerprint.</param>
/// <param name="At">When.</param>
public sealed record KeyNotice(string Fingerprint, DateTimeOffset At);

/// <summary>A peer's new address being confirmed (core section 13.2).</summary>
/// <param name="Address">The address it named.</param>
/// <param name="Since">When it first named it.</param>
/// <param name="FailingSince">When the confirmation began failing; null while it hasn't failed.</param>
/// <param name="Warn">A day of failures: the operator is warned.</param>
public sealed record MoveView(string Address, DateTimeOffset Since, DateTimeOffset? FailingSince, bool Warn);

/// <summary>What the panel shows of this server.</summary>
public sealed record ServerView
{
    /// <summary>The current key's fingerprint.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Where this server is now (its address of the .invalid form while it can't be called).</summary>
    public required string Address { get; init; }

    /// <summary>What its hellos name: its former address while the new one isn't reached yet.</summary>
    public required string Named { get; init; }

    /// <summary>The address it moved from, still accepted; "" for no move under way (or one from the .invalid form).</summary>
    public string Former { get; init; } = "";

    /// <summary>When the move began; null for no move under way.</summary>
    public DateTimeOffset? MovedAt { get; init; }

    /// <summary>The new address doesn't answer yet, so peers aren't told.</summary>
    public bool Waiting { get; init; }

    /// <summary>During a move, the peers that aren't gone which sign for the new address.</summary>
    public int PeersMoved { get; init; }

    /// <summary>During a move, the peers that aren't gone.</summary>
    public int PeersToMove { get; init; }

    /// <summary>The operator's "this server can't be called".</summary>
    public bool Uncallable { get; init; }

    /// <summary>The discovery document is served.</summary>
    public bool Discovery { get; init; }

    /// <summary>The committed next key's fingerprint (from its commitment, core section 4.2).</summary>
    public required string NextKey { get; init; }

    /// <summary>Whether the next key's seed is still here.</summary>
    public bool NextOnServer { get; init; }

    /// <summary>When this server's change of key began; null when none is under way.</summary>
    public DateTimeOffset? RotationSince { get; init; }

    /// <summary>How many peers still know this server by its old key.</summary>
    public int NotRotated { get; init; }

    /// <summary>
    /// When this server's key was revoked with no next key; for 30 days,
    /// former peers' hellos get the revocation. Null for none.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>The core versions spoken.</summary>
    public IReadOnlyList<int> Core { get; init; } = [];

    /// <summary>The parts served, with their versions.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> Parts { get; init; } = new Dictionary<string, IReadOnlyList<int>>();
}

public sealed partial class MsfpEngine
{
    internal PeerView View(PeerRecord p)
    {
        var agreed = Agreed(p);
        var a = OurAddress();
        var notMoved = a.Former is not null && a.Reached && NotMoved(p, Address.Host(a.Address));
        return new PeerView
        {
            Id = p.Id, Name = p.Name, Address = p.Address, Fingerprint = Keys.Fingerprint(p.Key), State = p.State,
            Created = p.Created, Paired = p.Paired, Seen = p.Seen, Suspended = p.Suspended,
            Core = agreed?.Core ?? -1, Parts = agreed?.Parts ?? new Dictionary<string, int>(),
            Inbound = p.Inbound, CallsFailed = p.CallsFailed, CommitmentFrom = p.CommitmentFrom ?? "",
            OurKey = p.OurKey ?? "",
            Pending = p.Pending?.New is { } pk ? new KeyNotice(Keys.Fingerprint(pk), DateTimeOffset.FromUnixTimeSeconds(p.Pending.Date)) : null,
            Refused = p.Refused is { } rk ? new KeyNotice(Keys.Fingerprint(rk.Key), rk.At) : null, Limits = p.Limits with { }, TheirLimits = p.TheirLimits is null ? null : p.TheirLimits with { },
            RotationAnswer = p.RotationAnswer ?? "", NotMoved = notMoved, MoveLate = notMoved && a.MovedAt is { } at && Now() - at >= MoveLate,
            Moving = p.Move is { } m ? new MoveView(m.Address, m.Since, m.FailingSince, new AddressMove(m.Address, null, m.FailingSince).Warn(Now())) : null,
            Offers = p.Offers?.Count ?? 0, Local = p.Local, Wants = p.Wants?.Count(w => w.Wanted) ?? 0, WantsGone = p.Wants?.Count(w => w.Wanted && w.Gone) ?? 0,
            Groups = [.. p.Groups ?? []], Individual = Individual(p), ByHand = Individual(p),
            IndividualAsked = Asked(p), IndividualAsks = Asks(p) && !p.KeepChoice, KeepChoice = p.KeepChoice,
            JoiningGroup = p.JoiningGroup ?? "",
        };
    }

    /// <summary>Every peer, in every state, by name.</summary>
    public async Task<IReadOnlyList<PeerView>> PeersAsync(CancellationToken ct = default)
    {
        List<PeerView> output = [];
        await _store.ReadAsync(async tx => output = (await tx.PeersAsync().ConfigureAwait(false)).Select(View).ToList(), ct)
            .ConfigureAwait(false);
        return output.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>One peer.</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public async Task<PeerView> PeerAsync(string id, CancellationToken ct = default) =>
        View(await PeerRecordAsync(id, ct).ConfigureAwait(false));

    /// <summary>This server, as the panel shows it.</summary>
    public async Task<ServerView> ServerAsync(CancellationToken ct = default)
    {
        var r = await ServerRecordAsync(ct).ConfigureAwait(false);
        IReadOnlyList<PeerRecord> peers = [];
        await _store.ReadAsync(async tx => peers = await tx.PeersAsync().ConfigureAwait(false), ct).ConfigureAwait(false);
        var moving = !string.IsNullOrEmpty(r.Former);
        var host = Address.Host(r.Address);
        var v = OurVersions();
        return new ServerView
        {
            Fingerprint = Current().Fingerprint, Address = r.Address, Named = Named(), Uncallable = r.Uncallable, Discovery = _discovery,
            NextKey = Keys.CommitmentFingerprint(r.NextCommitment ?? []), NextOnServer = r.Next is not null,
            RotationSince = r.Old is null ? null : r.OldSince, NotRotated = peers.Count(p => p.OurKey == KeyOld),
            RevokedAt = r.RevokedAt, Core = v.Core, Parts = v.Parts,
            Former = moving && !Uncallables.Valid(r.Former) ? r.Former! : "",
            MovedAt = moving ? r.MovedAt : null, Waiting = moving && !r.Reached,
            PeersToMove = moving ? peers.Count(p => p.State != PeerState.Gone) : 0,
            PeersMoved = moving ? peers.Count(p => p.State != PeerState.Gone && !NotMoved(p, host)) : 0,
        };
    }
}
