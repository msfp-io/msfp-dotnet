using System.Text;

namespace Msfp.Protocol.Tests;

/// <summary>wire/discovery_test.go: base paths (strict.json, bases) and the document.</summary>
public class DiscoveryTests
{
    private static bool BaseOk(string s)
    {
        try
        {
            Discovery.CheckBase(s);
            return true;
        }
        catch (ProtocolException e) when (e.Error == ProtocolError.Base)
        {
            return false;
        }
    }

    // Base paths: absolute, plain segments, ending in /api/v1/federation/,
    // 256 bytes at most.
    [Fact]
    public void BaseVectors()
    {
        var cases = TestVectors.Cases("strict.json", "bases");
        var failures = cases.Where(c => BaseOk(c.Str("text")) != c.Valid())
            .Select(c => $"\"{c.Str("text")}\" ({c.Str("reason")}): want valid {c.Valid()}").ToList();
        TestVectors.NoFailures(failures, cases.Count, "strict.json bases");
    }

    [Fact]
    public void ReadDiscovery()
    {
        var d = Discovery.Read("""{"core":[1],"parts":{"catalog":[1],"video":[1]},"base":"/srv/media/api/v1/federation/"}"""u8);
        Assert.Equal("/srv/media/api/v1/federation/", d.Base);
        Assert.Equal("/srv/media", Discovery.Prefix(d.Base));
        Assert.Equal("[1]", d.Core!.ToJsonString());
        Assert.Equal("""{"catalog":[1],"video":[1]}""", d.Parts!.ToJsonString());

        d = Discovery.Read("{}"u8);
        Assert.Equal(Discovery.DefaultBase, d.Base);
        Assert.Equal("", Discovery.Prefix(d.Base));
        Assert.Null(d.Core);

        string[] bad =
        [
            "", "[]", "\"x\"", """{"base":5}""", """{"base":"/x/"}""", """{"base":null}""", """{"core":"1"}""",
            """{"parts":[1]}""", """{"parts":{"video":1}}""", """{"base":"/srv/../api/v1/federation/"}""", """{"base":"/a"} x""",
        ];
        foreach (var body in bad)
        {
            var e = Assert.Throws<ProtocolException>(() => Discovery.Read(Encoding.UTF8.GetBytes(body)));
            Assert.True(e.Error is ProtocolError.Json or ProtocolError.Base, body);
        }

        // A part it doesn't know is ignored, whatever it holds.
        Discovery.Read("""{"parts":{"later":{"x":1}}}"""u8);
    }
}
