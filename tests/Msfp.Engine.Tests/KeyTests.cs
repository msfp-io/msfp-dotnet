using System.Security.Cryptography;
using Msfp.Protocol;
using Msfp.Stores;

namespace Msfp.Tests;

/// <summary>
/// Changes of key (MSFP core section 12) between engines on one fake clock, and
/// by requests signed by hand with a server's keys (msfp-go's keys_test.go).
/// </summary>
public sealed class KeyTests
{
    // The test keys of the vectors (keys.json): a, then a2 as A's next key;
    // b2 another.
    private const string SeedA = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private const string SeedA2 = "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f";
    private const string SeedB2 = "c0c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedf";

    private static MsfpKey KeyOf(string seed) => MsfpKey.FromSeed(Convert.FromHexString(seed));

    // An engine on clock whose first draws of 32 bytes are seeds: its key,
    // then its next key.
    private static Task<TestServer> KeyServerAsync(Network net, string address, FakeTime clock, params string[] seeds) =>
        TestServer.StartAsync(net, address, clock: clock, random: new ScriptedRandom().Queue(seeds));

    private static async Task Edit(TestServer s, string id, Action<PeerRecord> change) =>
        await s.Store.UpdateAsync(async tx =>
        {
            var p = (await tx.PeerAsync(id))!;
            change(p);
            await tx.PutPeerAsync(p);
        }, CancellationToken.None);

    private static int Count<T>(TestServer s)
        where T : MsfpEvent => s.Events.Of<T>().Count;

    [Fact]
    public async Task Rotation()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var a = await KeyServerAsync(net, "https://a.example.org", clock, SeedA, SeedA2);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var c = await TestServer.StartAsync(net, "https://c.example.org", clock: clock);
        var (bOnA, aOnB) = await Kit.LinkAsync(a, b);
        await Kit.LinkAsync(a, c);
        var before = await a.Engine.ServerAsync();
        Assert.Equal(KeyOf(SeedA2).Fingerprint, before.NextKey);
        Assert.True(before.NextOnServer);

        await a.Engine.RotateAsync();
        var after = await a.Engine.ServerAsync();
        Assert.Equal(before.NextKey, after.Fingerprint);
        Assert.NotEqual(before.NextKey, after.NextKey);
        Assert.True(after.NextOnServer);
        Assert.Null(after.RotationSince);
        Assert.Equal(0, after.NotRotated);
        Assert.Equal(2, Count<RotationAnswered>(a));
        Assert.Equal(1, Count<RotationEnded>(a));
        var onB = await b.Engine.PeerAsync(aOnB);
        Assert.Equal(after.Fingerprint, onB.Fingerprint);
        Assert.Equal("rotation", onB.CommitmentFrom);
        Assert.Equal(1, Count<PeerRotated>(b));
        Assert.Equal(MsfpEngine.KeyCurrent, (await a.Engine.PeerAsync(bOnA)).OurKey);
        await a.Engine.CheckAsync(bOnA);

