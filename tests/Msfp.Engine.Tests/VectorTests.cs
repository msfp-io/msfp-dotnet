using System.Text;
using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp.Tests;

/// <summary>
/// The vectors of MSFP core section 17 through two engines (msfp-go's
/// vectors_test.go): A at a.example.org and B at 192.0.2.10:8096, their
/// keys from keys.json, each on its own clock at 1800000000. Their pair
/// exchange byte for byte both ways, the hello and play requests a peer
/// receives, and the hello answers a caller reads.
/// </summary>
public sealed class VectorTests
{
    private const string Ids = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private sealed record Pair(Network Net, TestServer A, TestServer B, PeerView OnA, PeerView OnB, Dictionary<string, JsonObject> Vs);

    private static async Task<Pair> NewPairAsync()
    {
        var vs = Vectors.Exchanges();
        var code = Vectors.Load("msfp/v0/code.json")["code"]!.AsObject();
        var net = new Network();
        var randA = new ScriptedRandom().Queue(Vectors.Seed("a"), Vectors.Seed("a2"), Ids, code.S("secret"));
        var randB = new ScriptedRandom().Queue(Vectors.Seed("b"), Vectors.Seed("b2"), Ids, vs["pair"].S("nonce"));
        var a = await TestServer.StartAsync(net, "https://a.example.org", "Server A", random: randA);
        var b = await TestServer.StartAsync(net, "https://192.0.2.10:8096", "Server B", random: randB, parts: []);

        // pair, B to A: the code A makes is the vector's; B's request and
        // A's answer are the vector's, byte for byte.
        var (text, _) = await a.Engine.MakeCodeAsync("");
        Assert.Equal(code.S("text"), text);
        var onB = await b.Engine.RedeemCodeAsync(text);
        var got = net.SeenBy(a.Host).Last(s => s.Target.EndsWith("/pair", StringComparison.Ordinal));
        var want = vs["pair"];
        Assert.Equal(want.S("body"), got.RequestBody);
        Assert.Equal(want.S("signature"), got.RequestSignature);
        Assert.Equal(200, got.Status);
        Assert.Equal(want.S("answerBody"), got.ResponseBody);
        Assert.Equal(want.S("answerSignature"), got.ResponseSignature);
        Assert.Equal(want.S("answerContentType"), got.ContentType);
        var onA = Assert.Single(await a.Engine.PeersAsync());
        return new Pair(net, a, b, onA, onB, vs);
    }

    // Sends a vector's request to an engine, as it came.
    private static Task<MsfpResponse> ServeAsync(MsfpEngine e, JsonObject v)
    {
        var h = new HeaderList().Add("Content-Type", "application/json")
            .Add(Signing.HeaderKey, Vectors.Public(v.S("signer"))).Add(Signing.HeaderDate, v.S("date"))
            .Add(Signing.HeaderNonce, v.S("nonceText")).Add(Signing.HeaderSignature, v.S("signature"));
        return e.HandleFederationAsync(new MsfpRequest
        {
            Method = v.S("method"), Target = v.S("target"), Headers = h, Body = new MemoryStream(Encoding.UTF8.GetBytes(v.S("body"))),
        });
    }

