using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Msfp.Protocol;
using Msfp.Stores;

namespace Msfp.Tests;

/// <summary>A clock the test sets.</summary>
internal class FakeTime(long unix) : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(unix);

    public override DateTimeOffset GetUtcNow()
    {
        lock (this)
        {
            return _now;
        }
    }

    public void Set(long unix)
    {
        lock (this)
        {
            _now = DateTimeOffset.FromUnixTimeSeconds(unix);
        }
    }

    public void Advance(TimeSpan d)
    {
        lock (this)
        {
            _now += d;
        }
    }
}

/// <summary>
/// A FakeTime whose timers fire at once, moving it on (msfp-go's
/// timerClock): what the engine waited is counted, not waited.
/// </summary>
internal sealed class TimerTime(long unix) : FakeTime(unix)
{
    private TimeSpan _waited;

    public TimeSpan Waited
    {
        get
        {
            lock (this)
            {
                return _waited;
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (this)
            {
                _waited += dueTime;
            }

            Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new Fired();
    }

    private sealed class Fired : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Random bytes from a script: each read whose length is the next
/// chunk's gets it; any other read is random (msfp-go's helpers_test.go).
/// </summary>
internal sealed class ScriptedRandom : RandomNumberGenerator
{
    private readonly Queue<byte[]> _chunks = new();

    public ScriptedRandom Queue(params string[] hexes)
    {
        lock (_chunks)
        {
            foreach (var h in hexes)
            {
                _chunks.Enqueue(Convert.FromHexString(h));
            }
        }

        return this;
    }

    public override void GetBytes(byte[] data) => GetBytes(data.AsSpan());

    public override void GetBytes(Span<byte> data)
    {
        lock (_chunks)
        {
            if (_chunks.TryPeek(out var c) && c.Length == data.Length)
            {
                _chunks.Dequeue().CopyTo(data);
                return;
            }
        }

        RandomNumberGenerator.Fill(data);
    }

    public override void GetNonZeroBytes(byte[] data) => throw new NotSupportedException();
}

/// <summary>Collects an engine's events.</summary>
internal sealed class EventLog : INotifier
{
    private readonly List<MsfpEvent> _events = [];

    public void Notify(MsfpEvent e)
    {
        lock (_events)
        {
            _events.Add(e);
        }
    }

    public List<MsfpEvent> All()
    {
        lock (_events)
        {
            return [.. _events];
        }
    }

    public List<T> Of<T>()
        where T : MsfpEvent => All().OfType<T>().ToList();
}

/// <summary>One exchange a server received, as it came and went.</summary>
internal sealed record Seen(string Target, string RequestBody, string RequestSignature, int Status, string ResponseBody,
    string? ResponseSignature, string? ContentType);

/// <summary>
/// An in-process HTTP network: requests to a host go to the engine (or the
/// handler) registered there, through the neutral request and response,
/// as a host would carry them. Every exchange is recorded.
/// </summary>
internal sealed class Network : HttpMessageHandler
{
    private readonly Dictionary<string, Func<MsfpRequest, Task<MsfpResponse>>> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Host, Seen Seen)> _log = [];

    public void Add(string host, MsfpEngine engine) => _servers[host] = r =>
        r.Target.StartsWith("/.well-known/", StringComparison.Ordinal) ? Task.FromResult(engine.HandleDiscovery(r))
        : r.Target.StartsWith("/api/v1/stream/", StringComparison.Ordinal) ? HostKit.StreamAsync(engine, r)
        : engine.HandleFederationAsync(r);

    private readonly Dictionary<string, (Func<string, HeaderList, byte[], byte[]?> Rewrite, Func<MsfpKey> Key)> _rewrites =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _down = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes host a hostile peer: rewrite changes its answers (given the
    /// path, the answer's headers to change, its body; null leaves it), each
    /// signed again with key when it was signed (msfp-go's setRewrite).
    /// </summary>
    public void Rewrite(string host, Func<string, HeaderList, byte[], byte[]?>? rewrite, Func<MsfpKey>? key = null)
    {
        lock (_rewrites)
        {
            if (rewrite is null)
            {
                _rewrites.Remove(host);
            }
            else
            {
                _rewrites[host] = (rewrite, key ?? (() => throw new InvalidOperationException("no key")));
            }
        }
    }

    /// <summary>Makes host answer every request with a plain 503, as if it couldn't be reached (msfp-go's setDown).</summary>
    public void Down(string host, bool down)
    {
        lock (_down)
        {
            if (down)
            {
                _down.Add(host);
            }
            else
            {
                _down.Remove(host);
            }
        }
    }

    public void Add(string host, Func<MsfpRequest, Task<MsfpResponse>> handler) => _servers[host] = handler;

    public void Remove(string host) => _servers.Remove(host);

    /// <summary>A request through this network, as a caller's view sends it.</summary>
    public Task<HttpResponseMessage> RouteAsync(HttpRequestMessage request, CancellationToken ct) => SendAsync(request, ct);

    public List<Seen> SeenBy(string host)
    {
        lock (_log)
        {
            return _log.Where(l => string.Equals(l.Host, host, StringComparison.OrdinalIgnoreCase)).Select(l => l.Seen).ToList();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        if (!_servers.TryGetValue(host, out var server))
        {
            throw new HttpRequestException("no route to " + host);
        }

        var headers = new HeaderList();
        foreach (var h in request.Headers)
        {
            foreach (var v in h.Value)
            {
                headers.Add(h.Key, v);
            }
        }

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
            {
                foreach (var v in h.Value)
                {
                    headers.Add(h.Key, v);
                }
            }
        }

        lock (_down)
        {
            if (_down.Contains(host))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent("down"u8.ToArray()) };
            }
        }

        var res = await server(new MsfpRequest
        {
            Method = request.Method.Method,
            Target = uri.PathAndQuery,
            Headers = headers,
            Body = new MemoryStream(body),
            ClientAddress = IPAddress.Parse("192.0.2.99"),
        });
        (Func<string, HeaderList, byte[], byte[]?> Rewrite, Func<MsfpKey> Key) rw;
        bool hostile;
        lock (_rewrites)
        {
            hostile = _rewrites.TryGetValue(host, out rw);
        }

        if (hostile)
        {
            // A hostile peer: its answer changed, and signed again with its
            // key when it was signed.
            var hl = new HeaderList();
            foreach (var (k, v) in res.Headers)
            {
                hl.Add(k, v);
            }

            hl.Remove("Content-Length"); // the body's own, unless the rewrite sets one
            if (rw.Rewrite(uri.AbsolutePath, hl, res.Body.ToArray()) is { } changed)
            {
                if (hl.Get(Signing.HeaderSignature) is not null)
                {
                    hl.Set(Signing.HeaderSignature, Signing.SignResponse(rw.Key(), headers.Get(Signing.HeaderNonce) ?? "", res.Status,
                        Signing.MediaType(hl.Get("Content-Type")) is { Length: > 0 } t ? t : null, changed));
                }

                res = new MsfpResponse { Status = res.Status, Headers = hl.Items, Body = changed };
            }
        }
        var output = new HttpResponseMessage((HttpStatusCode)res.Status) { Content = new ByteArrayContent(res.Body.ToArray()) };
        foreach (var (k, v) in res.Headers)
        {
            if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                output.Content.Headers.ContentLength = long.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (!output.Headers.TryAddWithoutValidation(k, v))
            {
                output.Content.Headers.TryAddWithoutValidation(k, v);
            }
        }

        lock (_log)
        {
            _log.Add((host, new Seen(uri.PathAndQuery, System.Text.Encoding.UTF8.GetString(body), headers.Get(Signing.HeaderSignature) ?? "",
                res.Status, System.Text.Encoding.UTF8.GetString(res.Body.Span), res.Header(Signing.HeaderSignature), res.Header("Content-Type"))));
        }

        return output;
    }
}

/// <summary>
/// One server's view of the network (msfp-go's mapDialer and gateDialer):
/// hosts it can't connect to, hosts it refuses as destinations, hosts whose
/// connections wait for a gate; everything else as the network routes it.
/// </summary>
internal sealed class CallerNetwork(Network net) : HttpMessageHandler
{
    private readonly Dictionary<string, Func<CancellationToken, Task>> _rules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Connections to host fail, as to an address nothing listens at.</summary>
    public CallerNetwork Closed(string host) => Rule(host, _ => throw new HttpRequestException("connection refused: " + host));

