using System.Text;
using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>wire/music_test.go: the cases of music.json.</summary>
public class MusicTests
{
    [Fact]
    public void MusicKeyVectors()
    {
        var cases = TestVectors.Cases("music.json", "keys");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var ok = MusicKey.TryParse(c.Str("text"), out var k);
            if (ok != c.Valid() || (ok && (k.Kind != c.Str("kind") || k.ToString() != c.Str("text"))))
            {
                failures.Add($"{c.Str("name")}: {k} {ok}, want {c.Valid()} {c.Str("kind")}");
            }

            if (TitleKey.TryParse(c.Str("text"), out var tk) && c.Valid() && (tk.Part != "music" || tk.Playable != (c.Str("kind") == "track")))
            {
                failures.Add($"{c.Str("name")}: TitleKey {tk}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "music.json keys");
    }

    [Fact]
    public void MusicTitleVectors()
    {
        var libraries = TestVectors.Load("music.json")["libraries"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var cases = TestVectors.Cases("music.json", "titles");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var got = Music.ReadTitle(Encoding.UTF8.GetBytes(c.Str("text")));
            if (got is not null && !libraries.Contains(got.Library))
            {
                got = null; // the caller's check: a library it asked for
            }

            if ((got is not null) != c.Valid())
            {
                failures.Add($"{c.Str("name")}: kept {got is not null}, want {c.Valid()}");
                continue;
            }

            if (got is null)
            {
                continue;
            }

            var have = JsonNode.Parse(Write(got));
            if (!JsonNode.DeepEquals(have, JsonNode.Parse(c.Str("output"))))
            {
                failures.Add($"{c.Str("name")}:\n    got  {have!.ToJsonString()}\n    want {c.Str("output")}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "music.json titles");
    }

    // A kept title as the vectors write it.
    private static byte[] Write(MusicTitle m) => JsonOut.Object(o => o
        .String("key", m.Key).String("kind", m.Kind).String("library", m.Library).String("name", m.Name)
        .Strings("artists", m.Artists.Count > 0 ? m.Artists : null)
        .String("artistNames", m.ArtistNames.Length > 0 ? m.ArtistNames : null)
        .String("album", m.Album.Length > 0 ? m.Album : null)
        .Integer("disc", m.Disc != 0 ? m.Disc : null)
        .Integer("number", m.Number != 0 ? m.Number : null)
        .Integer("year", m.Year != 0 ? m.Year : null)
        .Integer("durationMs", m.DurationMs != 0 ? m.DurationMs : null)
        .Object("ids", m.Ids.Count > 0 ? i => { foreach (var (k, v) in m.Ids) { i.String(k, v); } } : null));

    [Fact]
    public void CleanText()
    {
        Assert.Equal("a b c", Music.CleanText("  a \t\n b\u2028c\u200b  ", 100));
        Assert.Equal("ab", Music.CleanText("ab c", 3));
        Assert.Equal("\U0001F600\U0001F600", Music.CleanText("\U0001F600\U0001F600\U0001F600", 2));
    }
}
