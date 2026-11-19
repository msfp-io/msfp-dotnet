using System.Text;
using Msfp.Stores;

namespace Msfp.Tests;

// Go's store/jsonfile tests, the files' modes, and the Go store's files.
public sealed class JsonFileStoreTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("msfp-jsonfile-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    // What a commit writes is there after reopening; a failed commit's
    // changes aren't; a catalog whose peer is gone (a crash between two
    // writes) is removed; no temporary file is left.
    [Fact]
    public async Task Reopen()
    {
        using (var st = JsonFileStore.Open(dir))
        {
            await st.UpdateAsync(async tx =>
            {
                await tx.PutPeerAsync(new PeerRecord { Id = "p1", Key = B("k"), Name = "one" });
                await tx.PutPeerTitlesAsync("p1", 1, [
                    new VideoTitle { Key = "movie/tmdb/1", Kind = "movie" },
                    new VideoTitle { Key = "series/tmdb/1399/e63056", Kind = "episode", Place = new Place { Season = 1, Episode = 0 } },
                ]);
            }, Ct);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await st.UpdateAsync(async tx =>
                {
                    await tx.PutPeerAsync(new PeerRecord { Id = "p2", Key = B("k2") });
                    throw new InvalidDataException();
                }, Ct));
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "peers", "p9.json"), """{"titles":{}}""", Ct);

        using (var st = JsonFileStore.Open(dir))
        {
            await st.ReadAsync(async tx =>
            {
                var peer = Assert.Single(await tx.PeersAsync());
                Assert.Equal("one", peer.Name);
                Assert.NotNull(await tx.PeerTitleAsync("p1", "movie/tmdb/1"));
                var ep = Assert.IsType<VideoTitle>(await tx.PeerTitleAsync("p1", "series/tmdb/1399/e63056"));
                Assert.Equal(new Place { Season = 1, Episode = 0 }, ep.Place);
                Assert.NotNull(await tx.PeerByKeyAsync(B("k")));
            }, Ct);
        }

        // As msfp-go's file store writes it: place, with season and episode (0 kept).
        var file = string.Concat((await File.ReadAllTextAsync(Path.Combine(dir, "peers", "p1.json"), Ct)).Where(c => !char.IsWhiteSpace(c)));
        Assert.Contains("\"place\":{\"season\":1,\"episode\":0}", file, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dir, "peers", "p9.json")), "a lone catalog kept");
        Assert.Empty(Directory.GetFiles(dir, ".tmp-*", SearchOption.AllDirectories));
    }

    // One process at a time: a second Open fails until the first is closed.
    [Fact]
    public void OpenedOnce()
    {
        var st = JsonFileStore.Open(dir);
        var e = Assert.Throws<IOException>(() => JsonFileStore.Open(dir));
        Assert.Contains("another process uses this directory", e.Message, StringComparison.Ordinal);
        st.Dispose();
        JsonFileStore.Open(dir).Dispose();
    }

    // Every file is the owner's alone, every directory too; the seal key is
    // made on first use.
    [Fact]
    public async Task Modes()
    {
        var sub = Path.Combine(dir, "store");
        using (var st = JsonFileStore.Open(sub))
        {
            await st.UpdateAsync(async tx =>
            {
                await tx.PutServerAsync(new ServerRecord { Address = "https://a.example" });
                await tx.PutPeerAsync(new PeerRecord { Id = "p1", Key = B("k") });
                await tx.MarkSentAsync("p1", ["x"]);
                await ((IOwnLogTx)tx).BumpOwnSeqAsync();
            }, Ct);
            var box = await st.Sealer.SealAsync(B("seed"), "msfp current key", Ct);
            Assert.DoesNotContain("seed", Encoding.Latin1.GetString(box), StringComparison.Ordinal);
        }
        var files = Directory.GetFiles(sub, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(sub, f)).Order(StringComparer.Ordinal);
        Assert.Equal(["lock", "own.json", "peers/p1.json", "sealing.key", "state.json"], files);
        foreach (var f in Directory.GetFiles(sub, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, GoSamples.Mode(f));
        }
        foreach (var d in new[] { sub, Path.Combine(sub, "peers") })
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, GoSamples.Mode(d));
        }
        Assert.Equal(32, new FileInfo(Path.Combine(sub, "sealing.key")).Length);
    }

    // A deleted peer's catalog file goes with it.
    [Fact]
    public async Task DeletePeerRemovesItsFile()
    {
        using var st = JsonFileStore.Open(dir);
        await st.UpdateAsync(async tx =>
        {
            await tx.PutPeerAsync(new PeerRecord { Id = "p1", Key = B("k") });
            await tx.MarkSentAsync("p1", ["x"]);
        }, Ct);
        Assert.True(File.Exists(Path.Combine(dir, "peers", "p1.json")));
        await st.UpdateAsync(async tx => await tx.DeletePeerAsync("p1"), Ct);
        Assert.False(File.Exists(Path.Combine(dir, "peers", "p1.json")));
    }

    // A peer ID that can't be a file name fails the commit, and nothing is kept.
    [Fact]
    public async Task BadPeerId()
    {
        using var st = JsonFileStore.Open(dir);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await st.UpdateAsync(async tx =>
            {
                await tx.PutPeerAsync(new PeerRecord { Id = "../x", Key = B("k") });
                await tx.MarkSentAsync("../x", ["x"]);
            }, Ct));
        await st.ReadAsync(async tx => Assert.Empty(await tx.PeersAsync()), Ct);
    }

    // The KeyFile option puts the key elsewhere.
    [Fact]
    public async Task KeyFileElsewhere()
    {
        var key = Path.Combine(dir, "apart.key");
        using var st = JsonFileStore.Open(Path.Combine(dir, "data"), new JsonFileStoreOptions { KeyFile = key });
        await st.Sealer.SealAsync(B("x"), "msfp ids secret", Ct);
        Assert.True(File.Exists(key));
        Assert.False(File.Exists(Path.Combine(dir, "data", "sealing.key")));
    }

    // A key file that can't be a key fails every seal.
    [Fact]
    public async Task BadKeyFile()
    {
        await File.WriteAllBytesAsync(Path.Combine(dir, "sealing.key"), [1, 2, 3], Ct);
        using var st = JsonFileStore.Open(dir);
        var e = await Assert.ThrowsAsync<MsfpException>(async () => await st.Sealer.SealAsync(B("x"), "msfp ids secret", Ct));
        Assert.Equal(MsfpError.Internal, e.Code);
    }

    private void WriteGoDirectory()
    {
        Directory.CreateDirectory(Path.Combine(dir, "peers"));
        File.WriteAllText(Path.Combine(dir, "state.json"), GoSamples.State);
        File.WriteAllText(Path.Combine(dir, "own.json"), GoSamples.Own);
        File.WriteAllText(Path.Combine(dir, "peers", "p1.json"), GoSamples.PeerP1);
        File.WriteAllBytes(Path.Combine(dir, "sealing.key"), Convert.FromHexString(GoSamples.SealingKey));
    }

    // A directory of the Go store opens here with every record as Go wrote
    // it, its sealing key included; written again here, it reads the same.
    [Fact]
    public async Task OpensGoDirectory()
    {
        WriteGoDirectory();
        using (var st = JsonFileStore.Open(dir))
        {
            await CheckGoRecords(st);
            Assert.Equal(B("secret"), await st.Sealer.OpenAsync(Convert.FromHexString(GoSamples.DirBox), "msfp ids secret", Ct));

            // Every file written again by this store.
            await st.UpdateAsync(async tx =>
            {
                await tx.PutServerAsync((await tx.ServerAsync())!);
                await tx.PutPeerTitlesAsync("p1", 3, []);
                await ((IOwnLogTx)tx).AppendOwnChangesAsync([]);
            }, Ct);
        }
        using (var st = JsonFileStore.Open(dir))
        {
            await CheckGoRecords(st);
        }
    }

    private static async Task CheckGoRecords(JsonFileStore st)
    {
        var t1 = new DateTimeOffset(2026, 11, 18, 21, 30, 15, TimeSpan.Zero).AddTicks(1234567);
        var t2 = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        await st.ReadAsync(async tx =>
        {
            var server = (await tx.ServerAsync())!;
            Assert.Equal(B("cur"), server.Current);
            Assert.Equal(B("nc"), server.NextCommitment);
            Assert.Null(server.Next);
            Assert.Equal(B("ids"), server.IdsSecret);
            Assert.Equal("https://old.example", server.Former);
            Assert.Equal(t1, server.MovedAt);
            Assert.True(server.Reached);
            Assert.Null(server.OldSince);

            var peers = await tx.PeersAsync();
            Assert.Equal(["p1", "p2"], peers.Select(p => p.Id));
            var p = peers[0];
            Assert.Equal(B("peer key"), p.Key);
            Assert.Equal(t1, p.Created);
            Assert.Equal(PeerState.Paired, p.State);
            Assert.True(p.WasPaired);
            Assert.Equal(t2, p.Paired);
            Assert.Null(p.Seen);
            Assert.Equal(Inbound.Yes, p.Inbound);
            Assert.Equal("https://p1b.example", p.Move?.Address);
            Assert.Equal(t1, p.Move?.Since);
            Assert.Equal([1], p.Versions?.Core);
            Assert.Equal([1], p.Versions?.Parts?["catalog"]);
            Assert.Equal(["pin1"], p.Pins);
            Assert.Equal(B("o"), p.Pending?.Old);
            Assert.Null(p.Pending?.Next);
            Assert.Equal(42, p.Pending?.Date);
            Assert.Equal(B("s2"), p.Pending?.NewSignature);
            Assert.Equal("old", p.OurKey);
            Assert.True(Assert.Single(p.Wants!).Wanted);
            Assert.Equal(new Limits { Streams = 2, Transcodes = 1 }, p.Limits);
            Assert.Equal(3, p.TheirLimits?.Streams);
            Assert.Equal(7, p.CatalogRead.Seq);
            Assert.Equal(["L1"], p.CatalogRead.Wants);
            var zero = peers[1];
            Assert.Equal(default, zero.Created);
            Assert.Equal(new PeerState(""), zero.State);
            Assert.Equal(default, zero.Inbound);
            Assert.Equal("p2", (await tx.PeerByKeyAsync(B("other")))?.Id);

            var code = (await tx.CodeByHashAsync(B("hash")))!;
            Assert.Equal(B("sec"), code.Secret);
            Assert.Equal(t2, code.Expires);
            var f = (await tx.ForgottenAsync(B("kh")))!;
            Assert.True(f.Revoked);
            Assert.Equal(t1, f.At);
            Assert.Equal("pub1", (await tx.LibraryIdsAsync())["host1"]);
            Assert.Equal(new Leftover("p1", "pb1"), Assert.Single(await tx.LeftoversAsync()));

            var titles = await tx.PeerTitlesAsync("p1", "", 10);
            var film = Assert.IsType<VideoTitle>(titles[0]);
            Assert.Equal(["L1"], film.Libraries);
            Assert.Equal(2160, film.Video?.Height);
            Assert.Equal(["dv"], film.Video?.Hdr);
            var song = Assert.IsType<MusicTitle>(titles[1]);
            Assert.Equal(("Song", 1, 2, 1000L), (song.Name, song.Disc, song.Number, song.DurationMs));
            Assert.Single(await tx.SentAsync("p1", ["movie/tmdb/9"]));

            var own = (IOwnLogTx)tx;
            Assert.Equal("movie/tmdb/5", Assert.Single(await own.OwnSnapshotAsync()).Key);
            var changes = await own.OwnChangesAsync(0, "", 10);
            Assert.Equal([1L, 2L], changes.Select(c => c.Seq));
            Assert.IsType<VideoTitle>(changes[0].Title);
            Assert.Null(changes[1].Title);
            Assert.Equal(t1, changes[0].At);
            Assert.Equal(2, await own.OwnSeqAsync());
        }, Ct);
    }
}