    [Fact]
    public async Task ThroughEngines()
    {
        var (net, a, b, onA, onB, vs) = await NewPairAsync();
        Assert.Equal(PeerState.Theirs, onB.State);
        Assert.Equal("Server A", onB.Name);
        Assert.Equal("https://a.example.org", onB.Address);
        Assert.Equal(PeerState.Ours, onA.State);
        Assert.Equal("Server B", onA.Name);
        Assert.Equal("https://192.0.2.10:8096", onA.Address);

        // hello, A to B: B, waiting for A's operator, is paired by it, keeps
        // A's versions and pins, and answers signed with b.
        b.Clock.Set(1800000060);
        var hello = vs["hello"];
        var res = await ServeAsync(b.Engine, hello);
        Assert.Equal(200, res.Status);
        Assert.True(Signing.CheckResponse(Base64UrlText.Decode(Vectors.Public("b"))!, hello.S("nonceText"), res.Status,
            res.Header("Content-Type"), res.Body.Span, res.Header(Signing.HeaderSignature)));
        Assert.StartsWith("{\"state\":\"paired\",\"core\":[0],\"parts\":{}", Encoding.UTF8.GetString(res.Body.Span), StringComparison.Ordinal);
        var recB = await b.RecordAsync(onB.Id);
        Assert.Equal(PeerState.Paired, recB.State);
        Assert.Equal([0], recB.Versions!.Core!);
        Assert.Equal(["catalog", "media", "playback", "video"], recB.Versions.Parts!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["JN-hf1COiASAAUsiYeHXMNVvNnxYR2dKYnBT2Zx_6Fo"], recB.Pins!);

        // play, A to B: B speaks no playback, so a signed 404
        // not-supported, byte for byte.
        b.Clock.Set(1800000180);
        var play = vs["play, to a peer without playback"];
        res = await ServeAsync(b.Engine, play);
        Assert.Equal(404, res.Status);
        Assert.Equal(play.S("answerBody"), Encoding.UTF8.GetString(res.Body.Span));
        Assert.Equal(play.S("answerSignature"), res.Header(Signing.HeaderSignature));

        // The hello answers A reads: B's, signed with b, whose versions and
        // pins A keeps; then B's signed 404, and A marks B gone.
        JsonObject answering = hello;
        net.Add(b.Host, _ => Task.FromResult(new MsfpResponse
        {
            Status = answering["status"]!.GetValue<int>(),
            Headers =
            [
                new("Content-Type", answering.S("answerContentType")),
                new(Signing.HeaderSignature, answering.S("answerSignature")),
            ],
            Body = Encoding.UTF8.GetBytes(answering.S("answerBody")),
        }));
        a.Random.Queue(hello.S("nonce"));
        await a.Engine.CheckAsync(onA.Id);
        var recA = await a.RecordAsync(onA.Id);
        Assert.Equal(PeerState.Ours, recA.State);
        Assert.Equal([0], recA.Versions!.Core!);
        Assert.Equal(["catalog"], recA.Versions.Parts!.Keys);
        Assert.Equal(["E7UUIjYbkigLNKPjmSGixyA-pslmiFKckCEX_A6IZlM"], recA.Pins!);
        // The request A sent is the vector's, byte for byte but for the
        // hello's own members, which are A's (it speaks every part). (Its
        // last hello: the discovery document A reads at the pairing, in the
        // background, can reach B after it.)
        var sent = net.SeenBy(b.Host).Last(s => s.Target.EndsWith("/hello", StringComparison.Ordinal));
        Assert.Equal(hello.S("signature").Length, sent.RequestSignature.Length);

        answering = vs["refused hello"];
        a.Random.Queue(answering.S("nonce"));
        var e = await Assert.ThrowsAsync<MsfpException>(() => a.Engine.CheckAsync(onA.Id));
        Assert.Equal(MsfpError.Unknown, e.Code);
        recA = await a.RecordAsync(onA.Id);
        Assert.Equal(PeerState.Gone, recA.State);
        Assert.Single(recA.Pins!);
    }

    private static JsonObject KeyVector(string file) => Vectors.Load(file)["exchange"]!.AsObject();

