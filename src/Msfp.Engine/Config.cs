using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Msfp;

/// <summary>
/// What the host gives the engine when it opens it. Only
/// <see cref="Address"/> (but for a server that can't be called) and
/// <see cref="Store"/> are required; every other member has a safe default.
/// </summary>
/// <remarks>
/// Two of Go's members belong to the HTTP glue (Msfp.AspNetCore), not here:
/// ClientAddress (the address a request comes from, as the host's trusted
/// proxies say) and NotFound (the handler for refused requests).
/// </remarks>
public sealed record MsfpConfig
{
    /// <summary>
    /// This server's address, https://host[:port], without a path (MSFP core
    /// section 5.2). Another than at the last start is a move. While this
    /// server can't be called it is kept for when it can again; it may be
    /// empty for a server whose stored record says it can't be called.
    /// </summary>
    public required string Address { get; init; }

    /// <summary>
    /// Keeps the engine's records: <see cref="Stores.JsonFileStore"/> for a
    /// small host, <see cref="Stores.MemoryStore"/> for tests, or the host's
    /// own database.
    /// </summary>
    public required IStore Store { get; init; }

    /// <summary>
    /// The name peers show for this server, read at each pair and hello (a
    /// callback, since a host's branding may change it). Default: the host of
    /// Address.
    /// </summary>
    public Func<string>? Name { get; init; }

    /// <summary>
    /// Seals the keys and secrets stored. Default: the Store's own
    /// (<see cref="ISealerProvider"/>); opening fails when there is neither.
    /// </summary>
    public ISealer? Sealer { get; init; }

    /// <summary>
    /// Where the host mounts the routes, before /api/v1/federation/ and
    /// /api/v1/stream/; "" by default. Segments of A-Z a-z 0-9 . _ ~ - only,
    /// as "/srv/media" (MSFP core section 11).
    /// </summary>
    public string Prefix { get; init; } = "";

    /// <summary>
    /// Serves the discovery document at /.well-known/msfp. Off by default: a
    /// server that serves it tells anyone it federates (MSFP core sections 11
    /// and 14).
    /// </summary>
    public bool Discovery { get; init; }

    /// <summary>
    /// This server's certificate pins, named in its hellos (MSFP core section
    /// 3.2). Null, by default: the engine reads them, connecting to its own
    /// address as a peer would.
    /// </summary>
    public IReadOnlyList<string>? Pins { get; init; }

    /// <summary>Lets the default dialer connect to peers on this machine (MSFP core section 3.3). Off by default.</summary>
    public bool AllowLoopback { get; init; }

    /// <summary>
    /// Accepts and makes http pairings, for a host's own tests only (MSFP core
    /// section 3.1); the engine logs a warning at each start with it on.
    /// </summary>
    public bool PlainHttpForTests { get; init; }

    /// <summary>
    /// The limits on calls here (MSFP core section 8.3). Default: the
    /// specification's suggested numbers; a member left zero takes its default.
    /// </summary>
    public CallLimits Limits { get; init; } = new();

    /// <summary>The limits a new pairing starts with. Default: 2 streams, 1 transcode, no bitrate cap.</summary>
    public Limits? NewPairing { get; init; }

    /// <summary>The time; the system's by default. Tests give a fake one and make time's work happen.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// The source of keys, nonces, codes and IDs; the system's
    /// cryptographic generator by default. Tests may fix it; a server never does.
    /// </summary>
    public RandomNumberGenerator? Random { get; init; }

    /// <summary>Makes connections to peers; the engine's checked one when null.</summary>
    public IDialer? Dialer { get; init; }

    /// <summary>
    /// The HTTP handler the engine's calls to peers go through, in place of
    /// its own (which connects through <see cref="Dialer"/>, refuses local
    /// destinations, checks certificates as core section 3.2 says, never
    /// follows redirects, asks no compression and uses no proxy). For tests
    /// and for hosts with a transport of their own, which must keep those
    /// rules. Never disposed by the engine.
    /// </summary>
    public System.Net.Http.HttpMessageHandler? Transport { get; init; }

    /// <summary>Is told the engine's events; without one they are only logged.</summary>
    public INotifier? Notifier { get; init; }

