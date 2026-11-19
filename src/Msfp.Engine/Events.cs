namespace Msfp;

/// <summary>
/// Something the operator's panel or the viewers' pages may want to show at
/// once. Tell them apart with a type pattern:
/// <code>
/// if (e is PeerWaiting w) { panel.Show("A server used your code: " + w.Name); }
/// </code>
/// Each event names the peer it concerns by ID (null for an event of this
/// server alone); its state is in the queries too, so a host that missed
/// one loses nothing.
/// </summary>
public abstract record MsfpEvent
{
    /// <summary>The peer the event concerns; null for none.</summary>
    public virtual string? PeerId => null;
}

/// <summary>
/// A peer redeemed our code. It is in state ours: the operator compares
/// fingerprints, then accepts or refuses (End). MSFP core section 7.1, step 4.
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Fingerprint">Its key's fingerprint.</param>
public sealed record PeerWaiting(string Peer, string Name, string Fingerprint) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>A pairing became paired, on either side (MSFP core section 7.1, step 6).</summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
public sealed record PeerPaired(string Peer, string Name) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// A peer answered, signed, that it doesn't know us (MSFP core section 7.3);
/// or it has been suspended 30 days without a valid rotation, and stays
/// suspended (section 12.6).
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
public sealed record PeerGone(string Peer, string Name) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// A pairing ended: by us (ByThem false, End) or by the peer (its hello said
/// ended). MSFP core section 7.4.
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="ByThem">The peer ended it.</param>
public sealed record PeerEnded(string Peer, string Name, bool ByThem) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>A peer's versions changed; the parts on with it may have changed (MSFP core section 10).</summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
public sealed record VersionsChanged(string Peer, string Name) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

// Keys (MSFP core section 12).

/// <summary>
/// A peer revoked its key. Nothing is heard from that key but rotate and
/// revoke, and nothing is asked of the peer: show a warning, and hide what
/// needs it from viewers (section 12.6).
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
public sealed record PeerSuspended(string Peer, string Name) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// A suspended peer rotated to the key it had committed to: the suspension
/// is lifted (section 12.6). <see cref="PeerRotated"/> comes with it.
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
public sealed record PeerResumed(string Peer, string Name) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// A peer moved to a key it hadn't committed to (its last rotation said
/// none): the operator compares Fingerprint with the other operator, then
/// confirms the rotation, or ends the pairing (section 12.3, case 4).
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Fingerprint">The new key's fingerprint.</param>
public sealed record RotationPending(string Peer, string Name, string Fingerprint) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// A peer's rotation to the key of Fingerprint didn't match its commitment,
/// and was refused: someone holding its key tried to move the pairing to
/// another key. A warning (section 12.3, case 3); DismissRefused clears it.
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Fingerprint">The refused key's fingerprint.</param>
public sealed record RotationRefused(string Peer, string Name, string Fingerprint) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>A peer's new key, of Fingerprint, is current (section 12.3).</summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Fingerprint">The new key's fingerprint.</param>
public sealed record PeerRotated(string Peer, string Name, string Fingerprint) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// A peer answered this server's change of key: State "rotated" (it moved),
/// "pending" (its operator must confirm) or "refused" (section 12.4).
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="State">"rotated", "pending" or "refused".</param>
public sealed record RotationAnswered(string Peer, string Name, string State) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// The 30 days of this server's change of key are over (or a next change
/// began) and this peer never moved: it must pair again (section 12.4). A
/// warning.
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
public sealed record OurKeyLost(string Peer, string Name) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// This server's change of key is over, its old key deleted: every peer
/// moved, or Lost of them didn't (each told by <see cref="OurKeyLost"/>). An
/// event of this server: PeerId is null.
/// </summary>
/// <param name="Lost">The peers that didn't move.</param>
public sealed record RotationEnded(int Lost) : MsfpEvent;

// Addresses (MSFP core sections 7.5 and 13).