        // The same rotation again, signed with a: rotated, nothing changes.
        var (ka, ka2) = (KeyOf(SeedA), KeyOf(SeedA2));
        clock.Advance(TimeSpan.FromMinutes(1));
        var rec = await b.RecordAsync(aOnB);
        var res = await Kit.SendAsync(b, ka, "rotate", Rotations.SignRotation(ka, ka2, rec.Commitment, clock.GetUtcNow()).Body());
        Assert.Equal(200, res.Status);
        Assert.Equal("{\"state\":\"rotated\"}", res.Text());
        rec = await b.RecordAsync(aOnB);
        Assert.Single(rec.Rotations!);
        Assert.Equal(ka2.Public(), rec.Key);
        // Another key, signed with a: refused.
        res = await Kit.SendAsync(b, ka, "rotate", Rotations.SignRotation(ka, MsfpKey.Generate(), null, clock.GetUtcNow()).Body());
        Assert.True(res.Status == 409 && res.Signed());
        // a heard for 10 minutes, then not at all.
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(200, (await Kit.SendAsync(b, ka, "hello", Kit.Paired)).Status);
        clock.Advance(TimeSpan.FromMinutes(6));
        res = await Kit.SendAsync(b, ka, "hello", Kit.Paired);
        Assert.True(res.Status == 404 && !res.Signed());
    }

    // A rotation that doesn't match the commitment is refused and shown; one
    // with no commitment held waits for the operator, through repeats, and
    // once confirmed the rotating server learns it at its next contact.
    [Fact]
    public async Task RotationRefusedAndPending()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var a = await KeyServerAsync(net, "https://a.example.org", clock, SeedA, SeedA2);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var (bOnA, aOnB) = await Kit.LinkAsync(a, b);
        var (ka, ka2) = (KeyOf(SeedA), KeyOf(SeedA2));

        // To a key it didn't commit to: 409, a warning, the key kept.
        var thief = MsfpKey.Generate();
        var res = await Kit.SendAsync(b, ka, "rotate", Rotations.SignRotation(ka, thief, null, clock.GetUtcNow()).Body());
        Assert.True(res.Status == 409 && res.Text().Contains("\"rotation-refused\"", StringComparison.Ordinal) && res.Signed());
        var v = await b.Engine.PeerAsync(aOnB);
        Assert.Equal(thief.Fingerprint, v.Refused?.Fingerprint);
        Assert.Equal(ka.Fingerprint, v.Fingerprint);
        Assert.Equal(1, Count<RotationRefused>(b));
        await b.Engine.DismissRefusedAsync(aOnB);
        Assert.Null((await b.Engine.PeerAsync(aOnB)).Refused);

        // No commitment held (as after a rotation statement with "-"): A's
        // rotate is answered pending.
        await Edit(b, aOnB, p =>
        {
            p.Commitment = null;
            p.CommitmentFrom = null;
        });
        await a.Engine.RotateAsync();
        var onA = await a.Engine.PeerAsync(bOnA);
        Assert.Equal((MsfpEngine.KeyOld, "pending"), (onA.OurKey, onA.RotationAnswer));
        var sv = await a.Engine.ServerAsync();
        Assert.NotNull(sv.RotationSince);
        Assert.Equal(1, sv.NotRotated);
        v = await b.Engine.PeerAsync(aOnB);
        Assert.Equal(ka2.Fingerprint, v.Pending?.Fingerprint);
        Assert.Equal(1, Count<RotationPending>(b));
        // Repeated: still pending, no new warning.
        clock.Advance(TimeSpan.FromSeconds(1));
        var repeat = Rotations.SignRotation(ka, ka2, Keys.Commitment(MsfpKey.Generate().PublicSpan), clock.GetUtcNow());
        res = await Kit.SendAsync(b, ka, "rotate", repeat.Body());
        Assert.Equal(202, res.Status);
        Assert.Equal("{\"state\":\"pending\"}", res.Text());
        Assert.Equal(1, Count<RotationPending>(b));
        // The operator confirms, after the 10 seconds between two rotates to
        // a peer whose answer was pending.
        clock.Advance(TimeSpan.FromSeconds(11));
        v = await b.Engine.ConfirmRotationAsync(aOnB);
        Assert.Equal(ka2.Fingerprint, v.Fingerprint);
        Assert.Null(v.Pending);
        // B's hello reached A, which sent its rotate again: B answered rotated.
        await a.WaitAsync();
        Assert.Equal(MsfpEngine.KeyCurrent, (await a.Engine.PeerAsync(bOnA)).OurKey);
        Assert.Null((await a.Engine.ServerAsync()).RotationSince);
        Assert.Equal(MsfpError.NoPending, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.ConfirmRotationAsync(aOnB))).Code);
        await b.Engine.CheckAsync(aOnB);
    }

    // A retired key never becomes current again, nor another peer's; a
    // pending key is forgotten when the pairing ends.
    [Fact]
    public async Task RotationRetiredAndEnded()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var a = await KeyServerAsync(net, "https://a.example.org", clock, SeedA, SeedA2);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var c = await KeyServerAsync(net, "https://c.example.org", clock, SeedB2);
        var (_, aOnB) = await Kit.LinkAsync(a, b);
        await Kit.LinkAsync(b, c);
        var (ka, ka2) = (KeyOf(SeedA), KeyOf(SeedA2));

        // a to a2, committing to none.
        Assert.Equal(200, (await Kit.SendAsync(b, ka, "rotate", Rotations.SignRotation(ka, ka2, null, clock.GetUtcNow()).Body())).Status);
        Assert.Equal("", (await b.Engine.PeerAsync(aOnB)).CommitmentFrom);
        // Back to a: refused, not pending.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(409, (await Kit.SendAsync(b, ka2, "rotate", Rotations.SignRotation(ka2, ka, null, clock.GetUtcNow()).Body())).Status);
        // To C's key: refused.
        Assert.Equal(409, (await Kit.SendAsync(b, ka2, "rotate", Rotations.SignRotation(ka2, KeyOf(SeedB2), null, clock.GetUtcNow()).Body())).Status);
        Assert.Null((await b.Engine.PeerAsync(aOnB)).Pending);
        // To a new key: pending. Then B ends the pairing: the pending key is
        // forgotten too, its requests get a signed 404.
        var n = MsfpKey.Generate();
        Assert.Equal(202, (await Kit.SendAsync(b, ka2, "rotate", Rotations.SignRotation(ka2, n, null, clock.GetUtcNow()).Body())).Status);
        await b.Engine.EndAsync(aOnB);
        foreach (var k in new[] { n, ka2, ka })
        {
            var res = await Kit.SendAsync(b, k, "hello", Kit.Paired);
            Assert.True(res.Status == 404 && res.Signed(), k.Fingerprint);
        }

        ForgottenKey? forgotten = null;
        await b.Store.ReadAsync(async tx => forgotten = await tx.ForgottenAsync(SHA256.HashData(n.Public())), CancellationToken.None);
        Assert.NotNull(forgotten);
    }

    // KeyStolen with the next key: B suspends a, then moves to a2 at once, a
    // never heard again; C, down then, moves at its next contact.
    [Fact]
    public async Task KeyStolenWithNext()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var a = await KeyServerAsync(net, "https://a.example.org", clock, SeedA, SeedA2);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var c = await TestServer.StartAsync(net, "https://c.example.org", clock: clock);
        var (_, aOnB) = await Kit.LinkAsync(a, b);
        var (cOnA, aOnC) = await Kit.LinkAsync(a, c);
        net.Remove(c.Host);

        Assert.True(await a.Engine.KeyStolenAsync());
        var v = await b.Engine.PeerAsync(aOnB);
        Assert.Equal(KeyOf(SeedA2).Fingerprint, v.Fingerprint);
        Assert.Null(v.Suspended);
        Assert.Equal(1, Count<PeerSuspended>(b));
        Assert.Equal(1, Count<PeerResumed>(b));
        Assert.Null((await b.RecordAsync(aOnB)).PreviousUntil);
        var res = await Kit.SendAsync(b, KeyOf(SeedA), "hello", Kit.Paired);
        Assert.True(res.Status == 404 && !res.Signed());
        var sv = await a.Engine.ServerAsync();
        Assert.NotNull(sv.RotationSince);
        Assert.Equal(1, sv.NotRotated);
        Assert.Equal(MsfpEngine.KeyOld, (await a.Engine.PeerAsync(cOnA)).OurKey);

        // C comes back and calls, after the minute between two rotates: A's
        // rotate follows, at that contact.
        net.Add(c.Host, c.Engine);
        clock.Advance(TimeSpan.FromMinutes(2));
        await c.Engine.CheckAsync(aOnC);
        await a.WaitAsync();
        Assert.Equal(MsfpEngine.KeyCurrent, (await a.Engine.PeerAsync(cOnA)).OurKey);
        Assert.Null((await a.Engine.ServerAsync()).RotationSince);
        Assert.Equal((await a.Engine.ServerAsync()).Fingerprint, (await c.Engine.PeerAsync(aOnC)).Fingerprint);
        // NoNextKey only when asked to rotate with no seed and no text.
        await a.Engine.ExportNextKeyAsync();
        await a.Engine.DeleteNextKeyAsync();
        Assert.Equal(MsfpError.NoNextKey, (await Assert.ThrowsAsync<MsfpException>(() => a.Engine.KeyStolenAsync())).Code);
        Assert.Equal(MsfpError.NotNextKey, (await Assert.ThrowsAsync<MsfpException>(() =>
            a.Engine.KeyStolenAsync("msfp-next-key-1." + Base64UrlText.Encode(new byte[32])))).Code);
    }

    // The next key exported, deleted, then given back to RotateAsync, white
    // space and all.
    [Fact]
    public async Task RotateWithExportedKey()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var a = await KeyServerAsync(net, "https://a.example.org", clock, SeedA, SeedA2);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        await Kit.LinkAsync(a, b);
        var (text, fp) = await a.Engine.ExportNextKeyAsync();
        Assert.Equal("msfp-next-key-1." + Base64UrlText.Encode(KeyOf(SeedA2).Seed()), text);
        Assert.Equal((await a.Engine.ServerAsync()).NextKey, fp);
        await a.Engine.DeleteNextKeyAsync();
        var sv = await a.Engine.ServerAsync();
        Assert.False(sv.NextOnServer);
        Assert.Equal(fp, sv.NextKey);
        Assert.Equal(MsfpError.NoNextKey, (await Assert.ThrowsAsync<MsfpException>(() => a.Engine.RotateAsync())).Code);
        Assert.Equal(MsfpError.NotNextKey, (await Assert.ThrowsAsync<MsfpException>(() =>
            a.Engine.RotateAsync("overfin-next-key-1." + text["msfp-next-key-1.".Length..]))).Code);
        await a.Engine.RotateAsync(text[..20] + " \n\t" + text[20..] + "\n");
        sv = await a.Engine.ServerAsync();
        Assert.Equal(fp, sv.Fingerprint);
        Assert.True(sv.NextOnServer);
    }

    // Two changes in a row: the first ends as the second starts, its
    // stragglers lost; the first's late ends never touch the second.
    [Fact]
    public async Task TwoChangesInARow()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var a = await KeyServerAsync(net, "https://a.example.org", clock, SeedA, SeedA2);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var c = await TestServer.StartAsync(net, "https://c.example.org", clock: clock);
        var (bOnA, aOnB) = await Kit.LinkAsync(a, b);
        var (cOnA, _) = await Kit.LinkAsync(a, c);
        var (ka, ka2) = (KeyOf(SeedA), KeyOf(SeedA2));

        net.Remove(c.Host);
        await a.Engine.RotateAsync();
        Assert.Equal(1, (await a.Engine.ServerAsync()).NotRotated);
        // The second: C, still on a, is lost; B, down now, stays on a2.
        net.Remove(b.Host);
        clock.Advance(TimeSpan.FromHours(1));
        await a.Engine.RotateAsync();
        var second = await a.Engine.ServerAsync();
        Assert.Equal(1, second.NotRotated);
        Assert.Equal(clock.GetUtcNow(), second.RotationSince);
        Assert.Equal(MsfpEngine.KeyLost, (await a.Engine.PeerAsync(cOnA)).OurKey);
        Assert.Equal(1, Count<OurKeyLost>(a));
        Assert.Equal(MsfpEngine.KeyOld, (await a.Engine.PeerAsync(bOnA)).OurKey);

        // Late: the first change's end, and B's move to a2, learnt now.
        Assert.False(await a.Engine.FinishRotationAsync(ka.Public(), CancellationToken.None));
        Assert.False(await a.Engine.PeerMovedAsync(bOnA, ka2.Public(), CancellationToken.None));
        var sv = await a.Engine.ServerAsync();
        Assert.Equal((second.RotationSince, 1, second.Fingerprint), (sv.RotationSince, sv.NotRotated, sv.Fingerprint));
        Assert.Equal(MsfpEngine.KeyOld, (await a.Engine.PeerAsync(bOnA)).OurKey);

        // B back: the daily rotate moves it, and the second change ends.
        net.Add(b.Host, b.Engine);
        clock.Advance(TimeSpan.FromHours(25));
        await a.Engine.RunOnceAsync();
        Assert.Null((await a.Engine.ServerAsync()).RotationSince);
        Assert.Equal(second.Fingerprint, (await b.Engine.PeerAsync(aOnB)).Fingerprint);
        // C: lost; A calls it no more.
        net.Add(c.Host, c.Engine);
        Assert.Equal(MsfpError.KeyLost, (await Assert.ThrowsAsync<MsfpException>(() => a.Engine.CheckAsync(cOnA))).Code);
    }

    // A peer that never moves is lost after 30 days, and the old key goes.
    [Fact]
    public async Task RotationThirtyDays()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var a = await KeyServerAsync(net, "https://a.example.org", clock, SeedA, SeedA2);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var (bOnA, _) = await Kit.LinkAsync(a, b);
        net.Remove(b.Host);
        await a.Engine.RotateAsync();
        for (var day = 1; day <= 30; day++)
        {
            clock.Advance(TimeSpan.FromHours(24));
            await a.Engine.RunOnceAsync();
        }

        Assert.Null((await a.Engine.ServerAsync()).RotationSince);
        Assert.Equal(1, Count<OurKeyLost>(a));
        Assert.Equal(MsfpEngine.KeyLost, (await a.Engine.PeerAsync(bOnA)).OurKey);
        Assert.DoesNotContain(net.SeenBy(b.Host), x => x.Target.EndsWith("/rotate", StringComparison.Ordinal));
    }

    // On the JSON file store, a restart in the middle of a change: the old
    // key and who moved are kept; the peer that hadn't moves at the daily
    // rotate after the restart.
    [Fact]
    public async Task RotationJsonRestart()
    {
        var dir = Directory.CreateTempSubdirectory("msfp-test-").FullName;
        try
        {
            var net = new Network();
            var clock = new FakeTime(1800000000);
            var st = JsonFileStore.Open(dir);
            var a = await TestServer.StartAsync(net, "https://a.example.org", clock: clock, store: st,
                random: new ScriptedRandom().Queue(SeedA, SeedA2));
            var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
            var c = await TestServer.StartAsync(net, "https://c.example.org", clock: clock);
            var (bOnA, _) = await Kit.LinkAsync(a, b);
            var (cOnA, aOnC) = await Kit.LinkAsync(a, c);
            net.Remove(c.Host);
            await a.Engine.RotateAsync();
            var before = await a.Engine.ServerAsync();
            a.Dispose();
            st.Dispose();

            using var st2 = JsonFileStore.Open(dir);
            clock.Advance(TimeSpan.FromHours(1));
            using var a2 = await TestServer.StartAsync(net, "https://a.example.org", clock: clock, store: st2);
            var sv = await a2.Engine.ServerAsync();
            Assert.Equal((before.Fingerprint, before.RotationSince, 1, before.NextKey), (sv.Fingerprint, sv.RotationSince, sv.NotRotated, sv.NextKey));
            Assert.Equal(MsfpEngine.KeyCurrent, (await a2.Engine.PeerAsync(bOnA)).OurKey);
            await a2.Engine.CheckAsync(bOnA);
            net.Add(c.Host, c.Engine);
            clock.Advance(TimeSpan.FromHours(24));
            await a2.Engine.RunOnceAsync();
            Assert.Equal(MsfpEngine.KeyCurrent, (await a2.Engine.PeerAsync(cOnA)).OurKey);
            Assert.Equal(sv.Fingerprint, (await c.Engine.PeerAsync(aOnC)).Fingerprint);
            Assert.Null((await a2.Engine.ServerAsync()).RotationSince);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