    // rotation.json: A rotates from a to a2, committing to a3; B, which
    // holds A's commitment to a2 from the pair, moves at once and answers
    // rotated, byte for byte.
    [Fact]
    public async Task RotateThroughB()
    {
        var (_, _, b, _, onB, _) = await NewPairAsync();
        var want = KeyVector("msfp/v0/rotation.json");
        b.Clock.Set(1800100000);
        var res = await ServeAsync(b.Engine, want);
        Assert.Equal(want["status"]!.GetValue<int>(), res.Status);
        Assert.Equal(want.S("answerBody"), Encoding.UTF8.GetString(res.Body.Span));
        Assert.Equal(want.S("answerSignature"), res.Header(Signing.HeaderSignature));
        Assert.Equal(want.S("answerContentType"), res.Header("Content-Type"));
        var rec = await b.RecordAsync(onB.Id);
        Assert.Equal(Vectors.Public("a2"), Base64UrlText.Encode(rec.Key));
        Assert.Equal(Vectors.Public("a"), Base64UrlText.Encode(rec.PreviousKey!));
        Assert.Equal("rotation", rec.CommitmentFrom);
        Assert.Single(b.Events.Of<PeerRotated>());
        Assert.Equal("KGOK-5E7J-HLIB-DV4F-GVMJ-IQC6", (await b.Engine.PeerAsync(onB.Id)).Fingerprint);
    }

    // revocation.json: A revokes a; B suspends it and answers suspended,
    // byte for byte. Then only rotate and revoke are heard from a.
    [Fact]
    public async Task RevokeThroughB()
    {
        var (_, _, b, _, onB, _) = await NewPairAsync();
        var want = KeyVector("msfp/v0/revocation.json");
        b.Clock.Set(1800200000);
        var res = await ServeAsync(b.Engine, want);
        Assert.Equal(want["status"]!.GetValue<int>(), res.Status);
        Assert.Equal(want.S("answerBody"), Encoding.UTF8.GetString(res.Body.Span));
        Assert.Equal(want.S("answerSignature"), res.Header(Signing.HeaderSignature));
        Assert.NotNull((await b.RecordAsync(onB.Id)).Suspended);
        Assert.Single(b.Events.Of<PeerSuspended>());
        // A hello from the suspended key: a signed 404.
        var h = new HeaderList().Add("Content-Type", "application/json");
        var body = "{\"state\":\"paired\"}"u8.ToArray();
        var key = Msfp.Protocol.MsfpKey.FromSeed(Convert.FromHexString(Vectors.Seed("a")));
        Signing.SignRequest("POST", "192.0.2.10:8096", "/api/v1/federation/hello", body, key, b.Clock.GetUtcNow()).Apply(h);
        res = await b.Engine.HandleFederationAsync(new MsfpRequest
        {
            Method = "POST", Target = "/api/v1/federation/hello", Headers = h, Body = new MemoryStream(body),
        });
        Assert.Equal(404, res.Status);
        Assert.False(res.PlainNotFound);
        Assert.NotNull(res.Header(Signing.HeaderSignature));
    }

    private static string NonceHex(JsonObject v) => Convert.ToHexStringLower(Base64UrlText.Decode(v.S("nonceText"), 16)!);

    // rotation.json from A's side: A rotates from a to a2, committing to
    // a3; its rotate to B and B's answer are the vector's, byte for byte.
    [Fact]
    public async Task RotateFromA()
    {
        var (net, a, b, onA, onB, _) = await NewPairAsync();
        var want = KeyVector("msfp/v0/rotation.json");
        a.Clock.Set(1800100000);
        b.Clock.Set(1800100000);
        a.Random.Queue(Vectors.Seed("a3"), NonceHex(want));
        await a.Engine.RotateAsync();
        var got = Assert.Single(net.SeenBy(b.Host), x => x.Target.EndsWith("/rotate", StringComparison.Ordinal));
        Assert.Equal(want.S("body"), got.RequestBody);
        Assert.Equal(want.S("signature"), got.RequestSignature);
        Assert.Equal(want["status"]!.GetValue<int>(), got.Status);
        Assert.Equal(want.S("answerBody"), got.ResponseBody);
        Assert.Equal(want.S("answerSignature"), got.ResponseSignature);
        Assert.Equal(Vectors.Public("a2"), Base64UrlText.Encode((await b.RecordAsync(onB.Id)).Key));
        Assert.Equal("KGOK-5E7J-HLIB-DV4F-GVMJ-IQC6", (await a.Engine.ServerAsync()).Fingerprint);
        // B was the only peer: the change is over, the old key deleted.
        Assert.Equal(MsfpEngine.KeyCurrent, (await a.Engine.PeerAsync(onA.Id)).OurKey);
        Assert.Single(a.Events.Of<RotationEnded>());
        ServerRecord? rec = null;
        await a.Store.ReadAsync(async tx => rec = await tx.ServerAsync(), CancellationToken.None);
        Assert.Null(rec!.Old);
        // Calls go on, signed with a2, which B hears.
        await a.Engine.CheckAsync(onA.Id);
    }

