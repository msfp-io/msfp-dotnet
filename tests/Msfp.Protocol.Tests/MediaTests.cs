using System.Text;

namespace Msfp.Protocol.Tests;

/// <summary>wire/media_test.go: playlists, and the hls cases of media.json.</summary>
public class MediaTests
{
    private static bool Passes(string body, IReadOnlyCollection<string>? subset = null)
    {
        try
        {
            Media.FilterPlaylist(Encoding.UTF8.GetBytes(body), subset);
            return true;
        }
        catch (ProtocolException e) when (e.Error == ProtocolError.Playlist)
        {
            return false;
        }
    }

    [Fact]
    public void HlsVectors()
    {
        var cases = TestVectors.Cases("media.json", "hls");
        Assert.Equal(25, cases.Count);
        var failures = new List<string>();
        foreach (var c in cases)
        {
            if (Passes(c.Str("text")) != c.Valid())
            {
                failures.Add($"{c.Str("name")}: want valid {c.Valid()}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "media.json hls");
    }

    // A paired server's playlist passes only when it names files of the
    // same stream, by name: no path, no other server.
    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv0.m3u8\n", true)]
    [InlineData("#EXTM3U\n#EXT-X-MAP:URI=\"init0.mp4\"\n#EXTINF:6,\ns0.mp4\r\n", true)]
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
    [InlineData("#EXTM3U\n...\n", true)]
    public void CheckPlaylist(string body, bool ok) => Assert.Equal(ok, Passes(body));

    // A relayed playlist keeps the subset's tags only: comments and other
    // tags are dropped, URIs checked in them all the same.
    [Fact]
    public void FilterPlaylistKeepsTheSubset()
    {
        var got = Media.FilterPlaylist("#EXTM3U\r\n# a comment\n#EXT-X-FOO:BAR=1\n#EXT-X-TARGETDURATION:6\n\n#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST"u8, Media.MediaSubset.ToList());
        Assert.Equal("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg0.ts\n#EXT-X-ENDLIST\n", Encoding.UTF8.GetString(got));
        Assert.False(Passes("#EXTM3U\n#EXT-X-FOO:URI=\"a/b\"\n", Media.MediaSubset.ToList()), "a dropped tag's URI still counts");
        var many = new StringBuilder("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv0.m3u8\n");
        for (var i = 0; i < Media.MaxPlaylistUris; i++)
        {
            many.Append("s.ts\n");
        }

        Assert.False(Passes(many.ToString()), "too many URIs");

        // Offsets stay right past letters whose upper case is longer.
        Assert.False(Passes("#EXTM3U\n#EXTINF:6,ßßßß uri=\"a/b\"\nseg0.ts\n"), "a URI after non-ASCII text");
        Assert.Throws<ProtocolException>(() => Media.CheckPlaylist(new byte[Media.MaxPlaylist + 1]));
    }

    // What Overfin as the owning server sent of Jellyfin 12.2's HLS: within
    // the subset, kept whole.
    [Fact]
    public void FilterPlaylistOfJellyfin()
    {
        var master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5198950,AVERAGE-BANDWIDTH=5198950,VIDEO-RANGE=SDR,CODECS=\"avc1.64001F,mp4a.40.2\",RESOLUTION=1280x720,FRAME-RATE=24\nv0.m3u8\n";
        var media = "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:11\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-MAP:URI=\"init.mp4\"\n" +
            "#EXTINF:10.417000,\nseg0.mp4\n#EXTINF:10.416000,\nseg1.mp4\n#EXT-X-ENDLIST\n";
        Assert.Equal(master, Encoding.UTF8.GetString(Media.FilterPlaylist(Encoding.UTF8.GetBytes(master), Media.MasterSubset.ToList())));
        Assert.Equal(media, Encoding.UTF8.GetString(Media.FilterPlaylist(Encoding.UTF8.GetBytes(media), Media.MediaSubset.ToList())));
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
        byte[] body = [.. "#EXTM3U\n# x"u8, 0xff, 0xc3, .. "\ns.ts\n"u8];
        Assert.Equal(body, Media.FilterPlaylist(body, null));
    }
}
