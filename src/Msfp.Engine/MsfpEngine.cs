using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>
/// The MSFP engine: it runs the protocol inside a media server, the host.
/// It signs and checks every call, keeps the pairings, keys, addresses and
/// limits; the host plugs in through the interfaces of <see cref="MsfpConfig"/>.
/// Make one with <see cref="OpenAsync"/>, give peers' requests to
/// <see cref="HandleFederationAsync"/>, and call its actions from the
/// operator's panel.
/// </summary>
/// <remarks>
/// Every method is safe to call from several threads at once. Methods
/// throw <see cref="MsfpException"/> with the codes each one names, or
/// <see cref="MsfpError.Internal"/>, or the token's
/// <see cref="OperationCanceledException"/>.
/// </remarks>
public sealed partial class MsfpEngine : IDisposable
{
    private readonly MsfpConfig _cfg;
    private readonly ILogger _log;
    private readonly IStore _store;
    private readonly ISealer _sealer;
    private readonly TimeProvider _time;
    private readonly RandomNumberGenerator _random;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<int>> _parts;
    private readonly NonceMemory _nonces;
    private readonly CallLimiter _limits;
    private readonly Notifications _events;
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _recordLock = new(1, 1);
    private readonly object _keysLock = new();
    private ServerKeys _keys = null!;
    private AddressState _addr = new("", null, null, false, false, null);
    private Verifier _verifier = null!;

    private MsfpEngine(MsfpConfig cfg, ISealer sealer)
    {
        _cfg = cfg;
        _log = cfg.Logger!;
        _store = cfg.Store;
        _sealer = sealer;
        _time = cfg.TimeProvider!;
        _random = cfg.Random!;
        _parts = ServedParts(cfg.Parts);
        _nonces = new NonceMemory(Now());
        _limits = new CallLimiter(cfg.Limits);
        _events = new Notifications(cfg.Notifier, _log);
        _discovery = cfg.Discovery;
        _startedAt = Now();
    }

    // When this engine started: the first readings of peers' catalogs go a
    // minute after.
    private readonly DateTimeOffset _startedAt;

    // The client for a call to a peer: any certificate (pair, hello, the
    // discovery document), or those publicly trusted and the peer's pins.
    // Made again when the pins change.
    internal HttpClient ClientFor(IReadOnlyCollection<string>? pins, bool anyCertificate)
    {
        var key = anyCertificate ? "*" : string.Join(" ", pins ?? []);
        lock (_clients)
        {
            if (_clients.TryGetValue(key, out var c))
            {
                return c;
            }

            c = _cfg.Transport is { } transport
                ? new HttpClient(transport, disposeHandler: false)
                : new HttpClient(PeerHandler.Create(_cfg.Dialer, _cfg.AllowLoopback, pins ?? [], anyCertificate), disposeHandler: true);
            c.Timeout = CallTimeout;
            _clients[key] = c;
            return c;
        }
    }

