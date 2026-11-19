using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Msfp.Protocol;
using Msfp.Stores;

namespace Msfp.Tests;

/// <summary>A clock the test sets.</summary>
internal sealed class FakeTime(long unix) : TimeProvider
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

    public void Add(string host, MsfpEngine engine) => _servers[host] = r => engine.HandleFederationAsync(r);

    public void Add(string host, Func<MsfpRequest, Task<MsfpResponse>> handler) => _servers[host] = handler;

    public void Remove(string host) => _servers.Remove(host);

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

        var res = await server(new MsfpRequest
        {
            Method = request.Method.Method,
            Target = uri.PathAndQuery,
            Headers = headers,
            Body = new MemoryStream(body),
            ClientAddress = IPAddress.Parse("192.0.2.99"),
        });
        var output = new HttpResponseMessage((HttpStatusCode)res.Status) { Content = new ByteArrayContent(res.Body.ToArray()) };
        foreach (var (k, v) in res.Headers)
        {
            if (!output.Headers.TryAddWithoutValidation(k, v))
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

/// <summary>An engine of a test, with what it was made with.</summary>
internal sealed class TestServer : IDisposable
{
    public required MsfpEngine Engine { get; init; }

    public required MemoryStore Store { get; init; }

    public required FakeTime Clock { get; init; }

    public required ScriptedRandom Random { get; init; }

    public required EventLog Events { get; init; }

    public required string Address { get; init; }

    public string Host => Protocol.Address.Host(Address);

    public static async Task<TestServer> StartAsync(Network net, string address, string? name = null, long unix = 1800000000,
        ScriptedRandom? random = null, IReadOnlyList<string>? parts = null, IReadOnlyList<string>? pins = null)
    {
        var store = new MemoryStore();
        var clock = new FakeTime(unix);
        random ??= new ScriptedRandom();
        var events = new EventLog();
        var engine = await MsfpEngine.OpenAsync(new MsfpConfig
        {
            Address = address,
            Store = store,
            Name = name is null ? null : () => name,
            TimeProvider = clock,
            Random = random,
            Notifier = events,
            Transport = net,
            Parts = parts,
            Pins = pins,
        });
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
}

internal static class Vectors
{
    public static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "v1", name)))!.AsObject();

    public static Dictionary<string, JsonObject> Exchanges() =>
        Load("exchanges.json")["exchanges"]!.AsArray().Select(x => x!.AsObject()).ToDictionary(x => x["name"]!.GetValue<string>());

    public static string S(this JsonObject o, string name) => o[name]!.GetValue<string>();

    public static string Seed(string key) => Load("keys.json")["keys"]![key]!["seed"]!.GetValue<string>();

    public static string Public(string key) => Load("keys.json")["keys"]![key]!["public"]!.GetValue<string>();
}
