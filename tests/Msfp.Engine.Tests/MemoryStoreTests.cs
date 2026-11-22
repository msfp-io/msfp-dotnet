using System.Text;
using Msfp.Stores;

namespace Msfp.Tests;

// Go's store/memory tests, and what else the shared state promises.
public sealed class MemoryStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    internal static Title Video(string key) =>
        new VideoTitle { Key = key, Kind = "movie", Libraries = ["L"], Name = "fr" };

    // A transaction that fails keeps nothing; one that succeeds keeps all;
    // what a getter returns is a copy; a read can't write.
    [Fact]
    public async Task Transactions()
    {
        using var st = new MemoryStore();
        var p = new PeerRecord { Id = "p1", Key = B("key one"), Name = "one", Pins = ["x"] };
        var boom = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await st.UpdateAsync(async tx =>
            {
                await tx.PutPeerAsync(p);
                await tx.PutCodeAsync(new CodeRecord { Id = "c1", SecretHash = B("h") });
                throw new InvalidOperationException("boom");
            }, Ct));
        Assert.Equal("boom", boom.Message);
        await st.ReadAsync(async tx =>
        {
            Assert.Empty(await tx.PeersAsync());
            Assert.Null(await tx.CodeByHashAsync(B("h")));
        }, Ct);

        await st.UpdateAsync(async tx => await tx.PutPeerAsync(p), Ct);
        await st.UpdateAsync(async tx =>
        {
            var got = (await tx.PeerByKeyAsync(B("key one")))!;
            got.Pins![0] = "changed";
            got.Name = "changed";
        }, Ct);
        await st.ReadAsync(async tx =>
        {
            var got = (await tx.PeerAsync("p1"))!;
            Assert.Equal("one", got.Name);
            Assert.Equal("x", got.Pins![0]);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.PutPeerAsync(p));
        }, Ct);

        // A new key: found by it, no longer by the old one.
        p.Key = B("key two");
        await st.UpdateAsync(async tx => await tx.PutPeerAsync(p), Ct);
        await st.ReadAsync(async tx =>
        {
            Assert.Null(await tx.PeerByKeyAsync(B("key one")));
            Assert.NotNull(await tx.PeerByKeyAsync(B("key two")));
        }, Ct);
    }

    // A peer's titles: put, paged in key order, ended by a reading, dropped
    // with the peer.
    [Fact]
    public async Task PeerTitles()
    {
        using var st = new MemoryStore();
        await st.UpdateAsync(async tx =>
        {
            await tx.PutPeerAsync(new PeerRecord { Id = "p1", Key = B("k") });
            await tx.PutPeerTitlesAsync("p1", 1, [Video("movie/tmdb/3"), Video("movie/tmdb/1"), Video("movie/tmdb/2")]);
            await tx.PutPeerTitlesAsync("p1", 2, [Video("movie/tmdb/2")]);
            await tx.MarkSentAsync("p1", ["movie/tmdb/9"]);
        }, Ct);
        await st.ReadAsync(async tx =>
        {
            var page = await tx.PeerTitlesAsync("p1", "movie/tmdb/1", 1);
            var only = Assert.IsType<VideoTitle>(Assert.Single(page));
            Assert.Equal("movie/tmdb/2", only.Key);
            Assert.Equal("fr", only.Name);
            Assert.Single(await tx.SentAsync("p1", ["movie/tmdb/9", "movie/tmdb/8"]));
        }, Ct);
        await st.UpdateAsync(async tx => await tx.EndReadingAsync("p1", 2), Ct);
        await st.ReadAsync(async tx => Assert.Single(await tx.PeerTitlesAsync("p1", "", 10)), Ct);
        await st.UpdateAsync(async tx => await tx.DeletePeerAsync("p1"), Ct);
        await st.ReadAsync(async tx =>
        {
            Assert.Empty(await tx.PeerTitlesAsync("p1", "", 10));
            Assert.Empty(await tx.SentAsync("p1", ["movie/tmdb/9"]));
        }, Ct);
    }

    // Forgotten keys, trimmed by age.
    [Fact]
    public async Task Forgotten()
    {
        using var st = new MemoryStore();
        var old = DateTimeOffset.FromUnixTimeSeconds(1000);
        var recent = DateTimeOffset.FromUnixTimeSeconds(5000);
        await st.UpdateAsync(async tx =>
        {
            await tx.ForgetAsync(new ForgottenKey { KeyHash = B("a"), At = old });
            await tx.ForgetAsync(new ForgottenKey { KeyHash = B("b"), At = recent, Revoked = true });
        }, Ct);
        await st.UpdateAsync(async tx => await tx.TrimForgottenAsync(DateTimeOffset.FromUnixTimeSeconds(2000)), Ct);
        await st.ReadAsync(async tx =>
        {
            Assert.Null(await tx.ForgottenAsync(B("a")));
            var f = await tx.ForgottenAsync(B("b"));
            Assert.NotNull(f);
            Assert.True(f.Revoked);
            Assert.Equal(B("b"), f.KeyHash);
        }, Ct);
        await st.UpdateAsync(async tx => await tx.UnforgetAsync(B("b")), Ct);
        await st.ReadAsync(async tx => Assert.Null(await tx.ForgottenAsync(B("b"))), Ct);
    }

    // Leftovers, in order; a deleted peer takes its own with it.
    [Fact]
    public async Task Leftovers()
    {
        using var st = new MemoryStore();
        await st.UpdateAsync(async tx =>
        {
            await tx.PutPeerAsync(new PeerRecord { Id = "p1", Key = B("k1") });
            await tx.PutLeftoverAsync(new Leftover("p2", "b"));
            await tx.PutLeftoverAsync(new Leftover("p1", "a"));
            await tx.PutLeftoverAsync(new Leftover("p2", "a"));
            await tx.PutLibraryIdAsync("host", "pub");
        }, Ct);
        await st.ReadAsync(async tx =>
        {
            Assert.Equal([new Leftover("p1", "a"), new Leftover("p2", "a"), new Leftover("p2", "b")], await tx.LeftoversAsync());
            Assert.Equal("pub", (await tx.LibraryIdsAsync())["host"]);
        }, Ct);
        await st.UpdateAsync(async tx =>
        {
            await tx.DeletePeerAsync("p1");
            await tx.DeleteLeftoverAsync(new Leftover("p2", "b"));
            await tx.DeletePeerAsync("nobody");
        }, Ct);
        await st.ReadAsync(async tx => Assert.Equal([new Leftover("p2", "a")], await tx.LeftoversAsync()), Ct);
    }

    // Codes, found by the hash of their secret, in ID order.
    [Fact]
    public async Task Codes()
    {
        using var st = new MemoryStore();
        await st.UpdateAsync(async tx =>
        {
            await tx.PutCodeAsync(new CodeRecord { Id = "c2", SecretHash = B("h2"), Note = "two" });
            await tx.PutCodeAsync(new CodeRecord { Id = "c1", SecretHash = B("h1") });
        }, Ct);
        await st.ReadAsync(async tx =>
        {
            Assert.Equal(["c1", "c2"], (await tx.CodesAsync()).Select(c => c.Id));
            Assert.Equal("two", (await tx.CodeByHashAsync(B("h2")))?.Note);
        }, Ct);
        await st.UpdateAsync(async tx => await tx.DeleteCodeAsync("c2"), Ct);
        await st.ReadAsync(async tx => Assert.Null(await tx.CodeByHashAsync(B("h2"))), Ct);
    }

    // The own log: changes listed latest per key, in key order; trimmed
    // under a floor; the seq bumped above both.
    [Fact]
    public async Task OwnLog()
    {
        using var st = new MemoryStore();
        var t0 = DateTimeOffset.FromUnixTimeSeconds(100);
        var t1 = DateTimeOffset.FromUnixTimeSeconds(200);
        await st.UpdateAsync(async tx =>
        {
            var own = (IOwnLogTx)tx;
            await own.PutOwnSnapshotAsync(new Dictionary<string, Title> { ["a"] = Video("a") });
            await own.AppendOwnChangesAsync([
                new OwnChange(1, "b", Video("b"), t0),
                new OwnChange(2, "a", Video("a"), t0),
                new OwnChange(3, "b", null, t1),
            ]);
        }, Ct);
        await st.ReadAsync(async tx =>
        {
            var own = (IOwnLogTx)tx;
            var changes = await own.OwnChangesAsync(0, "", 10);
            Assert.Equal([("a", 2L), ("b", 3L)], changes.Select(c => (c.Key, c.Seq)));
            Assert.Null(changes[1].Title);
            Assert.Single(await own.OwnChangesAsync(0, "a", 10));
            Assert.Single(await own.OwnChangesAsync(0, "", 1));
            Assert.Equal(3, await own.OwnSeqAsync());
            Assert.Equal("a", Assert.Single(await own.OwnSnapshotAsync()).Key);
        }, Ct);
        await st.UpdateAsync(async tx => await ((IOwnLogTx)tx).TrimOwnChangesAsync(t1), Ct);
        var bumped = await st.UpdateAsync(async tx =>
        {
            var own = (IOwnLogTx)tx;
            Assert.Equal(2, await own.OwnFloorAsync());
            Assert.Equal(("b", 3L), (await own.OwnChangesAsync(0, "", 10)).Select(c => (c.Key, c.Seq)).Single());
            return await own.BumpOwnSeqAsync();
        }, Ct);
        Assert.Equal(4, bumped);
        Assert.Equal(4, await st.ReadAsync(async tx => await ((IOwnLogTx)tx).OwnSeqAsync(), Ct));
    }

    // A read started before an update sees the state as it was, all along.
    [Fact]
    public async Task ReadIsConsistent()
    {
        using var st = new MemoryStore();
        await st.UpdateAsync(async tx => await tx.PutPeerAsync(new PeerRecord { Id = "p1", Key = B("k1") }), Ct);
        var reading = new TaskCompletionSource();
        var updated = new TaskCompletionSource();
        var read = st.ReadAsync(async tx =>
        {
            Assert.Single(await tx.PeersAsync());
            reading.SetResult();
            await updated.Task;
            Assert.Single(await tx.PeersAsync());
            Assert.Null(await tx.PeerAsync("p2"));
        }, Ct).AsTask();
        await reading.Task;
        await st.UpdateAsync(async tx => await tx.PutPeerAsync(new PeerRecord { Id = "p2", Key = B("k2") }), Ct);
        updated.SetResult();
        await read;
        await st.ReadAsync(async tx => Assert.Equal(2, (await tx.PeersAsync()).Count), Ct);
    }

    // Updates run one at a time: their effects never interleave.
    [Fact]
    public async Task UpdatesOneAtATime()
    {
        using var st = new MemoryStore();
        var running = 0;
        var most = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            await st.UpdateAsync(async tx =>
            {
                most = Math.Max(most, Interlocked.Increment(ref running));
                await Task.Yield();
                await tx.PutLibraryIdAsync("h" + i, "p" + i);
                Interlocked.Decrement(ref running);
            }, Ct))));
        Assert.Equal(1, most);
        Assert.Equal(20, (await st.ReadAsync(async tx => await tx.LibraryIdsAsync(), Ct)).Count);
    }

    // A transaction kept past its end fails.
    [Fact]
    public async Task TxEnds()
    {
        using var st = new MemoryStore();
        IStoreTx? kept = null;
        await st.UpdateAsync(tx =>
        {
            kept = tx;
            return ValueTask.CompletedTask;
        }, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kept!.PeersAsync());
    }

    // The memory store seals nothing.
    [Fact]
    public void SealsNothing()
    {
        using var st = new MemoryStore();
        Assert.IsType<PlainSealer>(st.Sealer);
    }
}