    /// <summary>Connections to host are refused destinations (core section 3.3).</summary>
    public CallerNetwork Refused(string host) =>
        Rule(host, _ => throw new HttpRequestException("refused", new MsfpException(MsfpError.RefusedDestination, host)));

    /// <summary>Connections to host wait until the gate opens; waiting is set when one does.</summary>
    public CallerNetwork Held(string host, Task gate, TaskCompletionSource waiting) =>
        Rule(host, async ct =>
        {
            waiting.TrySetResult();
            await gate.WaitAsync(ct);
        });

    /// <summary>Connections to host go as the network routes them.</summary>
    public CallerNetwork Open(string host)
    {
        lock (_rules)
        {
            _rules.Remove(host);
        }

        return this;
    }

    private CallerNetwork Rule(string host, Func<CancellationToken, Task> rule)
    {
        lock (_rules)
        {
            _rules[host] = rule;
        }

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        Func<CancellationToken, Task>? rule;
        lock (_rules)
        {
            _rules.TryGetValue(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}", out rule);
        }

        if (rule is not null)
        {
            await rule(ct);
        }

        return await net.RouteAsync(request, ct);
    }
}

/// <summary>An engine of a test, with what it was made with.</summary>
internal sealed class TestServer : IDisposable
{
    public required MsfpEngine Engine { get; init; }

    public required IStore Store { get; init; }

    public required FakeTime Clock { get; init; }

    public required ScriptedRandom Random { get; init; }

    public required EventLog Events { get; init; }

    public required string Address { get; init; }

    public string Host => Protocol.Address.Host(Address);

