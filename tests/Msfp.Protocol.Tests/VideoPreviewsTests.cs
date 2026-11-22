using System.Text;

namespace Msfp.Protocol.Tests;

/// <summary>The previews call (video section 5.6), beyond video.json's cases.</summary>
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
    public void Answer()
    {
        var p = Video.ReadPreviewsAnswer("""{"ticket":"T1","etag":"e1","trickplay":{"columns":10,"x":null}}"""u8);
        Assert.Equal(new PreviewsAnswer("T1", "e1"), p);
        Assert.Equal("""{"ticket":"T1","etag":"e1"}""", Encoding.UTF8.GetString(Video.WritePreviewsAnswer(p)));
        Assert.Equal(Root + "T1/trickplay/2", MediaUrls.Sheet(Root, p.Ticket, 2));
        foreach (var s in new[] { """{"ticket":"T1"}""", """{"etag":"e1"}""", """{"ticket":"T.1","etag":"e1"}""", "null", "[]" })
        {
            Assert.Throws<ProtocolException>(() => Video.ReadPreviewsAnswer(Encoding.UTF8.GetBytes(s)));
        }
    }
}
