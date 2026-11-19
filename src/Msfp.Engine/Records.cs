using System.Text.Json.Serialization;

namespace Msfp;

// The records a Store keeps (host.md section 1). The engine fills every
// field; a store keeps them as they are, in JSON (the attributes are for
// the shipped stores, and give the Go engine's names) or in columns of its
// own. Times are UTC; null means "never" or "none" (Go's zero time).
//
// They are records with settable properties: the engine changes a copy it
// read and puts it back; a store's getters return copies (deep ones: the
// shipped stores keep the JSON), so nothing changes until it is put back.

/// <summary>This server's own: its keys, its address, its settings. There is one.</summary>
public sealed record ServerRecord
{
    /// <summary>The current key's seed, sealed ("msfp current key").</summary>
    [JsonPropertyName("current")]
    public byte[] Current { get; set; } = [];

    /// <summary>
    /// The next key's seed, sealed ("msfp next key"); null once the operator
    /// exported and deleted it.
    /// </summary>
    [JsonPropertyName("next"), OmitEmpty]
    public byte[]? Next { get; set; }

    /// <summary>
    /// SHA-256 of the next key's public key; it stays when <see cref="Next"/>
    /// is deleted (MSFP core section 12.5).
    /// </summary>
    [JsonPropertyName("nextCommitment")]
    public byte[]? NextCommitment { get; set; }

    /// <summary>
    /// The key before <see cref="Current"/> while peers move from it, sealed
    /// ("msfp old key"), since <see cref="OldSince"/> (MSFP core sections 12.4 and 12.7).
    /// </summary>
    [JsonPropertyName("old"), OmitEmpty]
    public byte[]? Old { get; set; }

    /// <summary>Since when <see cref="Old"/> is kept.</summary>
    [JsonPropertyName("oldSince"), OmitEmpty]
    public DateTimeOffset? OldSince { get; set; }

    /// <summary>The old key was revoked first.</summary>
    [JsonPropertyName("oldRevoked"), OmitEmpty]
    public bool OldRevoked { get; set; }

    /// <summary>
    /// A key revoked with no next key, replaced by Current, sealed ("msfp
    /// revoked key"), since <see cref="RevokedAt"/>: kept 30 days for the
    /// answers to the hellos of the peers whose pairing ended then (MSFP core
    /// section 12.7, case 2).
    /// </summary>
    [JsonPropertyName("revoked"), OmitEmpty]
    public byte[]? Revoked { get; set; }

    /// <summary>When <see cref="Revoked"/> was revoked.</summary>
    [JsonPropertyName("revokedAt"), OmitEmpty]
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>The secret of opaque IDs, sealed ("msfp ids secret"). It never changes.</summary>
    [JsonPropertyName("idsSecret")]
    public byte[] IdsSecret { get; set; } = [];

    /// <summary>
    /// Where this server is now: the address noted last, or the
    /// <see cref="UncallableAddress"/> while it can't be called.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    /// <summary>The address before <see cref="Address"/>, while a move lasts (MSFP core section 13.1).</summary>
    [JsonPropertyName("former"), OmitEmpty]
    public string? Former { get; set; }

    /// <summary>When the address changed, while a move lasts.</summary>
    [JsonPropertyName("movedAt"), OmitEmpty]
    public DateTimeOffset? MovedAt { get; set; }

    /// <summary>
    /// This server reached itself at <see cref="Address"/>, so that its move
    /// is announced (until then its hellos name <see cref="Former"/>).
    /// </summary>
    [JsonPropertyName("reached"), OmitEmpty]
    public bool Reached { get; set; }

    /// <summary>The operator's setting "this server can't be called" (MSFP core section 7.5).</summary>
    [JsonPropertyName("uncallable"), OmitEmpty]
    public bool Uncallable { get; set; }

    /// <summary>The https://msfp-&lt;26&gt;.invalid address made for <see cref="Uncallable"/>, kept.</summary>
    [JsonPropertyName("uncallableAddress"), OmitEmpty]
    public string? UncallableAddress { get; set; }
}

