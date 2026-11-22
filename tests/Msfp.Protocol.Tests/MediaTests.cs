using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>wire/media_test.go: playlists, and the hls, names and relayTypes cases of media.json.</summary>
public class MediaTests
{
    // The playback of the hls vectors: audio tracks a1 to a40.
    internal static bool VectorAudio(string id) =>
        id.StartsWith('a') && int.TryParse(id[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
        n is >= 1 and <= 40 && "a" + n.ToString(CultureInfo.InvariantCulture) == id;

    // The refusal of a playlist, null when it passes.
    private static ProtocolException? Refusal(string body, IReadOnlyCollection<string>? subset = null, Func<string, bool>? audio = null)
    {
        try
        {
            Media.FilterPlaylist(Encoding.UTF8.GetBytes(body), subset, audio);
            return null;
        }
        catch (ProtocolException e) when (e.Error == ProtocolError.Playlist)
        {
            return e;
        }
    }

    private static bool Passes(string body, IReadOnlyCollection<string>? subset = null, Func<string, bool>? audio = null) =>
        Refusal(body, subset, audio) is null;

    // Whether a refusal is at the step the case's reason names ("step 4:
    // …"), when it names one.
    internal static bool SameStep(string? error, string reason)
    {
        var colon = reason.IndexOf(':', StringComparison.Ordinal);
        var want = colon < 0 ? reason : reason[..colon];
        if (!want.StartsWith("step ", StringComparison.Ordinal))
        {
            return true;
        }

        return error is not null && error.Contains(want + ":", StringComparison.Ordinal);
    }

    // The names an owning server serves under a ticket (media section 1).
    [Fact]
    public void NameVectors()
    {
        var cases = TestVectors.Cases("media.json", "names");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var t = c["ticket"]!.AsObject();
            List<string> L(string name) => t[name]?.AsArray().Select(e => e!.GetValue<string>()).ToList() ?? [];
            var scope = new TicketScope
            {
                Kind = t.Str("kind"),
                Protocol = t.Str("protocol"),
                Subtitles = t["subtitles"]?.AsArray().Select(e => e!.AsObject())
                    .Select(e => new ServedSubtitle(e.Str("id"), e.Str("form"), e["ass"]?.GetValue<bool>() ?? false)).ToList() ?? [],
                FontCount = (int)(JsonMembers.AsInteger(t["fontCount"]) ?? 0),
                SheetCount = (int)(JsonMembers.AsInteger(t["sheetCount"]) ?? 0),
                Queue = L("queue"),
                Names = L("names"),
            };
            var ok = Media.Serves(scope, c.Str("text"), out var why);
            if (ok != c.Valid() || (!ok && why != c.Str("reason")))
            {
                failures.Add($"{c.Str("name")}: {ok} ({why}), want {c.Valid()} ({c.Str("reason")})");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "media.json names");
    }

    // The media type a viewer's server relays (media section 3).
    [Fact]
    public void RelayTypeVectors()
    {
        var cases = TestVectors.Cases("media.json", "relayTypes");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var got = c.Str("what") == "file" ? Media.RelayFileType(c.Str("value")) : Playback.RelayedMediaType(c.Str("value"));
            if (got != c.Str("output"))
            {
                failures.Add($"{c.Str("name")}: {got}, want {c.Str("output")}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "media.json relayTypes");
    }

    [Fact]
    public void HlsVectors()
    {
        var cases = TestVectors.Cases("media.json", "hls");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var e = Refusal(c.Str("text"), null, VectorAudio);
            if ((e is null) != c.Valid() || !SameStep(e?.Message, c.Str("reason")))
            {
                failures.Add($"{c.Str("name")}: {e?.Message}, want valid {c.Valid()} ({c.Str("reason")})");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "media.json hls");
    }

    private const string MediaHead = "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-TARGETDURATION:6\n";

    // A paired server's playlist passes only when it names files of the
    // same stream, by name: no path, no other server.
    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv0.m3u8\n", true)]
    [InlineData(MediaHead + "#EXT-X-MAP:URI=\"init0.mp4\"\n#EXTINF:6,\ns0.mp4\r\n#EXT-X-ENDLIST\n", true)]
    [InlineData("#EXTM3U\nhttps://elsewhere.example.org/s0.ts\n", false)]
    [InlineData("#EXTM3U\n../../api/v1/me\n", false)]
    [InlineData("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"https://k.example.org/key\"\n", false)]
    [InlineData("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=https://k.example.org/key\n", false)]
    [InlineData("#EXTM3U\n#EXT-X-MAP:uri=https://k.example.org/i.mp4\n", false)]
    [InlineData("#EXTM3U\n#EXT-X-MAP:URI=init0.mp4\n", false)]
    [InlineData("<html>\n", false)]
    [InlineData("#EXTM3U\n..\n", false)]
    [InlineData("#EXTM3U\n.\n", false)]
    [InlineData("#EXTM3U\n#EXT-X-MAP:URI=\"..\"\n", false)]
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n...\n", true)]
    [InlineData(MediaHead + "#EXTINF:6,\ns0.mp4\n", false)] // complete from the first fetch: a playlist that grows is refused
    [InlineData("#EXTM3U\n#EXT-X-VERSION:7\n", false)]      // neither a master nor a media playlist
    public void CheckPlaylist(string body, bool ok)
    {
        var passed = true;
        try
        {
            Media.CheckPlaylist(Encoding.UTF8.GetBytes(body), VectorAudio);
        }
        catch (ProtocolException e) when (e.Error == ProtocolError.Playlist)
        {
            passed = false;
        }

        Assert.Equal(ok, passed);
    }

    // Audio renditions name tracks the playback has: with no tracks (a null
    // audio), none passes.
    [Fact]
    public void PlaylistRenditions()
    {
        var master = "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"a2\",DEFAULT=YES,AUTOSELECT=YES,URI=\"a2.m3u8\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=1,AUDIO=\"audio\"\nv0.m3u8\n";
        Assert.Equal(master, Encoding.UTF8.GetString(Media.FilterPlaylist(Encoding.UTF8.GetBytes(master), Media.MasterSubset.ToList(), VectorAudio)));
        Assert.False(Passes(master), "a rendition of a playback without audio tracks");
        Assert.Contains("step 10:", Refusal(master)!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueStreamBase() =>
        Assert.Equal("https://a.example.org/api/v1/stream/t/queue/4/", Media.QueueStreamBase("https://a.example.org/api/v1/stream/t/", 4));

    // A relayed playlist keeps the subset's tags only: comments and other
    // tags are dropped, URIs checked in them all the same.
    [Fact]
    public void FilterPlaylistKeepsTheSubset()
    {
        var got = Media.FilterPlaylist("#EXTM3U\r\n# a comment\n#EXT-X-FOO:BAR=1\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-TARGETDURATION:6\n\n#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST"u8, Media.MediaSubset.ToList(), null);
        Assert.Equal("#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST\n", Encoding.UTF8.GetString(got));
        Assert.False(Passes("#EXTM3U\n#EXT-X-FOO:URI=\"a/b\"\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv0.m3u8\n", Media.MediaSubset.ToList()), "a dropped tag's URI still counts");
        var many = new StringBuilder("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv0.m3u8\n");
        for (var i = 0; i < Media.MaxPlaylistUris; i++)
        {
            many.Append("s.ts\n");
        }

        Assert.Contains("step 8:", Refusal(many.ToString())!.Message, StringComparison.Ordinal);

        // Offsets stay right past letters whose upper case is longer.
        Assert.False(Passes("#EXTM3U\n#EXTINF:6,ßßßß uri=\"a/b\"\nseg0.ts\n"), "a URI after non-ASCII text");
        var big = Assert.Throws<ProtocolException>(() => Media.CheckPlaylist(new byte[Media.MaxPlaylist + 1], null));
        Assert.Contains("step 1:", big.Message, StringComparison.Ordinal);
    }

    // What Overfin as the owning server sent of Jellyfin 12.2's HLS: within
    // the subset, kept whole.
    [Fact]
    public void FilterPlaylistOfJellyfin()
    {
        var master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5198950,AVERAGE-BANDWIDTH=5198950,VIDEO-RANGE=SDR,CODECS=\"avc1.64001F,mp4a.40.2\",RESOLUTION=1280x720,FRAME-RATE=24\nv0.m3u8\n";
        var media = "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:11\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-MAP:URI=\"init.mp4\"\n" +
            "#EXTINF:10.417000,\nseg0.mp4\n#EXTINF:10.416000,\nseg1.mp4\n#EXT-X-ENDLIST\n";
        Assert.Equal(master, Encoding.UTF8.GetString(Media.FilterPlaylist(Encoding.UTF8.GetBytes(master), Media.MasterSubset.ToList(), null)));
        Assert.Equal(media, Encoding.UTF8.GetString(Media.FilterPlaylist(Encoding.UTF8.GetBytes(media), Media.MediaSubset.ToList(), null)));
        Assert.Equal(["seg0.mp4", "seg1.mp4"], Media.UriLines(Encoding.UTF8.GetBytes(media)));
    }

    // A character players read as a line's end, inside a line, would hide
    // an address from the check.
    [Fact]
    public void FilterPlaylistHiddenLines()
    {
        var master = Media.MasterSubset.ToList();
        var media = Media.MediaSubset.ToList();
        Assert.False(Passes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\rhttps://elsewhere.example/v.m3u8\n", master));
        Assert.False(Passes("#EXTM3U\n#EXTINF:6,\u2028https://elsewhere.example/s.ts\n", media));
        Assert.False(Passes("#EXTM3U\n#EXTINF:6,\rhttps://elsewhere.example/s.ts\n", media));
        Assert.False(Passes("#EXTM3U\n#EXTINF:6,\vs.ts\n", media));
        Assert.False(Passes("#EXTM3U\n#EXTINF:6,\u0085s.ts\n"));
        Assert.True(Passes("#EXTM3U\r\n#EXT-X-STREAM-INF:BANDWIDTH=1,\tRESOLUTION=1x1\r\nv0.m3u8\r\n", master));
    }

    // Bytes that aren't UTF-8, in a tag line kept, are kept as they came.
    [Fact]
    public void FilterPlaylistKeepsBytes()
    {
        byte[] body = [.. "#EXTM3U\n# x"u8, 0xff, 0xc3, .. "\n#EXT-X-STREAM-INF:BANDWIDTH=1\ns.ts\n"u8];
        Assert.Equal(body, Media.FilterPlaylist(body, null, null));
    }

    // What the filter keeps is kept whole when checked again (FuzzFilterPlaylist's seeds).
    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv0.m3u8\n", true)]
    [InlineData("#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-TARGETDURATION:6\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6,\nseg0.m4s\r\n#EXT-X-ENDLIST\n", false)]
    [InlineData("#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"a1\",URI=\"a1.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv0.m3u8\n", true)]
    public void FilterPlaylistSettles(string body, bool master)
    {
        var subset = (master ? Media.MasterSubset : Media.MediaSubset).ToList();
        static bool Audio(string id) => id == "a1";
        var out_ = Media.FilterPlaylist(Encoding.UTF8.GetBytes(body), subset, Audio);
        Assert.Equal(out_, Media.FilterPlaylist(out_, subset, Audio));
    }
}
