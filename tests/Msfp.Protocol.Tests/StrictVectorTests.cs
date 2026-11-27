using System.Net;
using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>
/// wire/vectors_test.go's cases of strict.json in this part: media names
/// (TestStrictVectors), language codes (TestLanguageCodeVectors),
/// destinations (MSFP core section 3.3), open tokens (MSFP Media base 3) and
/// libraries' kinds (MSFP Media listing 2.2).
/// </summary>
public class StrictVectorTests
{
    [Fact]
    public void OpenTokenVectors()
    {
        var cases = TestVectors.Cases("strict.json", "openTokens");
        var failures = cases.Where(c => Names.IsToken(c.Str("text")) != c.Valid())
            .Select(c => $"\"{c.Str("text")}\": want valid {c.Valid()} ({c.Str("reason")})").ToList();
        TestVectors.NoFailures(failures, cases.Count, "strict.json openTokens");
    }

    [Fact]
    public void OpenMediaTypeVectors()
    {
        var cases = TestVectors.Cases("strict.json", "openMediaTypes");
        var failures = cases.Where(c => Names.IsMediaType(c.Str("text")) != c.Valid())
            .Select(c => $"\"{c.Str("text")}\": want valid {c.Valid()}").ToList();
        TestVectors.NoFailures(failures, cases.Count, "strict.json openMediaTypes");
    }

    [Fact]
    public void MediaNameVectors()
    {
        var cases = TestVectors.Cases("strict.json", "mediaNames");
        var failures = cases.Where(c => Media.FileName(c.Str("text")) != c.Valid())
            .Select(c => $"\"{c.Str("text")}\": want valid {c.Valid()}").ToList();
        TestVectors.NoFailures(failures, cases.Count, "strict.json mediaNames");
    }

    [Fact]
    public void LanguageCodeVectors()
    {
        var cases = TestVectors.Cases("strict.json", "languageCodes");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var ok = Languages.TrySentTag(c.Str("text"), out var got);
            if (ok != c.Valid() || got != c.Str("output"))
            {
                failures.Add($"TrySentTag(\"{c.Str("text")}\") = \"{got}\", {ok}; want \"{c.Str("output")}\", {c.Valid()}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "strict.json languageCodes");
        Assert.Equal(["fr", "de", "fr-CA"], Languages.SentTags(["fre", "fr", "FRA", "und", "ger", "fr-ca", "fre-CA"]));
    }

    [Fact]
    public void DestinationVectors()
    {
        var cases = TestVectors.Cases("strict.json", "destinations");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var valid = IPAddress.TryParse(c.Str("text"), out var ip) && !Destinations.Refused(ip, false);
            if (valid != c.Valid())
            {
                failures.Add($"{c.Str("text")} ({c.Str("reason")}): want valid {c.Valid()}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "strict.json destinations");
    }

    [Fact]
    public void LoopbackAllowed()
    {
        Assert.False(Destinations.Refused(IPAddress.Loopback, true));
        Assert.False(Destinations.Refused(IPAddress.Parse("::ffff:127.0.0.1"), true));
        Assert.True(Destinations.Refused(IPAddress.Parse("64:ff9b:1::1"), true));
        Assert.True(Destinations.Refused(IPAddress.Parse("168.63.129.16"), true));
        Assert.True(Destinations.Refused(IPAddress.Parse("fd00:ec2::254"), false));
    }
}
