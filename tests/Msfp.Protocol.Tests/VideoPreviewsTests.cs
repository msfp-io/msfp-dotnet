using System.Text;

namespace Msfp.Protocol.Tests;

/// <summary>The previews call (video sections 5.5 and 5.6), beyond video.json's cases.</summary>
public class VideoPreviewsTests
{
    private const string Root = "https://a.example.org/api/v1/stream/";

    [Fact]
    public void Request()
    {
        var r = Video.ReadPreviewsRequest("""{"title":"movie/tmdb/348/x/Tr7aQ1","versionId":"v1","x":null}"""u8);
        Assert.Equal(new PreviewsRequest("movie/tmdb/348/x/Tr7aQ1", "v1"), r);
        foreach (var s in new[]
                 {
                     """{"title":"series/tmdb/1399","versionId":"v1"}""", """{"title":null,"versionId":"v1"}""",
                     """{"title":"movie/tmdb/1","versionId":"v.1"}""", """{"title":"movie/tmdb/1"}""", "null",
                 })
        {
            Assert.Throws<ProtocolException>(() => Video.ReadPreviewsRequest(Encoding.UTF8.GetBytes(s)));
        }
    }

    [Fact]
    public void Trickplay()
    {
        var p = Video.ReadPreviewsAnswer(Encoding.UTF8.GetBytes(
            """{"trickplay":{"tileWidth":320,"tileHeight":180,"columns":10,"rows":10,"intervalMs":10000,"tileCount":150,"sheets":["""
            + $"\"{Root}T1/trickplay/0\",\"{Root}T1/trickplay/1\"]}}}}"), Root);
        Assert.NotNull(p);
        Assert.Equal("T1", p.Ticket);
        Assert.Equal(2, p.Trickplay.Sheets);

        // Dropped: too few sheets; a number too big for any bound.
        Assert.Null(Video.ReadPreviewsAnswer(Encoding.UTF8.GetBytes(
            $$$"""{"trickplay":{"tileWidth":320,"tileHeight":180,"columns":10,"rows":10,"intervalMs":10000,"tileCount":150,"sheets":["{{{Root}}}T1/trickplay/0"]}}"""), Root));
        Assert.Null(Video.ReadPreviewsAnswer(
            """{"trickplay":{"tileWidth":9007199254740991,"tileHeight":180,"columns":1,"rows":1,"intervalMs":10000,"tileCount":1}}"""u8, Root));
        Assert.Null(Video.ReadPreviewsAnswer("{}"u8, Root));

        // Refused: a null number, another ticket, null at the top.
        Assert.Throws<ProtocolException>(() => Video.ReadPreviewsAnswer("""{"trickplay":{"tileWidth":null}}"""u8, Root));
        Assert.Throws<ProtocolException>(() => Video.ReadPreviewsAnswer("""{"trickplay":{},"x":null}"""u8, Root));
        Assert.Throws<ProtocolException>(() => Video.ReadPreviewsAnswer(Encoding.UTF8.GetBytes(
            $$$"""{"trickplay":{"sheets":["{{{Root}}}T1/trickplay/0","{{{Root}}}T2/trickplay/1"]}}"""), Root));
    }
}
