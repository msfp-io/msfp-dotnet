using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

// MSFP Media's additions to playing and listing (msfp-go's
// wire/media_language_test.go): progressSeconds, paused, replaces, the
// capability list as MSFP refuses it and as a player's carrier reads it,
// target-refused, the language's refusals and instead, the titles request
// and its filters, msfpMedia in discovery.
public sealed class MediaLanguageTests
{
    [Theory]
    [InlineData(0, 60)]
    [InlineData(44, 60)]
    [InlineData(45, 45)]
    [InlineData(90, 90)]
    [InlineData(120, 120)]
    [InlineData(121, 60)]
    public void ProgressInterval(int n, int want)
    {
        Assert.Equal(TimeSpan.FromSeconds(want), Playback.ProgressInterval(n));
        Assert.Equal(TimeSpan.FromSeconds(2 * want), Playback.IdleLimit(n));
    }

    [Fact]
    public void ProgressSecondsInAnswer()
    {
        Assert.Equal(Playback.IdleLimit(Playback.DefaultProgressSeconds), Playback.IdleEnd);
        const string Base = "{\"id\":\"play_1\",\"ticket\":\"t1\",\"protocol\":\"hls\",\"method\":\"transcode\",\"etag\":\"e1\",\"positionMs\":0,\"versionId\":\"v1\"";
        foreach (var (tail, want) in new[]
        {
            ("}", 60), (""","progressSeconds":45}""", 45), (""","progressSeconds":120}""", 120), (""","progressSeconds":121}""", 60),
            (""","progressSeconds":44}""", 60), (""","progressSeconds":"90"}""", 60), (""","progressSeconds":90.5}""", 60),
            (""","progressSeconds":null}""", 60),
        })
        {
            var a = Playback.ReadPlayAnswer(Encoding.UTF8.GetBytes(Base + tail), new PlayAsked());
            Assert.True(a is not null, tail);
            Assert.Equal(want, a.ProgressSeconds);
        }

        // Null elsewhere at the top still refuses the answer.
        Assert.Null(Playback.ReadPlayAnswer(Encoding.UTF8.GetBytes(Base + ""","audioTrack":null}"""), new PlayAsked()));
    }

    [Fact]
    public void ReportPaused()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.True(Playback.ReadReport(Playback.CallProgress, """{"id":"play_1","paused":true}"""u8, now).Paused);
        foreach (var body in new[] { """{"id":"play_1","paused":1}""", """{"id":"play_1","paused":"true"}""", """{"id":"play_1","paused":null}""" })
        {
            Assert.Equal(400, Assert.Throws<PlayRefusal>(() => Playback.ReadReport(Playback.CallProgress, Encoding.UTF8.GetBytes(body), now)).Status);
        }