    public static async Task<TestServer> StartAsync(Network net, string address, string? name = null, long unix = 1800000000,
        ScriptedRandom? random = null, IReadOnlyList<string>? parts = null, IReadOnlyList<string>? pins = null,
        string prefix = "", bool discovery = false, FakeTime? clock = null, IStore? store = null, HttpMessageHandler? transport = null,
        IDialer? dialer = null, CallLimits? limits = null, Func<MsfpConfig, MsfpConfig>? with = null)
    {
        store ??= new MemoryStore();
        clock ??= new FakeTime(unix);
        random ??= new ScriptedRandom();
        var events = new EventLog();
        var config = new MsfpConfig
        {
            Address = address,
            Store = store,
            Name = name is null ? null : () => name,
            TimeProvider = clock,
            Random = random,
            Notifier = events,
            Transport = transport ?? net,
            Dialer = dialer,
            Limits = limits ?? new(),
            Parts = parts,
            Pins = pins,
            Prefix = prefix,
            Discovery = discovery,
        };
        var engine = await MsfpEngine.OpenAsync(with is null ? config : with(config));
        var s = new TestServer { Engine = engine, Store = store, Clock = clock, Random = random, Events = events, Address = address };
        net.Add(s.Host, engine);
        return s;
    }

    public async Task<PeerRecord> RecordAsync(string id)
    {
        PeerRecord? p = null;
        await Store.ReadAsync(async tx => p = await tx.PeerAsync(id), CancellationToken.None);
        return p ?? throw new InvalidOperationException("no peer " + id);
    }

    public void Dispose() => Engine.Dispose();

    /// <summary>Waits for the work the engine started in the background.</summary>
    public Task WaitAsync() => Engine.WaitForTestAsync();
}

internal static class Vectors
{
    public static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "v0", name)))!.AsObject();

    public static Dictionary<string, JsonObject> Exchanges() =>
        Load("exchanges.json")["exchanges"]!.AsArray().Select(x => x!.AsObject()).ToDictionary(x => x["name"]!.GetValue<string>());

    public static string S(this JsonObject o, string name) => o[name]!.GetValue<string>();

    public static string Seed(string key) => Load("keys.json")["keys"]![key]!["seed"]!.GetValue<string>();

    public static string Public(string key) => Load("keys.json")["keys"]![key]!["public"]!.GetValue<string>();
}

/// <summary>What the engine tests share: pairings, raw calls, keys read from a store.</summary>
internal static class Kit
{
    /// <summary>A pairing both operators accepted: a makes the code, b redeems it, a accepts. The peers' IDs: b on a, a on b.</summary>
    public static async Task<(string BOnA, string AOnB)> LinkAsync(TestServer a, TestServer b)
    {
        var fingerprint = (await b.Engine.ServerAsync()).Fingerprint;
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        var onB = await b.Engine.RedeemCodeAsync(code);
        var onA = (await a.Engine.PeersAsync()).Single(p => p.Fingerprint == fingerprint);
        await a.Engine.AcceptAsync(onA.Id);
        return (onA.Id, onB.Id);
    }

    /// <summary>A call to a server, signed with a key, as a peer sends it, at the server's time.</summary>
    public static async Task<MsfpResponse> SendAsync(TestServer to, MsfpKey signer, string call, byte[] body, string? host = null)
    {
        var target = "/api/v1/federation/" + call;
        var h = new HeaderList().Add("Content-Type", "application/json");
        Signing.SignRequest("POST", host ?? to.Host, target, body, signer, to.Clock.GetUtcNow()).Apply(h);
        return await to.Engine.HandleFederationAsync(new MsfpRequest { Method = "POST", Target = target, Headers = h, Body = new MemoryStream(body) });
    }

    /// <summary>A key of a server, read from its store (the plain sealer of the tests).</summary>
    public static async Task<MsfpKey> KeyAsync(TestServer s, Func<ServerRecord, byte[]?> pick, string context)
    {
        ServerRecord? r = null;
        await s.Store.ReadAsync(async tx => r = await tx.ServerAsync(), CancellationToken.None);
        return MsfpKey.FromSeed(await PlainSealer.Instance.OpenAsync(pick(r!)!, context, CancellationToken.None));
    }

    /// <summary>The server's record.</summary>
    public static async Task<ServerRecord> ServerRecordAsync(TestServer s)
    {
        ServerRecord? r = null;
        await s.Store.ReadAsync(async tx => r = await tx.ServerAsync(), CancellationToken.None);
        return r!;
    }

    public static byte[] Paired { get; } = "{\"state\":\"paired\",\"core\":[0],\"parts\":{}}"u8.ToArray();

    public static string Text(this MsfpResponse r) => System.Text.Encoding.UTF8.GetString(r.Body.Span);

    public static bool Signed(this MsfpResponse r) => r.Header(Signing.HeaderSignature) is not null;

    /// <summary>Moves every clock on by d.</summary>
    public static void Advance(TimeSpan d, params TestServer[] servers)
    {
        foreach (var s in servers)
        {
            s.Clock.Advance(d);
        }
    }
}
