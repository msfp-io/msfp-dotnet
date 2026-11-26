namespace Msfp.Tests;

// msfp-go's TestGenerousLimits: a peer linked by hand and through groups
// gets the most generous of its relations' limits (playback section 5,
// groups section 9).
public sealed class GenerousTests
{
    private static readonly Limits Hand = new() { Streams = 2, Transcodes = 1, BitrateKbps = 8000, Downloads = 0, DownloadsPerDay = 9, ScansPerDay = 1, ScanBytes = 100 };
    private static readonly Limits Family = new() { Streams = 3, Transcodes = 3, BitrateKbps = 0, Downloads = 2, DownloadsPerDay = 5, DownloadKbps = 4000, Audio = true, AudioStreams = 2 };
    private static readonly Limits Friends = new() { Streams = 4, Transcodes = 0, BitrateKbps = 20000, Downloads = 1, DownloadsPerDay = 10, DownloadKbps = 0, ScanBytes = 500, AudioTranscodes = 1 };

    private static PeerRecord Peer(bool byHand, Limits? hand, params (string Group, Limits Limits)[] pools) => new()
    {
        Id = "p1",
        Key = new byte[32],
        NotByHand = !byHand,
        Limits = hand ?? new Limits(),
        Pools = [.. pools.Select(p => new GroupPool { Group = p.Group, Limits = p.Limits })],
    };

    [Fact]
    public void OneRelationKeepsItsOwn()
    {
        Assert.Equal(Hand, MsfpEngine.Generous(Peer(true, Hand)));
        Assert.Equal(Family, MsfpEngine.Generous(Peer(false, null, ("g1", Family))));
        Assert.Equal(new Limits(), MsfpEngine.Generous(Peer(false, null)));
    }

    [Fact]
    public void TheMostGenerous()
    {
        var want = new Limits
        {
            Streams = 4, Transcodes = 3, BitrateKbps = 0, Downloads = 2, DownloadsPerDay = 10, DownloadKbps = 0,
            ScansPerDay = 1, ScanBytes = 500, Audio = true, AudioStreams = 2, AudioTranscodes = 1,
        };
        Assert.Equal(want, MsfpEngine.Generous(Peer(true, Hand, ("g1", Family), ("g2", Friends))));
        Assert.Equal(
            MsfpEngine.Generous(Peer(false, null, ("g1", Family), ("g2", Friends))),
            MsfpEngine.Generous(Peer(false, null, ("g2", Friends), ("g1", Family))));
        var got = MsfpEngine.Generous(Peer(false, null, ("a", new Limits { Downloads = 0, DownloadsPerDay = 1 }), ("b", new Limits { Downloads = 3, DownloadsPerDay = 0 })));
        Assert.Equal((3, 0), (got.Downloads, got.DownloadsPerDay));
    }
}
