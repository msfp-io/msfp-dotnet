using System.Text;
using Msfp.Protocol;

namespace Msfp.Tests;

/// <summary>A peer's rotations as this server receives them (MSFP core section 12.3): committed, refused, pending and confirmed.</summary>
public sealed class PeerKeyTests
{
    private static async Task<byte[]> Sealed(TestServer s, Func<ServerRecord, byte[]?> pick, string context)
    {
        ServerRecord? r = null;
        await s.Store.ReadAsync(async tx => r = await tx.ServerAsync(), CancellationToken.None);
        return await PlainSealer.Instance.OpenAsync(pick(r!)!, context, CancellationToken.None);
    }

    private static async Task<MsfpResponse> SendAsync(TestServer to, MsfpKey signer, string call, byte[] body)
    {
        var target = "/api/v1/federation/" + call;
        var h = new HeaderList().Add("Content-Type", "application/json");
        Signing.SignRequest("POST", to.Host, target, body, signer, to.Clock.GetUtcNow()).Apply(h);
        return await to.Engine.HandleFederationAsync(new MsfpRequest { Method = "POST", Target = target, Headers = h, Body = new MemoryStream(body) });
    }

    [Fact]
    public async Task CommittedRefusedPendingConfirmed()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org", "Server A");
        var b = await TestServer.StartAsync(net, "https://b.example.org", "Server B");
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        var onB = await b.Engine.RedeemCodeAsync(code);
        var current = MsfpKey.FromSeed(await Sealed(a, r => r.Current, "msfp current key"));
        var next = MsfpKey.FromSeed(await Sealed(a, r => r.Next, "msfp next key"));
        var now = b.Clock.GetUtcNow();

        // To a key it didn't commit to: refused, kept for the operator.
        var thief = MsfpKey.Generate();
        var res = await SendAsync(b, current, "rotate", Rotations.SignRotation(current, thief, null, now).Body());
        Assert.Equal(409, res.Status);
        Assert.Contains("rotation-refused", Encoding.UTF8.GetString(res.Body.Span), StringComparison.Ordinal);
        Assert.Single(b.Events.Of<RotationRefused>());
        Assert.NotNull((await b.RecordAsync(onB.Id)).Refused);
        await b.Engine.DismissRefusedAsync(onB.Id);
        Assert.Null((await b.RecordAsync(onB.Id)).Refused);

        // A bad statement: invalid-request.
        res = await SendAsync(b, current, "rotate", "{\"newKey\":5}"u8.ToArray());
        Assert.Equal(400, res.Status);

        // To the committed key, committing to none: rotated at once.
        res = await SendAsync(b, current, "rotate", Rotations.SignRotation(current, next, null, now).Body());
        Assert.Equal(200, res.Status);
        Assert.Equal("{\"state\":\"rotated\"}", Encoding.UTF8.GetString(res.Body.Span));
        Assert.Equal(next.Fingerprint, (await b.Engine.PeerAsync(onB.Id)).Fingerprint);
        Assert.Equal("", (await b.Engine.PeerAsync(onB.Id)).CommitmentFrom);

        // No commitment now: the next rotation waits for the operator (202).
        var third = MsfpKey.Generate();
        res = await SendAsync(b, next, "rotate", Rotations.SignRotation(next, third, null, now).Body());
        Assert.Equal(202, res.Status);
        Assert.Equal("{\"state\":\"pending\"}", Encoding.UTF8.GetString(res.Body.Span));
        Assert.Single(b.Events.Of<RotationPending>());
        Assert.Equal(third.Fingerprint, (await b.Engine.PeerAsync(onB.Id)).Pending!.Fingerprint);
        // Back to a former key: never.
        res = await SendAsync(b, next, "rotate", Rotations.SignRotation(next, current, null, now).Body());
        Assert.Equal(409, res.Status);
        net.Remove(a.Host); // the confirming hello can't reach A: no matter
        var v = await b.Engine.ConfirmRotationAsync(onB.Id);
        Assert.Equal(third.Fingerprint, v.Fingerprint);
        await Assert.ThrowsAsync<MsfpException>(() => b.Engine.ConfirmRotationAsync(onB.Id));
    }

    [Fact]
    public async Task RevokedThenMovedToTheCommittedKey()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org", "Server A");
        var b = await TestServer.StartAsync(net, "https://b.example.org", "Server B");
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        var onB = await b.Engine.RedeemCodeAsync(code);
        var current = MsfpKey.FromSeed(await Sealed(a, r => r.Current, "msfp current key"));
        var next = MsfpKey.FromSeed(await Sealed(a, r => r.Next, "msfp next key"));
        var now = b.Clock.GetUtcNow();
        var res = await SendAsync(b, current, "revoke", Rotations.SignRevocation(current, now).Body());
        Assert.Equal("{\"state\":\"suspended\"}", Encoding.UTF8.GetString(res.Body.Span));
        Assert.Equal(MsfpError.Suspended, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.CheckAsync(onB.Id))).Code);
        // Suspended, a thief's rotation is refused; the committed one lifts it.
        res = await SendAsync(b, current, "rotate", Rotations.SignRotation(current, MsfpKey.Generate(), null, now).Body());
        Assert.Equal(409, res.Status);
        res = await SendAsync(b, current, "rotate", Rotations.SignRotation(current, next, null, now).Body());
        Assert.Equal(200, res.Status);
        Assert.Null((await b.RecordAsync(onB.Id)).Suspended);
        Assert.Single(b.Events.Of<PeerResumed>());
        // The revoked key is never heard again.
        Assert.Null((await b.RecordAsync(onB.Id)).PreviousUntil);
    }
}
