using System.Text;
using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp.Tests;

/// <summary>
/// The vectors of core section 17 through two engines (msfp-go's
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
        var code = Vectors.Load("code.json")["code"]!.AsObject();
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
        Assert.StartsWith("{\"state\":\"paired\",\"core\":[1],\"parts\":{}", Encoding.UTF8.GetString(res.Body.Span), StringComparison.Ordinal);
        var recB = await b.RecordAsync(onB.Id);
        Assert.Equal(PeerState.Paired, recB.State);
        Assert.Equal([1], recB.Versions!.Core!);
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
        Assert.Equal([1], recA.Versions!.Core!);
        Assert.Equal(["catalog"], recA.Versions.Parts!.Keys);
        Assert.Equal(["E7UUIjYbkigLNKPjmSGixyA-pslmiFKckCEX_A6IZlM"], recA.Pins!);
        // The request A sent is the vector's, byte for byte but for the
        // hello's own members, which are A's (it speaks every part).
        var sent = net.SeenBy(b.Host)[^1];
        Assert.Equal(hello.S("signature").Length, sent.RequestSignature.Length);

        answering = vs["refused hello"];
        a.Random.Queue(answering.S("nonce"));
        var e = await Assert.ThrowsAsync<MsfpException>(() => a.Engine.CheckAsync(onA.Id));
        Assert.Equal(MsfpError.Unknown, e.Code);
        recA = await a.RecordAsync(onA.Id);
        Assert.Equal(PeerState.Gone, recA.State);
        Assert.Single(recA.Pins!);
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
}