        // stop knows no paused: an unknown member, ignored.
        Assert.False(Playback.ReadReport(Playback.CallStop, """{"id":"play_1","paused":1}"""u8, now).Paused);
    }

    [Fact]
    public void ReplacesAndCapabilities()
    {
        var r = Playback.ReadPlay("""{"title":"movie/tmdb/949","replaces":"play_abc-_1"}"""u8);
        Assert.Equal("play_abc-_1", r.Replaces);
        Assert.Contains("\"replaces\":\"play_abc-_1\"", Encoding.UTF8.GetString(r.ToJson()), StringComparison.Ordinal);
        foreach (var body in new[]
        {
            """{"title":"movie/tmdb/949","replaces":""}""", """{"title":"movie/tmdb/949","replaces":"a b"}""",
            """{"title":"movie/tmdb/949","replaces":7}""", """{"title":"movie/tmdb/949","replaces":null}""",
            // MSFP refuses a capability list, whatever it holds.
            """{"title":"movie/tmdb/949","capabilities":{}}""", """{"title":"movie/tmdb/949","capabilities":"x"}""",
        })
        {
            Assert.Equal(400, Assert.Throws<PlayRefusal>(() => Playback.ReadPlay(Encoding.UTF8.GetBytes(body))).Status);
        }

        var player = new PlayReader { Purposes = [Playback.PurposePlay, Playback.PurposeDownload], Capabilities = true };
        const string Good = """
            {"title":"movie/tmdb/949","capabilities":{"files":["video/mp4","video/matroska"],"segments":["video/mp4"],
            "video":[{"codec":"hevc","codecs":["hvc1.2.4.L153.B0"],"maxWidth":3840,"maxHeight":2160,"maxBitDepth":10,
            "transfers":[1,14,15,16,18],"hdr":["hdr10plus"],"dv":[5,8],"maxFps":"60/1","threeD":["sbs"]},
            {"codec":"h264","codecs":["avc1.640033"],"maxRefFrames":16,"interlaced":true},{"codec":"newcodec"}],
            "audio":[{"codec":"aac","codecs":["mp4a.40.2"],"maxChannels":6},{"codec":"eac3","objects":["dolby-atmos"],
            "maxSampleRate":48000,"maxBitDepth":24}],"subtitles":["subrip","hdmv_pgs_subtitle"],"future":1},"maxBitrateKbps":40000}
            """;
        r = player.Read(Encoding.UTF8.GetBytes(Good));
        var c = r.Capabilities!;
        Assert.Equal(3, c.Video.Count);
        Assert.Equal(10, c.Video[0].MaxBitDepth);
        Assert.Equal(new[] { 5, 8 }, c.Video[0].Dv);
        Assert.Equal("60/1", c.Video[0].MaxFps);
        Assert.True(c.Video[1].Interlaced);
        Assert.Equal(48000, c.Audio[1].MaxSampleRate);
        Assert.Equal(40000, r.MaxBitrateKbps);
        foreach (var body in new[]
        {
            """{"title":"movie/tmdb/949","purpose":"scan","delivery":"file"}""", // a purpose this carrier doesn't take
            """{"title":"movie/tmdb/949","delivery":"file","capabilities":{}}""",
            """{"title":"movie/tmdb/949","purpose":"download","capabilities":{}}""",
            """{"title":"movie/tmdb/949","capabilities":[]}""",
            """{"title":"movie/tmdb/949","capabilities":{"files":"video/mp4"}}""",
            """{"title":"movie/tmdb/949","capabilities":{"files":["Video/MP4"]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"segments":["video/mp4","video/mp2t","audio/mp4","audio/mp2t","x/y"]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"video":[{"codecs":["avc1.640033"]}]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"video":[{"codec":"h264"}]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"video":[{"codec":"hevc","codecs":["hvc1 2"]}]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"video":[{"codec":"vp8","maxWidth":0}]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"video":[{"codec":"vp8","maxFps":"60"}]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"video":[{"codec":"vp8","maxRefFrames":4}]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"video":[{"codec":"vp8","dv":[128]}]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"video":[{"codec":"vp8","interlaced":1}]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"audio":[{"codec":"aac"}]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"audio":[{"codec":"opus","maxChannels":65}]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"audio":[{"codec":"opus","maxSampleRate":7999}]}}""",
            """{"title":"movie/tmdb/949","capabilities":{"subtitles":["SubRip"]}}""",
        })
        {
            Assert.True(Assert.Throws<PlayRefusal>(() => player.Read(Encoding.UTF8.GetBytes(body))).Status == 400, body);
        }

        // With a capability list, the answer's method is the source's pick:
        // file with direct, HLS otherwise.
        const string Answer = """{"id":"play_1","ticket":"t1","etag":"e1","positionMs":0,"versionId":"v1",""";
        foreach (var (tail, ok) in new[]
        {
            ("\"protocol\":\"file\",\"method\":\"direct\"}", true), ("\"protocol\":\"hls\",\"method\":\"remux\"}", true),
            ("\"protocol\":\"hls\",\"method\":\"direct\"}", false), ("\"protocol\":\"file\",\"method\":\"transcode\"}", false),
        })
        {
            Assert.True(Playback.ReadPlayAnswer(Encoding.UTF8.GetBytes(Answer + tail), new PlayAsked { Capabilities = true }) is not null == ok, tail);
        }
    }

    [Fact]
    public void TargetRefusals()
    {
        var copy = new Copy("v1", 1000, "video/matroska", 100, "e1", [new CopyAudio("a0", "dts") { Codecs = "dtsh" }])
        {
            Video = new VideoStream("h264", 1920, 1080) { Codecs = "avc1.640028" },
            Parts = 2,
        };
        var makes = new OwnerMakes
        {
            VideoEncoders = [Names.BaselineVideoCodec], AudioEncoders = [Names.BaselineAudioCodec], Ranges = [Names.RangeSdr],
            SegmentCodecs = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                ["mp2t"] = ["h264", "aac"], ["mp4"] = ["h264", "aac"],
            },
        };
        foreach (var (r, problem) in new[]
        {
            (new PlayRequest { Delivery = "teleport" }, Playback.ProblemDeliveryRefused),
            (new PlayRequest { Delivery = Playback.DeliveryFile }, Playback.ProblemDeliveryRefused),
            (new PlayRequest { Delivery = Playback.DeliveryRemux, MediaType = "video/matroska" }, Playback.ProblemTargetRefused),
            (new PlayRequest { Delivery = Playback.DeliveryRemux, MediaType = "video/mp2t" }, Playback.ProblemTargetRefused),
            (new PlayRequest { Delivery = Playback.DeliveryTranscode, Transcode = new TranscodeTargets { VideoCodec = "vvc" } }, Playback.ProblemTargetRefused),
            (new PlayRequest { Delivery = Playback.DeliveryTranscode, Transcode = new TranscodeTargets { Range = "keep" } }, Playback.ProblemTargetRefused),
            (new PlayRequest { Delivery = Playback.DeliveryTranscode, Transcode = new TranscodeTargets { BurnSubtitle = "s3" } }, Playback.ProblemTargetRefused),
        })
        {
            var refused = Playback.RefuseDelivery(r with { Title = "movie/tmdb/949" }, copy, "a0", 0, makes);
            Assert.True(refused is { Status: 409 } && refused.Problem == problem, $"{r.Delivery} {r.MediaType}: {refused?.Problem}, want {problem}");
        }
    }

    [Fact]
    public void ProblemsAndMeaning()
    {
        foreach (var (type, status) in new[]
        {
            (Playback.ProblemGone, 404), (Playback.ProblemTargetRefused, 409), (Playback.ProblemUnreachable, 503),
            (Playback.ProblemTicketRefused, 404), (Playback.ProblemStreamLimit, 409),
        })
        {
            Assert.True(Problems.TryGet(type, out var s, out _) && s == status, type);
        }

        Assert.True(Problems.LanguageProblem(Playback.ProblemGone));
        Assert.False(Problems.LanguageProblem(Playback.ProblemStreamLimit));
        Assert.True(Problems.Temporary(Playback.ProblemUnreachable));
        Assert.False(Problems.Temporary(Playback.ProblemGone));
        foreach (var (status, type, play, want) in new[]
        {
            (404, Playback.ProblemGone, true, Problems.MeansGone), (404, "something-new", false, Problems.MeansNotFound),
            (409, Playback.ProblemTargetRefused, true, Problems.MeansAskElse), (409, Playback.ProblemDeliveryRefused, true, Problems.MeansAskElse),
            (409, Playback.ProblemNotPlayable, true, Problems.MeansNotPlayable), (409, Playback.ProblemStreamLimit, true, Problems.MeansBusy),
            (409, "something-new", true, Problems.MeansBusy), (409, "", true, Problems.MeansBusy), (410, "x", false, Problems.MeansStopped),
            (400, "x", false, Problems.MeansBadAsk), (429, "", false, Problems.MeansBusy), (503, Playback.ProblemUnreachable, true, Problems.MeansBusy),
            (500, "", false, Problems.MeansBusy), (404, Problems.NotSupported, true, Problems.MeansOther), (418, "", true, Problems.MeansOther),
            // A known type with another status is read by its status.
            (409, Playback.ProblemGone, true, Problems.MeansBusy),
        })
        {
            Assert.True(Problems.Meaning(status, type, play) == want, $"{status} {type}: {Problems.Meaning(status, type, play)}, want {want}");
        }
    }

    [Fact]
    public void ReadInstead()
    {
        const string Body = """
            {"type":"target-refused","title":"x","status":409,"instead":[
            {"delivery":"transcode","transcode":{"videoCodec":"h264","maxHeight":1080}},
            7,
            {"delivery":"remux"},
            {"delivery":"remux","mediaType":"video/mp4"},
            {"delivery":"transcode","transcode":{"maxHeight":1000}},
            {"delivery":"file"}]}
            """;
        var got = Problems.ReadInstead(Encoding.UTF8.GetBytes(Body), "");
        Assert.Equal(2, got.Count);
        Assert.Equal(Playback.DeliveryTranscode, got[0].Delivery);
        Assert.Equal(1080, got[0].Transcode!.MaxHeight);
        Assert.Equal("video/mp4", got[1].MediaType);

        // For a download, a remux isn't one it takes.
        var down = Problems.ReadInstead("""{"instead":[{"delivery":"remux","mediaType":"video/mp4"},{"delivery":"file"}]}"""u8, Playback.PurposeDownload);
        Assert.Equal(Playback.DeliveryFile, Assert.Single(down).Delivery);
        foreach (var body in new[] { """{"instead":{}}""", "[]", "x", """{"instead":[null]}""" })
        {
            Assert.Empty(Problems.ReadInstead(Encoding.UTF8.GetBytes(body), ""));
        }
    }

    [Fact]
    public void TitlesRequests()
    {
        var r = TitlesRequest.Read("{}"u8)!;
        Assert.Null(r.Libraries);
        Assert.True(r.Filter.None);
        r = TitlesRequest.Read("""{"libraries":[],"kind":"season","under":"series/tmdb/1","after":"a","since":-3}"""u8)!;
        Assert.Empty(r.Libraries!);
        Assert.Equal("season", r.Filter.Kind);
        Assert.True(r.Filter.HasUnder);
        Assert.Equal(0, r.Since);
        foreach (var body in new[]
        {
            "[]", """{"libraries":null}""", """{"libraries":"x"}""", """{"libraries":[1]}""", """{"kind":""}""", """{"kind":"Movie"}""",
            """{"under":1}""", """{"after":1}""", """{"since":1.5}""", """{"since":"1"}""",
        })
        {
            Assert.True(TitlesRequest.Read(Encoding.UTF8.GetBytes(body)) is null, body);
        }

        Assert.Equal("""{"libraries":[],"under":"","after":""}""",
            Encoding.UTF8.GetString(new TitlesRequest { Libraries = [], Filter = new TitleFilter { HasUnder = true } }.ToJson()));
        Assert.Equal("""{"kind":"track","after":"","since":4}""",
            Encoding.UTF8.GetString(new TitlesRequest { Filter = new TitleFilter { Kind = "track" }, Since = 4 }.ToJson()));
        var a = new TitlesRequest { Libraries = ["x", "y"], Filter = new TitleFilter { Kind = "movie" } };
        Assert.True(TitlesRequest.SinceAllowed(a, new TitlesRequest { Libraries = ["y", "x"], Filter = new TitleFilter { Kind = "movie" } }));
        Assert.False(TitlesRequest.SinceAllowed(a, new TitlesRequest { Libraries = ["x"], Filter = new TitleFilter { Kind = "movie" } }));
        Assert.False(TitlesRequest.SinceAllowed(a, new TitlesRequest { Libraries = ["x", "y"] }));
        Assert.False(TitlesRequest.SinceAllowed(new TitlesRequest(), new TitlesRequest { Libraries = [] }));
        var under = new TitlesRequest { Filter = new TitleFilter { Under = "x", HasUnder = true } };
        Assert.False(TitlesRequest.SinceAllowed(under, under));
    }

    [Fact]
    public void TitleFilterPasses()
    {
        static string Mb(string kind, string id) => kind + "/mb/" + id;
        var album = Mb("album", "11111111-1111-1111-1111-111111111111");
        var artist = Mb("artist", "22222222-2222-2222-2222-222222222222");
        var track = Mb("track", "33333333-3333-3333-3333-333333333333");
        static TitleFilter Under(string u) => new() { Under = u, HasUnder = true };
        var cases = new (TitleFilter F, FilterFacts T, bool Want)[]
        {
            (new TitleFilter(), new FilterFacts { Key = "movie/tmdb/1" }, true),
            (new TitleFilter(), new FilterFacts { Key = "not a key" }, false),
            (new TitleFilter { Kind = "movie" }, new FilterFacts { Key = "movie/tmdb/1" }, true),
            (new TitleFilter { Kind = "series" }, new FilterFacts { Key = "movie/tmdb/1" }, false),
            (Under("movie/tmdb/1"), new FilterFacts { Key = "movie/tmdb/1/x/a" }, true),
            (Under("movie/tmdb/1"), new FilterFacts { Key = "movie/tmdb/1" }, false),
            (Under("movie/tmdb/1"), new FilterFacts { Key = "movie/tmdb/12/x/a" }, false),
            (Under("series/tvdb/7"), new FilterFacts { Key = "series/tvdb/7/a12" }, true),
            (Under("series/tvdb/7"), new FilterFacts { Key = "series/tvdb/7/s1" }, true),
            (Under("series/tvdb/7"), new FilterFacts { Key = "series/tvdb/7/s1/e1" }, false),
            (Under("series/tvdb/7"), new FilterFacts { Key = "series/tvdb/7/s1/x/b" }, false),
            (Under("series/tvdb/7/s1"), new FilterFacts { Key = "series/tvdb/7/s1/x/b" }, true),
            (Under("series/tvdb/7/s1"), new FilterFacts { Key = "series/tvdb/7/s1/e1" }, true),
            (Under("series/tvdb/7/s1"), new FilterFacts { Key = "series/tvdb/7/s2/e1" }, false),
            (Under("series/tvdb/7/s1"), new FilterFacts { Key = "series/tvdb/7/a1" }, false),
            (Under("series/tmdb/7/s1"), new FilterFacts { Key = "series/tmdb/7/e99", PlaceSeason = 1 }, true),
            (Under("series/tmdb/7/s0"), new FilterFacts { Key = "series/tmdb/7/e99" }, false),
            (Under("series/tmdb/7/s2"), new FilterFacts { Key = "series/tmdb/7/e99", PlaceSeason = 1 }, false),
            (Under("series/tmdb/8/s1"), new FilterFacts { Key = "series/tmdb/7/e99", PlaceSeason = 1 }, false),
            (Under("series/tmdb/7/s1/e1"), new FilterFacts { Key = "series/tmdb/7/s1/e1" }, false),
            (Under(album), new FilterFacts { Key = track, Album = album }, true),
            (Under(album), new FilterFacts { Key = track }, false),
            (Under(artist), new FilterFacts { Key = album, Artists = [artist] }, true),
            (Under(artist), new FilterFacts { Key = track, Artists = [artist] }, false),
            (Under(""), new FilterFacts { Key = "movie/tmdb/1" }, false),
            (new TitleFilter { Kind = "extra", Under = "movie/tmdb/1", HasUnder = true }, new FilterFacts { Key = "movie/tmdb/1/x/a" }, true),
        };
        foreach (var (f, t, want) in cases)
        {
            Assert.True(f.Passes(t) == want, $"{f} {t}: {!want}");
        }

        Assert.Equal("series/tmdb/7/", Under("series/tmdb/7/s1").UnderPrefix);
        Assert.Equal("", Under(album).UnderPrefix);
    }

    [Fact]
    public void DiscoveryMsfpMedia()
    {
        Assert.Equal(new[] { 0, 1 }, Discovery.Read("""{"core":[0],"msfpMedia":[0,"x",1,1]}"""u8).MsfpMedia);
        Assert.Null(Discovery.Read("""{"core":[0]}"""u8).MsfpMedia);
        Assert.Throws<ProtocolException>(() => Discovery.Read("""{"msfpMedia":0}"""u8));
    }

    // The titles request as the owning server reads it (listing.json,
    // titleOffer and titleRequests): a source of the offer, run with
    // TitlesRequest.Read and TitleFilter.
    [Fact]
    public void TitleRequestVectors()
    {
        var v = TestVectors.Load("media/v0/listing.json");
        var offer = v["titleOffer"]!.AsObject();
        var visible = offer["visible"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        var titles = offer["titles"]!.AsArray().Select(e => e!.AsObject()).ToList();
        var cases = TestVectors.Cases("media/v0/listing.json", "titleRequests");
        Assert.Equal(48, cases.Count);
        Assert.NotEmpty(titles);
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var r = TitlesRequest.Read(Encoding.UTF8.GetBytes(c.Str("body")));
            if (r is not null != c.Valid())
            {
                failures.Add($"{c.Str("name")}: read {r is not null}");
                continue;
            }

            if (r is null)
            {
                continue;
            }

            var parts = c["parts"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
            var listed = new List<(string Key, string Library)>();
            foreach (var t in titles)
            {
                var key = t.Str("key");
                if (!TitleKey.TryParse(key, out var k) || !parts.Contains(k.Part) || string.CompareOrdinal(key, r.After) <= 0)
                {
                    continue;
                }

                var lib = "";
                foreach (var l in t["libraries"]!.AsArray().Select(e => e!.GetValue<string>()))
                {
                    if (visible.Contains(l) && (r.Libraries is null || r.Libraries.Contains(l)) && (lib.Length == 0 || string.CompareOrdinal(l, lib) < 0))
                    {
                        lib = l;
                    }
                }

                var facts = new FilterFacts
                {
                    Key = key, Album = t.Str("album"), PlaceSeason = t["season"]?.GetValue<int>(),
                    Artists = t["artists"]?.AsArray().Select(e => e!.GetValue<string>()).ToList() ?? [],
                };
                if (lib.Length > 0 && r.Filter.Passes(facts))
                {
                    listed.Add((key, lib));
                }
            }

            listed.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            var got = JsonOut.Object(o => o.Objects("titles", listed, (w, x) => w.String("key", x.Key).String("library", x.Library))
                .Bool("mayBeChanges", r.Since > 0 && r.Filter.Changes));
            var want = JsonSerializer.Serialize(JsonNode.Parse(c.Str("output")));
            if (Encoding.UTF8.GetString(got) != want)
            {
                failures.Add($"{c.Str("name")}: got {Encoding.UTF8.GetString(got)}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "titleRequests");
    }
}