    // revocation.json from A's side: A revokes a, then rotates to a2 in the
    // same action; its revoke and B's answer are the vector's.
    [Fact]
    public async Task KeyStolenFromA()
    {
        var (net, a, b, _, onB, _) = await NewPairAsync();
        var want = KeyVector("msfp/v0/revocation.json");
        a.Clock.Set(1800200000);
        b.Clock.Set(1800200000);
        a.Random.Queue(NonceHex(want), Vectors.Seed("a3"));
        Assert.True(await a.Engine.KeyStolenAsync());
        var got = Assert.Single(net.SeenBy(b.Host), x => x.Target.EndsWith("/revoke", StringComparison.Ordinal));
        Assert.Equal(want.S("body"), got.RequestBody);
        Assert.Equal(want.S("signature"), got.RequestSignature);
        Assert.Equal(want.S("answerBody"), got.ResponseBody);
        Assert.Equal(want.S("answerSignature"), got.ResponseSignature);
        // Suspended, then moved to the committed key: no longer suspended.
        var rec = await b.RecordAsync(onB.Id);
        Assert.Equal(Vectors.Public("a2"), Base64UrlText.Encode(rec.Key));
        Assert.Null(rec.Suspended);
    }

    // A peer that doesn't answer stays on our old key; at the next contact it
    // is sent the rotate first; one that never moves is lost when the
    // change ends.
    [Fact]
    public async Task RotateAtContactAndLost()
    {
        var (net, a, b, onA, onB, _) = await NewPairAsync();
        net.Remove(b.Host);
        await a.Engine.RotateAsync();
        Assert.Equal(MsfpEngine.KeyOld, (await a.Engine.PeerAsync(onA.Id)).OurKey);
        Assert.Empty(await a.Engine.CodesAsync());
        // Back: the next contact sends the rotate first, then the hello
        // under the new key.
        net.Add(b.Host, b.Engine);
        a.Clock.Advance(TimeSpan.FromMinutes(2));
        b.Clock.Advance(TimeSpan.FromMinutes(2));
        await a.Engine.CheckAsync(onA.Id);
        Assert.Equal(MsfpEngine.KeyCurrent, (await a.Engine.PeerAsync(onA.Id)).OurKey);
        Assert.Equal((await a.Engine.ServerAsync()).Fingerprint, (await b.Engine.PeerAsync(onB.Id)).Fingerprint);

        // Away again for two changes: the second ends the first, and B, still
        // on the key before, is lost.
        net.Remove(b.Host);
        await a.Engine.RotateAsync();
        await a.Engine.RotateAsync();
        Assert.Equal(MsfpEngine.KeyLost, (await a.Engine.PeerAsync(onA.Id)).OurKey);
        Assert.Single(a.Events.Of<OurKeyLost>());
        net.Add(b.Host, b.Engine);
        Assert.Equal(MsfpError.KeyLost, (await Assert.ThrowsAsync<MsfpException>(() => a.Engine.CheckAsync(onA.Id))).Code);
        // Another key action at once: refused.
        var t1 = a.Engine.RotateAsync();
        var e = await Record.ExceptionAsync(() => a.Engine.RotateAsync());
        await t1;
        Assert.True(e is null || e is MsfpException { Code: MsfpError.RotationUnderWay });
    }