    /// <summary>Where the engine logs; nowhere by default. Nothing secret is ever logged (MSFP core section 14.1).</summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    /// The parts this server speaks besides the core (MSFP core section 10.1):
    /// "catalog", "playback", "media", "video", "music". Null, by default: all
    /// five. A part listed without the parts it needs is left out. An empty
    /// list, not null: the core alone.
    /// </summary>
    public IReadOnlyList<string>? Parts { get; init; }

    /// <summary>The host's own libraries; absent, nothing is offered.</summary>
    public ICatalog? Catalog { get; init; }

    /// <summary>The host's playback; absent, nothing plays here (every play is answered 404).</summary>
    public IPlayback? Playback { get; init; }

    /// <summary>The host's media.</summary>
    public IMedia? Media { get; init; }

    /// <summary>Re-encodes relayed preview sheets; the engine's default when null.</summary>
    public ISheetEncoder? SheetEncoder { get; init; }

    /// <summary>This server's own cap on what peers play here; 0, by default, for none.</summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>
    /// Ends a peer's playback here that lasts longer; 24 hours by default
    /// (MSFP playback section 6.3).
    /// </summary>
    public TimeSpan LongestPlayback { get; init; }

    /// <summary>
    /// How often peers' catalogs are read; 30 minutes by default, the most
    /// often MSFP catalog section 3.2 suggests.
    /// </summary>
    public TimeSpan SyncEvery { get; init; }

    /// <summary>The configuration with the members the host left out filled.</summary>
    internal MsfpConfig WithDefaults() => this with
    {
        TimeProvider = TimeProvider ?? TimeProvider.System,
        Random = Random ?? RandomNumberGenerator.Create(),
        Logger = Logger ?? NullLogger.Instance,
        NewPairing = NewPairing ?? new Limits { Streams = 2, Transcodes = 1 },
        Sealer = Sealer ?? (Store as ISealerProvider)?.Sealer,
        LongestPlayback = LongestPlayback > TimeSpan.Zero ? LongestPlayback : TimeSpan.FromHours(24),
        SyncEvery = SyncEvery > TimeSpan.Zero ? SyncEvery : TimeSpan.FromMinutes(30),
        Limits = (Limits ?? new CallLimits()).WithDefaults(),
    };
}

/// <summary>
/// The limits on calls (MSFP core section 8.3). A zero member takes the
/// suggested default, given beside it.
/// </summary>
public sealed record CallLimits
{
    /// <summary>Per network address, before any signature: 50 a second.</summary>
    public double AddressRate { get; init; }

    /// <summary>Per network address: bursts of 400.</summary>
    public double AddressBurst { get; init; }

    /// <summary>Per peer: 20 calls a second.</summary>
    public double PeerRate { get; init; }

    /// <summary>Per peer: bursts of 200.</summary>
    public double PeerBurst { get; init; }

    /// <summary>All peers together: 200 a second.</summary>
    public double AllRate { get; init; }

    /// <summary>All peers together: bursts of 2,000.</summary>
    public double AllBurst { get; init; }

    /// <summary>play per peer: 30 a minute (0.5 a second).</summary>
    public double PlayRate { get; init; }

    /// <summary>play per peer: bursts of 20.</summary>
    public double PlayBurst { get; init; }

    /// <summary>Requests at once per peer: 8, with a queue twice as long.</summary>
    public int PeerAtOnce { get; init; }

    /// <summary>Requests at once in all: 64, with a queue twice as long.</summary>
    public int AllAtOnce { get; init; }

    /// <summary>The longest wait in a queue: 5 seconds.</summary>
    public TimeSpan QueueWait { get; init; }

    /// <summary>The limits with each zero member at its default.</summary>
    internal CallLimits WithDefaults() => new()
    {
        AddressRate = Or(AddressRate, 50),
        AddressBurst = Or(AddressBurst, 400),
        PeerRate = Or(PeerRate, 20),
        PeerBurst = Or(PeerBurst, 200),
        AllRate = Or(AllRate, 200),
        AllBurst = Or(AllBurst, 2000),
        PlayRate = Or(PlayRate, 0.5),
        PlayBurst = Or(PlayBurst, 20),
        PeerAtOnce = PeerAtOnce > 0 ? PeerAtOnce : 8,
        AllAtOnce = AllAtOnce > 0 ? AllAtOnce : 64,
        QueueWait = QueueWait > TimeSpan.Zero ? QueueWait : TimeSpan.FromSeconds(5),
    };

    private static double Or(double v, double d) => v > 0 ? v : d;
}
