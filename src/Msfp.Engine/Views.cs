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

    /// <summary>The core version on with it; 0 when there is none in common (the two can't talk).</summary>
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
}

/// <summary>A peer's key the operator is shown: its fingerprint, and when.</summary>
/// <param name="Fingerprint">The key's fingerprint.</param>
/// <param name="At">When.</param>
public sealed record KeyNotice(string Fingerprint, DateTimeOffset At);

/// <summary>What the panel shows of this server.</summary>
/// <param name="Fingerprint">The current key's.</param>
/// <param name="Address">Where this server is now.</param>
/// <param name="Named">What its hellos name.</param>
/// <param name="Uncallable">The operator's "this server can't be called".</param>
/// <param name="NextKey">The committed next key's fingerprint.</param>
/// <param name="NextOnServer">Whether the next key's seed is still here.</param>
public sealed record ServerView(string Fingerprint, string Address, string Named, bool Uncallable, string NextKey, bool NextOnServer);

public sealed partial class MsfpEngine
{
    internal PeerView View(PeerRecord p)
    {
        var agreed = Agreed(p);
        return new PeerView
        {
            Id = p.Id, Name = p.Name, Address = p.Address, Fingerprint = Keys.Fingerprint(p.Key), State = p.State,
            Created = p.Created, Paired = p.Paired, Seen = p.Seen, Suspended = p.Suspended,
            Core = agreed?.Core ?? 0, Parts = agreed?.Parts ?? new Dictionary<string, int>(),
            Inbound = p.Inbound, CallsFailed = p.CallsFailed, CommitmentFrom = p.CommitmentFrom ?? "",
            OurKey = p.OurKey ?? "",
            Pending = p.Pending?.New is { } pk ? new KeyNotice(Keys.Fingerprint(pk), DateTimeOffset.FromUnixTimeSeconds(p.Pending.Date)) : null,
            Refused = p.Refused is { } rk ? new KeyNotice(Keys.Fingerprint(rk.Key), rk.At) : null, Limits = p.Limits with { }, TheirLimits = p.TheirLimits is null ? null : p.TheirLimits with { },
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
        var a = OurAddress();
        return new ServerView(Current().Fingerprint, a.Uncallable ? a.UncallableAddress ?? a.Address : a.Address, Named(),
            a.Uncallable, Keys.CommitmentFingerprint(r.NextCommitment ?? []), r.Next is not null);
    }
}