    [Fact]
    public async Task TamperedAnswersRefused()
    {
        var (net, a, b, onA, _, vs) = await NewPairAsync();
        var hello = vs["hello"];
        foreach (var (ct, body, sig) in new[]
                 {
                     ("text/plain", hello.S("answerBody"), hello.S("answerSignature")),
                     (hello.S("answerContentType"), hello.S("answerBody") + " ", hello.S("answerSignature")),
                     (hello.S("answerContentType"), hello.S("answerBody"), ""),
                 })
        {
            net.Add(b.Host, _ => Task.FromResult(new MsfpResponse
            {
                Status = 200,
                Headers = [new("Content-Type", ct), new(Signing.HeaderSignature, sig)],
                Body = Encoding.UTF8.GetBytes(body),
            }));
            a.Random.Queue(hello.S("nonce"));
            var e = await Assert.ThrowsAsync<MsfpException>(() => a.Engine.CheckAsync(onA.Id));
            Assert.Equal(MsfpError.Unreachable, e.Code);
        }

        // Nothing was kept of them.
        Assert.Null((await a.RecordAsync(onA.Id)).Versions);
    }

    // The addresses of strict.json's uncallableAddresses, in hellos saying
    // inbound false, through an engine: each valid one is stored at once,
    // each invalid one refused, signed, and nothing kept (MSFP
    // core sections 5.2 and 9.2).
    [Fact]
    public async Task UncallableAddressVectorsThroughEngine()
    {
        var (_, a, _, onA, _, _) = await NewPairAsync();
        var keyB = MsfpKey.FromSeed(Convert.FromHexString(Vectors.Seed("b")));
        var stored = onA.Address;
        foreach (var c in Vectors.Load("msfp/v0/strict.json")["uncallableAddresses"]!.AsArray().Select(x => x!.AsObject()))
        {
            var text = c.S("text");
            var body = JsonOut.Object(o => o.String("state", "paired").Integers("core", [1]).String("address", text).Bool("inbound", false));
            var res = await Kit.SendAsync(a, keyB, "hello", body);
            var got = await a.RecordAsync(onA.Id);
            if (c["valid"]!.GetValue<bool>())
            {
                Assert.True(res.Status == 200 && got.Address == text && got.Inbound == Inbound.No, $"{text}: {res.Status}, B at {got.Address}");
            }
            else
            {
                Assert.True(res.Status == 404 && res.Signed() && got.Address == stored, $"{text}: {res.Status}, B at {got.Address}");
            }

            stored = got.Address;
        }
    }

