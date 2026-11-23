using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Msfp.Tests;

// Why a peer's titles weren't read (msfp-go's readfailure_test.go): the
// previous draft's answers recognised, and each other failure's code.
public sealed partial class ReadFailureTests
{
    [GeneratedRegex("(\"id\":\"[^\"]*\")")]
    private static partial Regex LibraryId();

    [GeneratedRegex("^[a-z]+(-[a-z]+)*$")]
    private static partial Regex Code();

    // A's libraries answer as the previous draft sent it: each library with a kind.
    private static byte[]? LibrariesWithKinds(string path, byte[] body) =>
        path.EndsWith("/libraries", StringComparison.Ordinal)
            ? HostKit.Bytes(LibraryId().Replace(System.Text.Encoding.UTF8.GetString(body), "$1,\"kind\":\"movies\""))
            : null;

    private static async Task<LastReading> LastAsync(Sharing s) =>
        (await s.B.Engine.PeerAsync(s.OnB)).Reading ?? throw new InvalidOperationException("no reading kept");

    // A peer answering in the previous draft's shapes (libraries with kinds,
    // titles with kinds and no copies) fails the reading as older-draft; what
    // was kept stays. Either mark alone isn't enough.
    [Fact]
    public async Task OlderDraft()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/1", "en");
        using var s = await Sharing.StartAsync(h);
        var first = await LastAsync(s);
        Assert.Null(first.Failure);
        var lib = s.LibraryPub;
        var old = "{\"titles\":[{\"key\":\"series/tmdb/1399\",\"kind\":\"series\",\"library\":\"" + lib + "\"}," +
            "{\"key\":\"movie/tmdb/949\",\"kind\":\"movie\",\"library\":\"" + lib + "\",\"audio\":[\"en\"],\"video\":{\"height\":1080}}," +
            "{\"key\":\"movie/tmdb/603\",\"kind\":\"movie\",\"library\":\"" + lib + "\"}],\"seq\":4}";
        var page = old;
        var libraries = true;
        s.Hostile((path, _, body) => path.EndsWith("/titles", StringComparison.Ordinal) ? HostKit.Bytes(page)
            : libraries ? LibrariesWithKinds(path, body) : null);
        var events = s.B.Events;
        var e = await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.ReadNowAsync(s.OnB));
        Assert.Equal(MsfpError.Unreachable, e.Code);
        var last = await LastAsync(s);
        Assert.Equal(ReadFailure.OlderDraft, last.Failure);
        Assert.Null(last.Refusal);
        Assert.Equal(ReadFailure.OlderDraft, events.Of<CatalogReadEnded>()[^1].Failure);
        Assert.Equal(["movie/tmdb/1"], [.. (await s.PeerTitlesAsync()).Keys]);

        // The same titles, but the libraries of this draft: titles skipped
        // one by one, as a broken owner's; the reading reads.
        libraries = false;
        await s.B.Engine.ReadNowAsync(s.OnB);
        Assert.Null((await LastAsync(s)).Failure);
        bool SinceSent() => !s.Exchanges("titles")[^1].RequestBody.Contains("\"since\":0", StringComparison.Ordinal) &&
            s.Exchanges("titles")[^1].RequestBody.Contains("\"since\"", StringComparison.Ordinal);
        await s.B.Engine.ReadNowAsync(s.OnB);
        Assert.True(SinceSent(), "a reading of changes");

        // Libraries with kinds, and a film with copies: this draft's; and
        // with kinds in libraries, the whole list, whose titles tell.
        libraries = true;
        page = "{\"titles\":[{\"key\":\"movie/tmdb/2\",\"kind\":\"movie\",\"library\":\"" + lib + "\",\"copies\":[{\"id\":\"v1\",\"durationMs\":1000,\"mediaType\":\"video/matroska\",\"size\":5,\"etag\":\"t1\",\"audioTracks\":[]}]}," +
            "{\"key\":\"movie/tmdb/603\",\"kind\":\"movie\",\"library\":\"" + lib + "\"}]}";
        await s.B.Engine.ReadNowAsync(s.OnB);
        Assert.Null((await LastAsync(s)).Failure);
        Assert.False(SinceSent(), "libraries with kinds, and a reading of changes: its titles might not tell");

        // Titles without kinds, or series only: nothing tells, the reading reads.
        page = "{\"titles\":[{\"key\":\"movie/tmdb/603\",\"library\":\"" + lib + "\"}]}";
        await s.B.Engine.ReadNowAsync(s.OnB);
        page = "{\"titles\":[{\"key\":\"series/tmdb/1399\",\"kind\":\"series\",\"library\":\"" + lib + "\"}]}";
        await s.B.Engine.ReadNowAsync(s.OnB);
        Assert.Null((await LastAsync(s)).Failure);

        page = old;
        await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.ReadNowAsync(s.OnB));
        Assert.Equal(ReadFailure.OlderDraft, (await LastAsync(s)).Failure);
    }

    // What the previous draft's pages look like, and what isn't enough to say so.
    [Theory]
    [InlineData("[{\"key\":\"movie/tmdb/949\",\"kind\":\"movie\",\"library\":\"l\"}]", true, false)]
    [InlineData("[{\"key\":\"series/tmdb/1\",\"kind\":\"series\",\"library\":\"l\"},{\"key\":\"series/tmdb/1/s1/e2\",\"kind\":\"episode\",\"library\":\"l\",\"audio\":[\"en\"]}]", true, false)]
    [InlineData("[{\"key\":\"track/mb/0c000000-0000-4000-8000-000000000003\",\"kind\":\"track\",\"library\":\"l\",\"name\":\"x\",\"durationMs\":1}]", true, false)]
    [InlineData("[{\"key\":\"series/tmdb/1\",\"kind\":\"series\",\"library\":\"l\"}]", false, false)]
    [InlineData("[]", false, false)]
    [InlineData("[{\"key\":\"movie/tmdb/949\",\"kind\":\"movie\",\"library\":\"l\"},{\"key\":\"movie/tmdb/603\",\"library\":\"l\"}]", false, false)]
    [InlineData("[{\"key\":\"movie/tmdb/949\",\"kind\":\"series\",\"library\":\"l\"}]", false, false)]
    [InlineData("[{\"key\":\"movie/tmdb/949\",\"kind\":7,\"library\":\"l\"}]", false, false)]
    [InlineData("[{\"key\":\"movie/tmdb/0949\",\"kind\":\"movie\",\"library\":\"l\"}]", false, false)]
    [InlineData("[{\"key\":\"movie/tmdb/949\",\"kind\":\"movie\",\"library\":\"l\"},{\"key\":\"movie/tmdb/603\",\"kind\":\"movie\",\"library\":\"l\",\"copies\":[]}]", false, true)]
    [InlineData("[{\"key\":\"movie/tmdb/603\",\"library\":\"l\",\"copies\":[]}]", false, true)]
    public void OlderDraftPage(string page, bool old, bool current) =>
        Assert.Equal((old, current), MsfpEngine.OlderDraftPage(JsonNode.Parse(page)!.AsArray()));

    // Each other way a reading fails has its code; none holds an address.
    [Fact]
    public async Task Failures()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/1", "en");
        using var s = await Sharing.StartAsync(h);

        // An answer not of the call's form.
        s.Hostile((path, _, _) => path.EndsWith("/titles", StringComparison.Ordinal) ? "{\"titles\":{}}"u8.ToArray() : null);
        await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.ReadNowAsync(s.OnB));
        Assert.Equal(ReadFailure.NotUnderstood, (await LastAsync(s)).Failure);
        s.Net.Rewrite(s.A.Host, null);

        // Cancelled by the host: not the peer's doing, nothing kept.
        var before = await LastAsync(s);
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.B.Engine.ReadNowAsync(s.OnB, cancelled.Token));
        }

        Assert.Equal(before, await LastAsync(s));

        // Plain refusals, unsigned: a proxy, or a server that lost its records.
        s.Net.Down(s.A.Host, true);
        await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.ReadNowAsync(s.OnB));
        Assert.Equal(ReadFailure.RefusedUnsigned, (await LastAsync(s)).Failure);
        s.Net.Down(s.A.Host, false);

        // No answer at all; then the engine holds off, which keeps the
        // failure before it.
        s.Net.Remove(s.A.Host);
        await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.ReadNowAsync(s.OnB));
        Assert.Equal(ReadFailure.Unreachable, (await LastAsync(s)).Failure);
        try
        {
            await s.B.Engine.ReadNowAsync(s.OnB);
        }
        catch (MsfpException)
        {
        }

        Assert.Equal(ReadFailure.Unreachable, (await LastAsync(s)).Failure);
        Assert.All(ReadFailure.All, c => Assert.Matches(Code(), c));
        Assert.Equal(13, ReadFailure.All.Distinct().Count());
    }

    // A signed refusal is refused, with its problem type.
    [Fact]
    public async Task Refused()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/1", "en");
        using var s = await Sharing.StartAsync(h, cfgA: c => c with { Limits = new CallLimits { PeerRate = 0.001, PeerBurst = 8 } });
        MsfpException? e = null;
        for (var i = 0; i < 10 && e is null; i++)
        {
            try
            {
                await s.B.Engine.ReadNowAsync(s.OnB);
            }
            catch (MsfpException x)
            {
                e = x;
            }
        }

        Assert.Equal(MsfpError.RateLimited, e?.Code);
        var last = await LastAsync(s);
        Assert.Equal(ReadFailure.Refused, last.Failure);
        Assert.Equal("rate-limited", last.Refusal);
    }

    // Each engine error a reading may end with, and its code.
    [Fact]
    public async Task Codes()
    {
        var h = new FakeHost();
        using var s = await Sharing.StartAsync(h);
        var paired = await s.B.RecordAsync(s.OnB);
        var noCore = await s.B.RecordAsync(s.OnB);
        noCore.Versions = new() { Core = [7], Parts = new() };
        var cases = new (Exception? Error, PeerRecord Peer, bool Background, string? Failure, string? Refusal, bool Keep)[]
        {
            (null, paired, false, null, null, true),
            (new MsfpException(MsfpError.BackingOff), paired, false, null, null, false),
            (new OperationCanceledException(), paired, false, null, null, false),
            (new OperationCanceledException(), paired, true, ReadFailure.Timeout, null, true),
            (new MsfpException(MsfpError.Unreachable, "couldn't connect") { NoConnection = true }, paired, false, ReadFailure.Unreachable, null, true),
            (new MsfpException(MsfpError.Unreachable, "timed out") { NoConnection = true, Failure = ReadFailure.Timeout }, paired, false, ReadFailure.Timeout, null, true),
            (new MsfpException(MsfpError.CertificateRefused), paired, false, ReadFailure.CertificateRefused, null, true),
            (new MsfpException(MsfpError.RefusedDestination), paired, false, ReadFailure.Unreachable, null, true),
            (new MsfpException(MsfpError.Unreachable) { NotHeard = 404 }, paired, false, ReadFailure.RefusedUnsigned, null, true),
            (new MsfpException(MsfpError.NotFound) { SignedRefusal = true, Refusal = "not-found" }, paired, false, ReadFailure.Refused, "not-found", true),
            (new MsfpException(MsfpError.NotSupported) { SignedRefusal = true, Refusal = "not-supported" }, noCore, false, ReadFailure.Refused, "not-supported", true),
            (new MsfpException(MsfpError.NotPaired), paired, false, ReadFailure.NotPaired, null, true),
            (new MsfpException(MsfpError.KeyLost), paired, false, ReadFailure.NotPaired, null, true),
            (new MsfpException(MsfpError.Suspended), paired, false, ReadFailure.Suspended, null, true),
            (new MsfpException(MsfpError.Uncallable), paired, false, ReadFailure.Uncallable, null, true),
            (new MsfpException(MsfpError.NotSupported), paired, false, ReadFailure.CatalogOff, null, true),
            (new MsfpException(MsfpError.NotSupported), noCore, false, ReadFailure.NoCommonVersion, null, true),
            (new HttpRequestException("reset"), paired, false, ReadFailure.Unreachable, null, true),
            (new InvalidOperationException("the store failed"), paired, false, ReadFailure.Internal, null, true),
        };
        foreach (var c in cases)
        {
            Assert.Equal((c.Failure, c.Refusal, c.Keep), s.B.Engine.ReadFailureOf(c.Peer, c.Error, c.Background));
        }
    }
}