    /// <summary>A call's time in all (core section 3).</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Opens the engine on its store: on the very first start it makes this
    /// server's keys (the current one and the next one it commits to, core
    /// sections 4 and 12.5) and keeps them sealed. Only
    /// <see cref="MsfpConfig.Address"/> and <see cref="MsfpConfig.Store"/>
    /// are required.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.InvalidAddress"/>,
    /// <see cref="MsfpError.PlainHttp"/>, <see cref="MsfpError.Sealed"/> (the store's keys can't be opened).</exception>
    public static async Task<MsfpEngine> OpenAsync(MsfpConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(config.Store);
        var cfg = config.WithDefaults();
        var sealer = cfg.Sealer ?? throw new MsfpException(MsfpError.Internal,
            "Config.Sealer is required, as the store brings none");
        if (cfg.PlainHttpForTests)
        {
            Address.AllowPlainHttpForTests(true);
            cfg.Logger!.LogWarning("msfp: plain http pairings are on: for tests only, never for a server in use");
        }

        if (cfg.Address.Length > 0)
        {
            try
            {
                Address.Check(cfg.Address);
            }
            catch (ProtocolException e) when (e.Error == ProtocolError.PlainHttp)
            {
                throw new MsfpException(MsfpError.PlainHttp);
            }
            catch (ProtocolException e)
            {
                throw new MsfpException(MsfpError.InvalidAddress, e.Message, e);
            }
        }

        if (Uncallables.Valid(cfg.Address))
        {
            throw new MsfpException(MsfpError.InvalidAddress,
                "the address of a server that can't be called is made by the engine");
        }

        try
        {
            Discovery.CheckBase(cfg.Prefix + Discovery.DefaultBase);
        }
        catch (ProtocolException e)
        {
            throw new MsfpException(MsfpError.InvalidAddress, $"Config.Prefix \"{cfg.Prefix}\": {e.Message}", e);
        }

        var engine = new MsfpEngine(cfg, sealer) { _configured = cfg.Address };
        try
        {
            await engine.LoadKeysAsync(ct).ConfigureAwait(false);
            await engine.KeepsLogAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            engine.Dispose();
            throw;
        }

        return engine;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_clients)
        {
            foreach (var c in _clients.Values)
            {
                c.Dispose();
            }

            _clients.Clear();
        }