    // The last three exchanges of exchanges.json from B's side, through its
    // engine: B can't be called, at the vectors' address; its scheduled
    // hellos carry inbound false and knownKey a; A's answer with a rotation
    // and a revocation makes B suspend a, then move to a2; B's next hello,
    // at once, carries knownKey a2, and A's answer under a2 is B's to keep.
    [Fact]
    public async Task UncallableExchangesFromCaller()
    {
        var (net, a, b, _, onB, vs) = await NewPairAsync();
        b.Random.Queue(Convert.ToHexStringLower(Base32Lower("4dq6fy7e4xtop2hj5lv6z3po54")));
        await b.Engine.SetUncallableAsync(true);
        Assert.Equal("https://msfp-4dq6fy7e4xtop2hj5lv6z3po54.invalid", (await b.Engine.ServerAsync()).Address);

        JsonObject answering = vs["hello"];
        var asked = new List<string>();
        net.Add(a.Host, async r =>
        {
            using var m = new MemoryStream();
            await r.Body.CopyToAsync(m);
            lock (asked)
            {
                asked.Add(Encoding.UTF8.GetString(m.ToArray()));
            }

            return new MsfpResponse
            {
                Status = answering["status"]!.GetValue<int>(),
                Headers = [new("Content-Type", answering.S("answerContentType")), new(Signing.HeaderSignature, answering.S("answerSignature"))],
                Body = Encoding.UTF8.GetBytes(answering.S("answerBody")),
            };
        });

        async Task SendAsync(string name, long at)
        {
            var v = vs[name];
            answering = v;
            asked.Clear();
            b.Clock.Set(at);
            b.Random.Queue(v.S("nonce"));
            await b.Engine.RunOnceAsync();
            var got = JsonNode.Parse(Assert.Single(asked))!.AsObject();
            var want = JsonNode.Parse(v.S("body"))!.AsObject();
            foreach (var member in new[] { "address", "inbound", "knownKey" })
            {
                Assert.Equal(want[member]?.ToJsonString(), got[member]?.ToJsonString());
            }
        }

        await SendAsync("hello, from a server that can't be called", 1800000240);
        var rec = await b.RecordAsync(onB.Id);
        Assert.Equal(PeerState.Paired, rec.State);
        Assert.Equal(1800000240, rec.Seen!.Value.ToUnixTimeSeconds());
        Assert.Single(rec.Pins!);
        await SendAsync("hello, answered with a rotation and a revocation", 1800300000);
        rec = await b.RecordAsync(onB.Id);
        Assert.Equal(Vectors.Public("a2"), Base64UrlText.Encode(rec.Key));
        Assert.Null(rec.Suspended);
        Assert.NotEmpty(b.Events.Of<PeerSuspended>());
        Assert.NotEmpty(b.Events.Of<PeerRotated>());
        Assert.NotEmpty(b.Events.Of<PeerResumed>());
        await SendAsync("hello, with the new key known", 1800300060);
        Assert.Equal(1800300060, (await b.RecordAsync(onB.Id)).Seen!.Value.ToUnixTimeSeconds());
    }

    // exchanges.json, the last three, from A's side: B can't be called. A
    // revokes a and rotates to a2: its answer to B's hello, signed with a,
    // carries both statements, newly dated, as the vector's; B's next hello
    // knows a2, and is answered under it.
    [Fact]
    public async Task UncallableExchangesFromAnswerer()
    {
        var (_, a, _, onA, _, vs) = await NewPairAsync();
        a.Clock.Set(1800000240);
        var res = await ServeAsync(a.Engine, vs["hello, from a server that can't be called"]);
        Assert.Equal(200, res.Status);
        Assert.Equal(Inbound.No, (await a.RecordAsync(onA.Id)).Inbound);
        a.Clock.Set(1800300000);
        a.Random.Queue(Vectors.Seed("a3"));
        Assert.True(await a.Engine.KeyStolenAsync());
        var pulled = vs["hello, answered with a rotation and a revocation"];
        res = await ServeAsync(a.Engine, pulled);
        Assert.Equal(200, res.Status);
        Assert.True(Signing.CheckResponse(Base64UrlText.Decode(Vectors.Public("a"))!, pulled.S("nonceText"), 200, res.Header("Content-Type"),
            res.Body.Span, res.Header(Signing.HeaderSignature)));
        var got = JsonNode.Parse(res.Body.Span)!.AsObject();
        var want = JsonNode.Parse(pulled.S("answerBody"))!.AsObject();
        Assert.Equal(want["rotation"]!.ToJsonString(), got["rotation"]!.ToJsonString());
        Assert.Equal(want["revocation"]!.ToJsonString(), got["revocation"]!.ToJsonString());
        a.Clock.Set(1800300060);
        var known = vs["hello, with the new key known"];
        res = await ServeAsync(a.Engine, known);
        Assert.Equal(200, res.Status);
        Assert.True(Signing.CheckResponse(Base64UrlText.Decode(Vectors.Public("a2"))!, known.S("nonceText"), 200, res.Header("Content-Type"),
            res.Body.Span, res.Header(Signing.HeaderSignature)));
        Assert.DoesNotContain("\"rotation\"", Encoding.UTF8.GetString(res.Body.Span), StringComparison.Ordinal);
        Assert.Null((await a.Engine.ServerAsync()).RotationSince);
    }

    private static byte[] Base32Lower(string text)
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. output];
    }
}
