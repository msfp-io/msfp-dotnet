using System.Text;
using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>
/// wire/catalog_test.go and names_test.go: strict.json's titlePages,
/// opaqueKeyParts and localImages, a local title's description, the one
/// language-tag rule, and the names lists.
/// </summary>
public class CatalogTests
{
    private const int CoreAnswer = 4 << 20; // the core's bound of an answer (core section 3)

    [Fact]
    public void PageVectors()
    {
        var cases = TestVectors.Cases("strict.json", "titlePages");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var p = new CatalogPage
            {
                Changes = c["changes"]?.GetValue<bool>() ?? false,
                Titles = c["titles"]?.GetValue<int>() ?? 0,
                Gone = c["gone"]?.GetValue<int>() ?? 0,
                Bytes = c["bytes"]?.GetValue<int>() ?? 0,
                Remain = c["remain"]?.GetValue<bool>() ?? false,
                After = c.Str("after"),
                LastKey = c.Str("lastKey"),
                Next = c["next"]?.GetValue<string>(),
            };
            var reason = CatalogRules.CheckPage(p);
            if ((reason is null) != c.Valid() || (reason is not null && reason != c.Str("reason")))
            {
                failures.Add($"{c.Str("name")}: CheckPage = {reason ?? "valid"}; want valid {c.Valid()} ({c.Str("reason")})");
            }

            var caller = p.Bytes > CoreAnswer ? "fail" : CatalogRules.AskOn(p.After, p.Next) ? "ask on" : "stop";
            if (caller != c.Str("caller"))
            {
                failures.Add($"{c.Str("name")}: the caller would {caller}; want {c.Str("caller")}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "strict.json titlePages");
    }

    [Fact]
    public void PageBudgetBounds()
    {
        var b = new PageBudget(CatalogRules.PageReserve);
        while (b.TryAdd(10))
        {
        }

        Assert.Equal(CatalogRules.MaxPageTitles, b.Keys);

        // Ended by size: entries of 1 MiB fit twice beside the reserve.
        b = new PageBudget(CatalogRules.PageReserve);
        var n = 0;
        while (b.TryAdd(1 << 20))
        {
            n++;
        }

        Assert.Equal(2, n);

        // Exactly 3 MiB in all, commas included, fits; one more byte doesn't.
        b = new PageBudget(CatalogRules.MaxPageBytes - 21);
        Assert.True(b.TryAdd(10));
        Assert.True(b.TryAdd(10));
        Assert.False(b.TryAdd(1));

        // An entry that alone passes the bound: false on an empty page.
        b = new PageBudget(CatalogRules.PageReserve);
        Assert.False(b.TryAdd(CatalogRules.MaxPageBytes));
        Assert.Equal(0, b.Keys);
    }

    [Fact]
    public void OpaqueKeyPartVectors()
    {
        var cases = TestVectors.Cases("strict.json", "opaqueKeyParts");
        var failures = cases.Where(c => CatalogRules.OpaqueKeyPart(c.Str("text")) != c.Valid())
            .Select(c => $"\"{c.Str("text")}\" ({c.Str("reason")}): want valid {c.Valid()}").ToList();
        TestVectors.NoFailures(failures, cases.Count, "strict.json opaqueKeyParts");
    }

    [Fact]
    public void LocalImageVectors()
    {
        var cases = TestVectors.Cases("strict.json", "localImages");
        var failures = new List<string>();
        var sides = new Dictionary<string, int>();
        foreach (var c in cases)
        {
            var name = c.Str("name");
            var side = c.Str("side");
            sides[side] = sides.GetValueOrDefault(side) + 1;
            var body = Encoding.UTF8.GetBytes(c.Str("body"));
            switch (side)
            {
                case "request":
                    var parts = (c["parts"]?.AsArray() ?? []).Select(p => p!.GetValue<string>()).ToList();
                    var offered = (c["offered"]?.AsArray() ?? []).Select(o => o!.AsObject())
                        .ToDictionary(o => o.Str("key"), o => o["image"]!.GetValue<bool>());
                    var (title, status, problem) = CatalogRules.CheckImageRequest(
                        parts.Contains, body, key => offered.GetValueOrDefault(key));
                    var wantStatus = c["status"]!.GetValue<int>();
                    if (status != wantStatus || problem != c.Str("type"))
                    {
                        failures.Add($"{name}: {status} \"{problem}\"; want {wantStatus} \"{c.Str("type")}\"");
                    }

                    if (status == 200 && !c.Str("body").Contains($"\"{title}\"", StringComparison.Ordinal))
                    {
                        failures.Add($"{name}: title \"{title}\"");
                    }

                    break;
                case "answer":
                    var root = c.Str("streamRoot");
                    var ok = CatalogRules.TryReadImageAnswer(body, root, out var ticket);
                    if (ok != c.Valid() || ticket != c.Str("ticket"))
                    {
                        failures.Add($"{name}: \"{ticket}\", {ok}; want \"{c.Str("ticket")}\", {c.Valid()} ({c.Str("reason")})");
                    }

                    if (ok && CatalogRules.ImageUrl(root, ticket) != root + ticket + "/image")
                    {
                        failures.Add($"{name}: ImageUrl");
                    }

                    break;
                case "image":
                    var valid = CatalogRules.TryCheckImage(c.Hex(), c["size"]?.GetValue<long>() ?? 0, out var format, out var reason);
                    if (valid != c.Valid() || format != c.Str("format") || reason != c.Str("reason"))
                    {
                        failures.Add($"{name}: \"{format}\", {valid} ({reason}); want \"{c.Str("format")}\", {c.Valid()} ({c.Str("reason")})");
                    }

                    break;
                default:
                    failures.Add($"{name}: unknown side \"{side}\"");
                    break;
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "strict.json localImages");
        Assert.True(sides.ContainsKey("request") && sides.ContainsKey("answer") && sides.ContainsKey("image"));

        // On a whole image: the head is all of it.
        Assert.True(CatalogRules.TryCheckImage(Convert.FromHexString("89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c489"),
            out var f, out _));
        Assert.Equal(CatalogRules.ImagePng, f);
    }

    public static TheoryData<string, bool, string, int, bool> DescriptionCases() => new()
    {
        { """{"name":" Home\u0000  movie ","year":1999,"image":true}""", true, "Home movie", 1999, true },
        { """{"name":"x","year":1799,"image":"yes"}""", true, "x", 0, false },
        { """{"name":"x","year":2000.5}""", true, "x", 0, false },
        { """{"name":"x","year":10000,"image":false}""", true, "x", 0, false },
        { "{\"name\":\"" + new string('é', 130) + "\"}", true, new string('é', 128), 0, false },
        { """{"name":"  "}""", false, "", 0, false },
        { """{"name":5}""", false, "", 0, false },
        { """{"name":null}""", false, "", 0, false },
        { "{}", false, "", 0, false },
        { "[]", false, "", 0, false },
    };

    [Theory]
    [MemberData(nameof(DescriptionCases))]
    public void Description(string json, bool ok, string name, int year, bool image)
    {
        var d = CatalogRules.ReadDescription(Encoding.UTF8.GetBytes(json), DescriptionBounds.Local);
        Assert.Equal(ok ? new LocalDescription(name, year, image) : null, d);
        Assert.Equal(d, CatalogRules.ReadDescription(JsonNode.Parse(json), DescriptionBounds.Local));
    }

    [Fact]
    public void DescriptionOwnBounds()
    {
        var b = new DescriptionBounds(false, 256, 1000, 9999);
        Assert.Equal(new LocalDescription("", 1000, false), CatalogRules.ReadDescription("""{"year":1000}"""u8, b));
    }

    [Fact]
    public void ValidLanguageTag()
    {
        foreach (var s in new[] { "fr", "fr-CA", "yue", "zh-Hant-TW", "es-419", "tlh" })
        {
            Assert.True(Languages.ValidTag(s), s);
        }

        foreach (var s in new[] { "fre", "fr-ca", "FR", "e", "und", "fr_CA", "", "fr--CA", "zh-hant-tw", "toolongtagvalue", null })
        {
            Assert.False(Languages.ValidTag(s), s);
        }

        // The rule is TrySentTag's: every tag it sends is valid, and no other.
        var cases = TestVectors.Cases("strict.json", "languageCodes");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            if (c.Valid() && !Languages.ValidTag(c.Str("output")))
            {
                failures.Add($"ValidTag(\"{c.Str("output")}\") = false, the tag sent for \"{c.Str("text")}\"");
            }

            if (Languages.ValidTag(c.Str("text")) != (c.Valid() && c.Str("text") == c.Str("output")))
            {
                failures.Add($"ValidTag(\"{c.Str("text")}\") = {Languages.ValidTag(c.Str("text"))}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "strict.json languageCodes as valid tags");
    }

    [Fact]
    public void NamesLists()
    {
        var lists = new Dictionary<string, IReadOnlyList<string>>
        {
            ["Containers"] = Names.Containers, ["VideoCodecs"] = Names.VideoCodecs, ["AudioCodecs"] = Names.AudioCodecs,
            ["HdrFormats"] = Names.HdrFormats, ["AudioProfiles"] = Names.AudioProfiles, ["Editions"] = Names.Editions,
            ["ExtraTypes"] = Names.ExtraTypes, ["SubtitleFormats"] = Names.SubtitleFormats,
        };
        foreach (var (name, list) in lists)
        {
            Assert.All(list, s => Assert.True(s.Length > 0 && s == s.ToLowerInvariant(), $"{name}: \"{s}\""));
            Assert.Equal(list.Count, list.Distinct(StringComparer.Ordinal).Count());
        }

        Assert.Equal(18, Names.Containers.Count);
        Assert.Equal(11, Names.VideoCodecs.Count);
        Assert.Equal(15, Names.AudioCodecs.Count);
        foreach (var list in new[] { Names.Containers, Names.VideoCodecs, Names.AudioCodecs, Names.ExtraTypes })
        {
            Assert.DoesNotContain(Names.Other, list);
        }

        Assert.Equal(["hdr10", "hdr10plus", "hlg", "dv"], Names.HdrFormats);
        Assert.Equal(Names.DvProfiles.Order(), Names.DvProfiles);

        Assert.True(Names.IsContainer("m2ts"));
        Assert.False(Names.IsContainer("matroska"));
        Assert.False(Names.IsContainer("MKV"));
        Assert.True(Names.IsVideoCodec("theora"));
        Assert.False(Names.IsVideoCodec("aac"));
        Assert.True(Names.IsAudioCodec("pcm_bluray"));
        Assert.False(Names.IsAudioCodec("h264"));

        Assert.Equal("mkv", Names.NameOrOther(Names.Containers, "mkv"));
        Assert.Equal(Names.Other, Names.NameOrOther(Names.Containers, "matroska"));
        Assert.Equal(Names.Other, Names.NameOrOther(Names.Containers, ""));
        Assert.Equal(Names.Other, Names.NameOrOther(Names.Containers, null));
        Assert.Equal("truehd", Names.NameOrOther(Names.AudioCodecs, "truehd"));
        Assert.Equal(Names.Other, Names.NameOrOther(Names.AudioCodecs, "TrueHD"));
        Assert.Equal("pgs", Names.NameOrOther(Names.SubtitleFormats, "pgs"));
        Assert.Equal(Names.Other, Names.NameOrOther(Names.SubtitleFormats, "hdmv_pgs_subtitle"));
        Assert.Equal("imax", Names.NameOrOther(Names.Editions, "imax"));
        Assert.Equal(Names.Other, Names.NameOrOther(Names.Editions, "fan"));
    }
}