/// <summary>
/// This server reached itself at its new address, and told its peers: they
/// move to it as they confirm it (section 13.1, step 2). An event of this
/// server: PeerId is null.
/// </summary>
/// <param name="Address">The new address.</param>
public sealed record AddressAnnounced(string Address) : MsfpEvent;

/// <summary>
/// This server's move is over, its former address no longer accepted: every
/// peer moved (NotMoved 0), or 30 days passed and NotMoved of them hadn't
/// (section 13.1). An event of this server.
/// </summary>
/// <param name="NotMoved">The peers that hadn't moved.</param>
public sealed record MoveEnded(int NotMoved) : MsfpEvent;

/// <summary>
/// A peer still calls this server's former address 3 days after the move
/// was announced. A warning (section 13.1).
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
public sealed record NotMovedLate(string Peer, string Name) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// A peer's new address was confirmed (or, for a peer that can't be called,
/// read from its hello) and stored (section 13.2).
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="From">Its former address.</param>
/// <param name="To">Its new address.</param>
public sealed record PeerMoved(string Peer, string Name, string From, string To) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// A peer's new address has failed to answer as it for a day: it is still
/// called at its old one (section 13.2, step 3). A warning, once per move.
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Address">The new address.</param>
/// <param name="Since">Since when it fails.</param>
public sealed record PeerMoveFailing(string Peer, string Name, string Address, DateTimeOffset Since) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// A peer says it can't be called now (Inbound No), or can again
/// (Confirming: its address is checked first), or its address answered and
/// it is called again (Yes); section 7.5.
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Inbound">Whether it can be called now.</param>
public sealed record InboundChanged(string Peer, string Name, Inbound Inbound) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// A peer's hello answer said this server's last calls to it couldn't
/// connect: the panel may suggest "this server can't be called" (section
/// 7.5). Nothing else changes.
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
public sealed record CallsFailed(string Peer, string Name) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

// Catalogs and playbacks.

/// <summary>
/// A reading of a peer's titles ended: Titles kept (new or changed, for a
/// reading of changes), or Error, why it failed; what was kept before a
/// failed reading stays (MSFP catalog section 3.2).
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Titles">The titles kept.</param>
/// <param name="Changes">It was a reading of changes.</param>
/// <param name="Error">Why it failed; null when it didn't.</param>
public sealed record CatalogReadEnded(string Peer, string Name, int Titles, bool Changes, Exception? Error) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// Libraries this server wants of a peer (their public IDs) weren't in its
/// last libraries answer: it no longer offers them (MSFP catalog section
/// 2.2). Their titles go with the next reading.
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Libraries">The libraries' public IDs.</param>
public sealed record WantsGone(string Peer, string Name, IReadOnlyList<string> Libraries) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>A peer's playback of Title started here (MSFP playback section 6.1), for the host's live view.</summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Playback">The playback's ID.</param>
/// <param name="Title">The title's key.</param>
/// <param name="Method">"direct", "remux" or "transcode".</param>
public sealed record PlaybackStarted(string Peer, string Name, string Playback, string Title, string Method) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>A peer's playback here ended, and why.</summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Playback">The playback's ID.</param>
/// <param name="Title">The title's key.</param>
/// <param name="Reason">Why.</param>
public sealed record PlaybackEnded(string Peer, string Name, string Playback, string Title, EndReason Reason) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}

/// <summary>
/// A playback of this server's on a peer ended without the host's Stop: the
/// peer answered 410 (Reason Expired), the pairing ended, the peer was
/// suspended or can't be called (PairingEnded). Its fetches fail from then
/// on; the viewer's player stops.
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Playback">The playback's ID.</param>
/// <param name="Reason">Why.</param>
public sealed record RemotePlaybackEnded(string Peer, string Name, string Playback, EndReason Reason) : MsfpEvent
{
    /// <inheritdoc/>
    public override string? PeerId => Peer;
}