/// <summary>
/// Where a pairing stands (MSFP core section 7.3): "ours", "theirs",
/// "paired" or "gone", written as that string. A string, not an enum, so
/// that a record read back keeps what it held, the empty value included.
/// </summary>
[JsonConverter(typeof(PeerStateConverter))]
public readonly struct PeerState : IEquatable<PeerState>
{
    private readonly string? value;

    /// <summary>A state of this string.</summary>
    public PeerState(string value) => this.value = value;

    /// <summary>The peer used our code; our operator hasn't accepted.</summary>
    public static PeerState Ours { get; } = new("ours");

    /// <summary>We used the peer's code; its operator hasn't accepted.</summary>
    public static PeerState Theirs { get; } = new("theirs");

    /// <summary>Both sides accepted.</summary>
    public static PeerState Paired { get; } = new("paired");

    /// <summary>The peer answered, signed, that it doesn't know us.</summary>
    public static PeerState Gone { get; } = new("gone");

    /// <summary>The state's string ("" for none).</summary>
    public string Value => value ?? "";

    /// <summary>Whether two states are the same.</summary>
    public static bool operator ==(PeerState a, PeerState b) => a.Equals(b);

    /// <summary>Whether two states differ.</summary>
    public static bool operator !=(PeerState a, PeerState b) => !a.Equals(b);

    /// <inheritdoc/>
    public bool Equals(PeerState other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PeerState other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// Whether a peer can be called (MSFP core section 7.5): "yes", "no" or
/// "confirming", written as that string.
/// </summary>
[JsonConverter(typeof(InboundConverter))]
public readonly struct Inbound : IEquatable<Inbound>
{
    private readonly string? value;

    /// <summary>A value of this string.</summary>
    public Inbound(string value) => this.value = value;

    /// <summary>It can be called.</summary>
    public static Inbound Yes { get; } = new("yes");

    /// <summary>It said it can't be called now.</summary>
    public static Inbound No { get; } = new("no");

    /// <summary>It can be called again, its address checked first.</summary>
    public static Inbound Confirming { get; } = new("confirming");

    /// <summary>The value's string ("" for none).</summary>
    public string Value => value ?? "";

    /// <summary>Whether two values are the same.</summary>
    public static bool operator ==(Inbound a, Inbound b) => a.Equals(b);

    /// <summary>Whether two values differ.</summary>
    public static bool operator !=(Inbound a, Inbound b) => !a.Equals(b);

    /// <inheritdoc/>
    public bool Equals(Inbound other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Inbound other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>A server this one is paired, or pairing, with.</summary>
public sealed record PeerRecord
{
    // Identity.

    /// <summary>The engine's own ID, random and stable; peers never see it.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>The peer's current public key.</summary>
    [JsonPropertyName("key")]
    public byte[] Key { get; set; } = [];

    /// <summary>Its key before its latest rotation, still heard until <see cref="PreviousUntil"/>.</summary>
    [JsonPropertyName("previousKey"), OmitEmpty]
    public byte[]? PreviousKey { get; set; }

    /// <summary>Until when <see cref="PreviousKey"/> is heard.</summary>
    [JsonPropertyName("previousUntil"), OmitEmpty]
    public DateTimeOffset? PreviousUntil { get; set; }

    /// <summary>The peer's name, cleaned.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>When the record was made.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; }

    // State.

    /// <summary>Where the pairing stands.</summary>
    [JsonPropertyName("state")]
    public PeerState State { get; set; }

    /// <summary>It was paired once, so that a gone peer's paired hello brings it back.</summary>
    [JsonPropertyName("wasPaired"), OmitEmpty]
    public bool WasPaired { get; set; }

    /// <summary>When it became paired.</summary>
    [JsonPropertyName("paired"), OmitEmpty]
    public DateTimeOffset? Paired { get; set; }

    /// <summary>When it was last heard from.</summary>
    [JsonPropertyName("seen"), OmitEmpty]
    public DateTimeOffset? Seen { get; set; }

    /// <summary>When it revoked its key; null when it isn't suspended.</summary>
    [JsonPropertyName("suspended"), OmitEmpty]
    public DateTimeOffset? Suspended { get; set; }

    // Address.

    /// <summary>Where to call it.</summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    /// <summary>Its base path from its discovery document ("" for the default, /api/v1/federation/).</summary>
    [JsonPropertyName("base"), OmitEmpty]
    public string? Base { get; set; }

    /// <summary>An address it announced, not confirmed yet.</summary>
    [JsonPropertyName("move"), OmitEmpty]
    public PeerMove? Move { get; set; }

    /// <summary>
    /// The host of ours (host[:port]) its requests were last signed for: it
    /// moved to our new address once that is our current host.
    /// </summary>
    [JsonPropertyName("ourAddress"), OmitEmpty]
    public string? OurAddress { get; set; }

    /// <summary>Whether it can be called.</summary>
    [JsonPropertyName("inbound")]
    public Inbound Inbound { get; set; }

    /// <summary>Its latest hello answer said our calls to it couldn't connect.</summary>
    [JsonPropertyName("callsFailed"), OmitEmpty]
    public bool CallsFailed { get; set; }

    /// <summary>What its latest signed hello listed (null before the first).</summary>
    [JsonPropertyName("versions"), OmitEmpty]
    public WireVersions? Versions { get; set; }

    /// <summary>Its certificates' pins, from the same hello.</summary>
    [JsonPropertyName("pins"), OmitEmpty]
    public List<string>? Pins { get; set; }

    // Its keys.

    /// <summary>Its next key commitment.</summary>
    [JsonPropertyName("commitment"), OmitEmpty]
    public byte[]? Commitment { get; set; }

    /// <summary>Where <see cref="Commitment"/> came from ("pair", "rotation"; "" for none).</summary>
    [JsonPropertyName("commitmentFrom"), OmitEmpty]
    public string? CommitmentFrom { get; set; }

    /// <summary>A rotation to a key with no commitment, waiting for our operator.</summary>
    [JsonPropertyName("pending"), OmitEmpty]
    public WireRotation? Pending { get; set; }

    /// <summary>The rotation statements kept.</summary>
    [JsonPropertyName("rotations"), OmitEmpty]
    public List<WireRotation>? Rotations { get; set; }

    /// <summary>A rotation refused, for the operator.</summary>
    [JsonPropertyName("refused"), OmitEmpty]
    public RefusedKey? Refused { get; set; }

    // Our keys toward it.

    /// <summary>
    /// What it knows us by: "" (our current key), "old" (it hasn't moved to
    /// our new one yet) or "lost" (it never did).
    /// </summary>
    [JsonPropertyName("ourKey"), OmitEmpty]
    public string? OurKey { get; set; }

    /// <summary>Its last answer to our rotate ("pending", "refused" or "").</summary>
    [JsonPropertyName("rotationAnswer"), OmitEmpty]
    public string? RotationAnswer { get; set; }

    /// <summary>When we last sent it a rotate.</summary>
    [JsonPropertyName("rotateSent"), OmitEmpty]
    public DateTimeOffset? RotateSent { get; set; }

    /// <summary>It holds our next key's commitment.</summary>
    [JsonPropertyName("commitmentTold"), OmitEmpty]
    public bool CommitmentTold { get; set; }

    // Sharing.

    /// <summary>The host libraries offered to it.</summary>
    [JsonPropertyName("offers"), OmitEmpty]
    public List<string>? Offers { get; set; }

    /// <summary>The change number the offers were set at.</summary>
    [JsonPropertyName("offersSeq"), OmitEmpty]
    public long OffersSeq { get; set; }

    /// <summary>Its libraries, and those of them we want.</summary>
    [JsonPropertyName("wants"), OmitEmpty]
    public List<WantedLibrary>? Wants { get; set; }

    /// <summary>Our limits to it.</summary>
    [JsonPropertyName("limits")]
    public Limits Limits { get; set; } = new();

    /// <summary>Its limits to us (null until it said).</summary>
    [JsonPropertyName("theirLimits"), OmitEmpty]
    public Limits? TheirLimits { get; set; }

    /// <summary>Where our reading of its titles stands.</summary>
    [JsonPropertyName("catalogRead")]
    public CatalogRead CatalogRead { get; set; } = new();
}

/// <summary>
/// An address a peer announced, being checked (MSFP core section 13.2).
/// </summary>
public sealed record PeerMove
{
    /// <summary>The address announced.</summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    /// <summary>Since when.</summary>
    [JsonPropertyName("since")]
    public DateTimeOffset Since { get; set; }

    /// <summary>When a confirming hello was last sent there.</summary>
    [JsonPropertyName("tried"), OmitEmpty]
    public DateTimeOffset? Tried { get; set; }

    /// <summary>Since when those failed.</summary>
    [JsonPropertyName("failingSince"), OmitEmpty]
    public DateTimeOffset? FailingSince { get; set; }

    /// <summary>The operator was warned of it (after a day).</summary>
    [JsonPropertyName("warned"), OmitEmpty]
    public bool Warned { get; set; }
}

/// <summary>A key a peer tried to rotate to, refused.</summary>
public sealed record RefusedKey
{
    /// <summary>The key.</summary>
    [JsonPropertyName("key")]
    public byte[] Key { get; set; } = [];

    /// <summary>When.</summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }
}

/// <summary>
/// Where the reading of a peer's titles stands: the change number read up
/// to, and the wants it was read with.
/// </summary>
public sealed record CatalogRead
{
    /// <summary>The change number read up to.</summary>
    [JsonPropertyName("seq"), OmitEmpty]
    public long Seq { get; set; }

    /// <summary>The wants it was read with.</summary>
    [JsonPropertyName("wants"), OmitEmpty]
    public List<string>? Wants { get; set; }
}

/// <summary>A pairing code this server made.</summary>
public sealed record CodeRecord
{
    /// <summary>Its ID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Its secret, sealed ("msfp code secret").</summary>
    [JsonPropertyName("secret")]
    public byte[]? Secret { get; set; }

    /// <summary>The hash of its secret, by which it is found.</summary>
    [JsonPropertyName("secretHash")]
    public byte[]? SecretHash { get; set; }

    /// <summary>The operator's note.</summary>
    [JsonPropertyName("note"), OmitEmpty]
    public string? Note { get; set; }

    /// <summary>When it was made.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; }

    /// <summary>When it expires.</summary>
    [JsonPropertyName("expires")]
    public DateTimeOffset Expires { get; set; }
}

/// <summary>
/// A key whose pairing ended (MSFP core section 7.4), by its SHA-256: its
/// requests get a signed 404. Revoked: the pairing ended when this
/// server's key was revoked with no next key (section 12.7, case 2), at
/// <see cref="At"/>, the server record's RevokedAt: for 30 days its hellos
/// get the revocation.
/// </summary>
public sealed record ForgottenKey
{
    /// <summary>The SHA-256 of the key.</summary>
    [JsonPropertyName("keyHash")]
    public byte[] KeyHash { get; set; } = [];

    /// <summary>When it was forgotten.</summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    /// <summary>The pairing ended with this server's key revoked.</summary>
    [JsonPropertyName("revoked"), OmitEmpty]
    public bool Revoked { get; set; }
}

/// <summary>
/// A playback this server started on a peer (the peer's playback ID), to
/// stop after a restart.
/// </summary>
/// <param name="Peer">The peer's ID.</param>
/// <param name="PlaybackId">The peer's playback ID.</param>
public sealed record Leftover(
    [property: JsonPropertyName("peer")] string Peer,
    [property: JsonPropertyName("playbackId")] string PlaybackId);