        _recordLock.Dispose();
        _keyAction.Dispose();
        _ownLock.Dispose();
        _smallGate.Dispose();
        _sheetGate.Dispose();
        lock (_readingLocks)
        {
            foreach (var l in _readingLocks.Values)
            {
                l.Dispose();
            }
        }
    }

    internal DateTimeOffset Now() => _time.GetUtcNow();

    // Whether the engine keeps the change log of the host's catalog: the
    // host has a catalog without one, and the store can keep it.
    private async Task KeepsLogAsync(CancellationToken ct)
    {
        if (_cfg.Catalog is null || ChangeLog() is not null)
        {
            return;
        }

        await _store.ReadAsync(tx =>
        {
            _logKept = tx is IOwnLogTx;
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
        if (!_logKept)
        {
            _log.LogWarning("msfp: the catalog keeps no change log, and the store can't: peers read the whole list each time");
        }
    }

    internal DateTimeOffset NowSecond()
    {
        var n = Now();
        return DateTimeOffset.FromUnixTimeSeconds(n.ToUnixTimeSeconds());
    }

    internal byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        _random.GetBytes(b);
        return b;
    }

    // An opaque ID: a prefix and n random bytes (10 by default) in
    // lowercase base32, without padding.
    internal string NewId(string prefix, int n = 10)
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var b = RandomBytes(n);
        var s = new System.Text.StringBuilder(prefix);
        int buffer = 0, bits = 0;
        foreach (var x in b)
        {
            buffer = (buffer << 8) | x;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                s.Append(Alphabet[(buffer >> bits) & 31]);
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            s.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return s.ToString();
    }

    // The parts Config.Parts lists (all when null), without those whose
    // needs aren't listed, each at version 0.
    private static Dictionary<string, IReadOnlyList<int>> ServedParts(IReadOnlyList<string>? listed)
    {
        string[] all = [Versions.PartCatalog, Versions.PartMedia, Versions.PartPlayback, Versions.PartVideo, Versions.PartMusic];
        var needs = new Dictionary<string, string[]>
        {
            [Versions.PartPlayback] = [Versions.PartCatalog, Versions.PartMedia],
            [Versions.PartVideo] = [Versions.PartCatalog, Versions.PartPlayback, Versions.PartMedia],
            [Versions.PartMusic] = [Versions.PartCatalog, Versions.PartPlayback, Versions.PartMedia],
        };
        var output = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var name in all)
        {
            if (listed is not null && !listed.Contains(name))
            {
                continue;
            }

            if (needs.GetValueOrDefault(name, []).All(output.ContainsKey))
            {
                output[name] = [0];
            }
        }

        return output;
    }

    /// <summary>What this server speaks: core 0, and its parts.</summary>
    internal Versions OurVersions() => new([0], _parts);

    internal Agreement? Agreed(PeerRecord p) =>
        p.Versions is null ? new Agreement(0, new Dictionary<string, int>()) : Versions.Agreed(OurVersions(), FromRecord(p.Versions));

    internal static Versions FromRecord(WireVersions v) =>
        new(v.Core ?? [], (v.Parts ?? []).ToDictionary(p => p.Key, p => (IReadOnlyList<int>)p.Value, StringComparer.Ordinal));

    internal static WireVersions ToRecord(Versions v) =>
        new() { Core = [.. v.Core], Parts = v.Parts.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.Ordinal) };

    // This server's name, as peers see it: the host's, cleaned, or its
    // address's host.
    internal string Name()
    {
        var named = Named();
        return CleanName(_cfg.Name?.Invoke() ?? Address.Host(named), named);
    }

    // The longest name, in code points (core section 9.1).
    private const int MaxName = 80;

    /// <summary>
    /// A peer's name, or a code's note, cleaned as core section 9.1 says;
    /// an empty one becomes the host of address, with its port.
    /// </summary>
    internal static string CleanName(string? name, string? address)
    {
        var n = Music.CleanText(name ?? "", MaxName);
        if (n.Length == 0 && !string.IsNullOrEmpty(address))
        {
            n = Address.Host(address);
        }

        return n;
    }

    internal async Task<ServerRecord> ServerRecordAsync(CancellationToken ct)
    {
        ServerRecord? rec = null;
        await _store.ReadAsync(async tx => rec = await tx.ServerAsync().ConfigureAwait(false), ct).ConfigureAwait(false);
        return rec ?? throw new MsfpException(MsfpError.Internal, "no server record");
    }

    internal async Task<PeerRecord> PeerRecordAsync(string id, CancellationToken ct)
    {
        PeerRecord? p = null;
        await _store.ReadAsync(async tx => p = await tx.PeerAsync(id).ConfigureAwait(false), ct).ConfigureAwait(false);
        return p ?? throw new MsfpException(MsfpError.NotFound);
    }

    // Changes a peer in a transaction: NotFound when it is gone meanwhile.
    internal async Task<PeerRecord> UpdatePeerAsync(string id, Action<PeerRecord> change, CancellationToken ct)
    {
        PeerRecord? now = null;
        await _store.UpdateAsync(async tx =>
        {
            var q = await tx.PeerAsync(id).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
            change(q);
            now = q;
            await tx.PutPeerAsync(q).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return now!;
    }

    internal void Notify(params MsfpEvent[] events)
    {
        foreach (var e in events)
        {
            React(e);
        }

        _events.Tell(events);
    }

    // What the engine itself does on an event, before the host is told: a
    // peer no longer paired, or suspended, ends its playbacks both ways
    // (MSFP playback section 6.3); one that can't be called is fetched from
    // no more, and what was kept of its titles goes (core section 7.5); a
    // change of the parts on makes the next reading of our titles a whole one.
    private void React(MsfpEvent e)
    {
        switch (e)
        {
            case PeerEnded or PeerGone or PeerSuspended:
                var id = e switch
                {
                    PeerEnded x => x.Peer,
                    PeerGone x => x.Peer,
                    PeerSuspended x => x.Peer,
                    _ => "",
                };
                EndPlaybacks(pb => pb.Peer == id, EndReason.PairingEnded);
                EndRemotes(id, EndReason.PairingEnded);
                if (e is PeerGone)
                {
                    Background(ct => DropCatalogAsync(id, ct));
                }

                break;
            case InboundChanged { Inbound: var inbound } changed when inbound == Inbound.No:
                EndRemotes(changed.Peer, EndReason.PairingEnded);
                Background(ct => DropCatalogAsync(changed.Peer, ct));
                break;
            case VersionsChanged v:
                Background(ct => PartsChangedAsync(v.Peer, ct));
                break;
        }
    }
}
