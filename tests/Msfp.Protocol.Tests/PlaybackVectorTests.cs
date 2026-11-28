using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Msfp.Protocol.Tests;

/// <summary>
/// The playback vectors, every array, as wire/playback_vectors_test.go runs
/// them: MSFP Media's playing.json (msfp-media's vectors/v0), the language's
/// cases under any carrier, and MSFP's playback.json (msfp-spec's vectors/v0),
/// what MSFP decides as its carrier (the purpose scan, the refusal of a
/// capability list, limits). Each file has its own context.
/// </summary>
public partial class PlaybackVectorTests
{
    private const string Media = "media/v0/playing.json";
    private const string Carrier = "msfp/v0/playback.json";

    // Title keys as the vectors write them (MSFP Media video 4, MSFP
    // Media music 2), the kinds play takes, local schemes and extras included.
    private const string PbId = "[1-9][0-9]{0,9}";
    private const string PbOpaque = "[A-Za-z0-9_-]{1,64}";
    private const string PbSeries = "series/(tmdb|tvdb)/" + PbId + "|series/local/" + PbOpaque;
    private const string PbFilm = "movie/tmdb/" + PbId + "|movie/local/" + PbOpaque;
    private const string PbSeason = "/s(0|[1-9][0-9]{0,3})";

    [GeneratedRegex("^(" + PbFilm + "|(" + PbSeries + ")" + PbSeason + "/e(0|[1-9][0-9]{0,4})|series/tmdb/" + PbId + "/e" + PbId +
        "|series/tvdb/" + PbId + "/a(0|[1-9][0-9]{0,4})" + "|(" + PbFilm + "|(" + PbSeries + ")(" + PbSeason + ")?)/x/" + PbOpaque +
        "|track/(mb/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|local/" + PbOpaque + "))\\z")]
    private static partial Regex PlayableRe();

    private static bool Playable(string key) => PlayableRe().IsMatch(key);

    private static JsonObject Context(string file) => TestVectors.Load(file)["context"]!.AsObject();

    private static int Int(JsonNode? n) => n is null ? 0 : (int)(JsonMembers.AsInteger(n) ?? 0);

    private static PlaybackLimits LimitsOf(JsonObject l) => new()
    {
        Streams = Int(l["streams"]), Transcodes = Int(l["transcodes"]), BitrateKbps = Int(l["bitrateKbps"]),
        Downloads = Int(l["downloads"]), DownloadsPerDay = Int(l["downloadsPerDay"]), DownloadKbps = Int(l["downloadKbps"]),
        ScansPerDay = Int(l["scansPerDay"]), AudioStreams = Int(l["audioStreams"]), AudioTranscodes = Int(l["audioTranscodes"]),
    };

    private static LimitUse UseOf(JsonObject u) => new()
    {
        Streams = Int(u["streams"]), Transcodes = Int(u["transcodes"]), Downloads = Int(u["downloads"]),
        DownloadsToday = Int(u["downloadsToday"]), ScansToday = Int(u["scansToday"]), ServerTranscodesFree = Int(u["serverTranscodesFree"]),
    };

    private static List<string> Strings(JsonNode? n) => n?.AsArray().Select(e => e!.GetValue<string>()).ToList() ?? [];

    // A copy of the context, as the catalog's Copy (the facts the baseline
    // and step 7 go by).
    private static Copy CopyOf(JsonObject c)
    {
        int? Opt(JsonNode? n) => n is null ? null : Int(n);
        VideoStream? video = null;
        if (c["video"] is JsonObject v)
        {
            var col = v["colour"] as JsonObject;
            video = new VideoStream(v.Str("codec"), 1, Int(v["height"]))
            {
                Codecs = v["codecs"] is null ? null : v.Str("codecs"),
                BitDepth = Opt(v["bitDepth"]),
                Interlaced = v["interlaced"]?.GetValue<bool>() ?? false,
                Colour = col is null ? null : new VideoColour(Opt(col["primaries"]), Opt(col["transfer"]), Opt(col["matrix"])),
            };
        }

        var audio = c["audio"]!.AsArray().Select(e => e!.AsObject()).Select(a => new CopyAudio(a.Str("id"), a.Str("codec"))
        {
            Codecs = a["codecs"] is null ? null : a.Str("codecs"),
            Channels = Opt(a["channels"]),
            SampleRate = Opt(a["sampleRate"]),
            Default = a["default"]?.GetValue<bool>() ?? false,
        }).ToList();
        return new Copy(c.Str("id"), c["durationMs"]!.GetValue<long>(), video is null ? "audio/mp4" : "video/mp4", c["size"]!.GetValue<long>(),
            c.Str("etag"), audio)
        {
            Parts = Int(c["parts"]) > 1 ? Int(c["parts"]) : null,
            Video = video,
            SubtitleTracks = Strings(c["subtitles"]).Select(id => new CopySubtitle(id, "")).ToList(),
        };
    }

    private static OwnerMakes MakesOf(JsonObject ctx, JsonObject c) => new()
    {
        VideoEncoders = Strings(ctx["videoEncoders"]),
        AudioEncoders = Strings(ctx["audioEncoders"]),
        Ranges = Strings(c["ranges"] ?? ctx["ranges"]),
        SegmentCodecs = ctx["segmentCodecs"]!.AsObject().ToDictionary(p => p.Key, p => (IReadOnlyCollection<string>)Strings(p.Value)),
    };

    // Two JSON texts compared, members whose value is a zero (false, 0, "")
    // left out of both, and members in any order.
    private static string Canon(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                var parts = new List<string>();
                foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (v is JsonValue jv && (jv.GetValueKind() == JsonValueKind.False ||
                        (jv.GetValueKind() == JsonValueKind.Number && jv.ToJsonString() == "0") ||
                        (jv.GetValueKind() == JsonValueKind.String && jv.GetValue<string>().Length == 0)))
                    {
                        continue;
                    }

                    parts.Add(JsonSerializer.Serialize(k) + ":" + Canon(v));
                }

                return "{" + string.Join(",", parts) + "}";
            case JsonArray a:
                return "[" + string.Join(",", a.Select(Canon)) + "]";
            default:
                return n?.ToJsonString() ?? "null";
        }
    }

    private static string Canon(string json) => Canon(JsonNode.Parse(json));

    private static JsonNode? Node(Action<JsonOut> fill) => JsonNode.Parse(JsonOut.Object(fill));

    // An owning server's play, section 2.2's steps, of the context's copies,
    // encoders and limits; for a queue entry, the request read as the
    // entry's (no counts, no idle rule: the playback counts once, under the
    // request).
    private static JsonObject Play(JsonObject ctx, JsonObject c)
    {
        var lim = LimitsOf((c["limits"] ?? ctx["limits"])!.AsObject());
        var inUse = (c["inUse"] ?? ctx["inUse"])!.AsObject();
        var use = UseOf(inUse);
        var progress = Int(c["progressSeconds"] ?? ctx["progressSeconds"]);
        var entry = c["entry"] is not null;

        // 1.
        var r = entry
            ? Playback.ReadQueueEntry(JsonNode.Parse(c.Str("request"))!.AsObject(), JsonNode.Parse(c.Str("entry")), Playable)
            : Playback.ReadPlay(Encoding.UTF8.GetBytes(c.Str("text")), Playable);

        // 2.
        if (lim.Open(r.Purpose) is { } open)
        {
            throw open;
        }

        // The playback it replaces, one of the caller's live ones: counted as
        // ended. Any other replaces is ignored.
        var replaced = "";
        foreach (var l in inUse["live"]?.AsArray().Select(e => e!.AsObject()) ?? [])
        {
            if (r.Replaces.Length > 0 && l.Str("id") == r.Replaces)
            {
                use = use.Without(l.Str("purpose"), l["transcode"]?.GetValue<bool>() ?? false);
                replaced = r.Replaces;
            }
        }

        // 3, 4.
        if (Strings(ctx["gone"]).Contains(r.Title))
        {
            throw new PlayRefusal(404, Playback.ProblemGone, "no longer held");
        }

        var copies = ctx["copies"]![r.Title]?.AsArray().Select(e => CopyOf(e!.AsObject())).ToList() ?? [];
        var i = Playback.ChooseCopy(copies, r.VersionId);
        if (i < 0)
        {
            throw new PlayRefusal(404, Playback.ProblemNotFound, "no copy");
        }

        if (!r.TrackIdsOk)
        {
            throw new PlayRefusal(400, Playback.ProblemInvalid, "a malformed track ID");
        }

        var cp = copies[i];
        var audio = Playback.ChooseAudio(cp, r.AudioTrack);

        // 5.
        if (lim.Room(r.Purpose, use) is { } room)
        {
            throw room;
        }

        // 6.
        var cap = r.Purpose == Playback.PurposePlay ? r.BitrateCap(Int(ctx["ownCapKbps"]), lim.BitrateKbps) : 0;

        // 7.
        if (Playback.RefuseDelivery(r, cp, audio, cap, MakesOf(ctx, c)) is { } refused)
        {
            throw refused;
        }

        // 8.
        var transcodes = "";
        if (Playback.TakesTranscode(r, cp, audio, cap))
        {
            if (lim.TranscodeRoom(r.Purpose, use) is { } busy)
            {
                throw busy;
            }

            // MSFP Media's words (playing.json): the carrier's transcodes and
            // the source's budget, the budget alone for a download.
            transcodes = r.Purpose == Playback.PurposeDownload ? "source" : "carrier and source";
        }

        if (Strings(ctx["unreachable"]).Contains(r.Title))
        {
            throw new PlayRefusal(503, Playback.ProblemUnreachable, "its media can't be reached");
        }

        var o = new JsonObject
        {
            ["title"] = r.Title,
            ["purpose"] = r.Purpose,
            ["delivery"] = r.Delivery,
            ["mediaType"] = r.MediaType,
            ["audioRenditions"] = r.AudioRenditions,
            ["versionId"] = cp.Id,
            ["audioTrack"] = audio,
            ["startPositionMs"] = r.Purpose == Playback.PurposeDownload ? 0 : r.StartPositionMs,
            ["bitrateCapKbps"] = cap,
            ["transcodes"] = transcodes,
            ["ticketSeconds"] = (long)Playback.Life(r.Purpose).TotalSeconds,
            ["idleSeconds"] = !entry && Playback.Idles(r.Purpose) ? (long)Playback.IdleLimit(progress).TotalSeconds : 0,
            ["progressSeconds"] = !entry && r.Purpose != Playback.PurposeDownload ? (long)Playback.ProgressInterval(progress).TotalSeconds : 0,
            ["replaces"] = replaced,
        };
        if (r.Delivery.Length == 0)
        {
            var b = Playback.Baseline(cp, audio, cap);
            o["baseline"] = Node(w => w.String("video", b.Video).String("audio", b.Audio).String("method", b.Method));
        }

        if (r.TargetsServed(cp.Video is not null) is { } t)
        {
            o["transcode"] = Node(t.Write);
        }

        switch (r.Purpose)
        {
            case Playback.PurposePlay:
                o["counts"] = entry ? "" : "streams";
                break;
            case Playback.PurposeDownload:
                o["counts"] = entry ? "" : "downloads";
                o["speedCapKbps"] = lim.DownloadKbps;
                break;
            case Playback.PurposeScan:
                o["counts"] = entry ? "" : "scansPerDay";
                o["scanBytes"] = Int(c["scanBytes"] ?? ctx["scanBytes"]);
                break;
        }

        return o;
    }

    // An owning server's progress or stop.
    private static JsonObject Report(JsonObject c, DateTimeOffset now)
    {
        var call = c.Str("call");
        var r = Playback.ReadReport(call, Encoding.UTF8.GetBytes(c.Str("text")), now);
        var p = c["playback"]!.AsObject();
        static DateTimeOffset? At(JsonNode? n) => Int(n) is var sec and not 0 ? DateTimeOffset.FromUnixTimeSeconds(sec) : null;
        var s = new PlaybackState
        {
            State = p.Str("state"),
            Purpose = p.Str("purpose"),
            Title = p.Str("title"),
            Queue = p["queue"]?.AsArray().Select(e => e!.GetValue<string>()).ToList() ?? [],
            PlayedAt = At(p["playedAt"]),
            LastAt = At(p["lastAt"]),
        };
        var o = s.Apply(call, r, now);
        var out_ = new JsonObject { ["effect"] = o.Effect, ["state"] = o.State };
        if (o.Effect is Playback.EffectRecorded or Playback.EffectRecordedAndEnded)
        {
            out_["title"] = o.Title;
            out_["at"] = o.At.ToUnixTimeSeconds();
            out_["paused"] = o.Paused;
            if (o.HasPosition)
            {
                out_["positionMs"] = o.PositionMs;
            }
        }

        return out_;
    }

    [Fact]
    public void Counts()
    {
        var media = TestVectors.Load(Media);
        var carrier = TestVectors.Load(Carrier);
        int N(JsonObject v, string s) => v[s]?.AsArray().Count ?? 0;
        Assert.Equal(98 + 20 + 106 + 24 + 21 + 36,
            N(media, "requests") + N(media, "replaces") + N(media, "deliveries") + N(media, "baseline") + N(media, "queueEntries") + N(media, "downloads"));
        Assert.Equal(61, N(media, "reports"));
        Assert.Equal(78, N(media, "answers"));
        Assert.Equal(19, N(media, "restarts"));
        Assert.Equal(35, N(media, "refusals"));
        Assert.Equal(0, N(media, "scans") + N(media, "limits"));
        Assert.Equal((10, 1, 19, 8, 21), (N(carrier, "requests"), N(carrier, "deliveries"), N(carrier, "scans"), N(carrier, "answers"), N(carrier, "limits")));
        Assert.Equal(0, N(carrier, "replaces") + N(carrier, "baseline") + N(carrier, "queueEntries") + N(carrier, "downloads") +
            N(carrier, "reports") + N(carrier, "restarts") + N(carrier, "refusals"));

        // MSFP Media leaves the refusal over a carrier's limits to the
        // carrier: the cases name it, and it is MSFP's.
        var lr = Context(Media)["limitRefusal"]!.AsObject();
        Assert.Equal((409, Playback.ProblemStreamLimit), (lr["status"]!.GetValue<int>(), lr.Str("type")));
    }

    [Theory]
    [InlineData(Media, "requests")]
    [InlineData(Media, "replaces")]
    [InlineData(Media, "deliveries")]
    [InlineData(Media, "baseline")]
    [InlineData(Media, "queueEntries")]
    [InlineData(Media, "downloads")]
    [InlineData(Media, "reports")]
    [InlineData(Carrier, "requests")]
    [InlineData(Carrier, "deliveries")]
    [InlineData(Carrier, "scans")]
    public void Calls(string file, string section)
    {
        var ctx = Context(file);
        var now = DateTimeOffset.FromUnixTimeSeconds(ctx["now"]!.GetValue<long>());
        var cases = TestVectors.Cases(file, section);
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var name = c.Str("name");
            JsonObject? output = null;
            int status;
            string problem;
            try
            {
                output = c.Str("call") is "progress" or "stop" ? Report(c, now) : Play(ctx, c);
                (status, problem) = (200, "");
            }
            catch (PlayRefusal e)
            {
                (status, problem) = (e.Status, e.Problem);
            }

            var wantStatus = c["status"]!.GetValue<int>();
            if (status != wantStatus || problem != c.Str("type"))
            {
                failures.Add($"{name}: {status} {problem}, want {wantStatus} {c.Str("type")}");
            }
            else if (output is not null && Canon(output) is var got && got != Canon(c.Str("output")))
            {
                failures.Add($"{name}: got {got}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, file + " " + section);
    }

    [Theory]
    [InlineData(Media)]
    [InlineData(Carrier)]
    public void Answers(string file)
    {
        var root = Context(file).Str("streamRoot");
        var cases = TestVectors.Cases(file, "answers");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var ask = c["asked"]!.AsObject();
            var asked = new PlayAsked
            {
                Purpose = ask.Str("purpose"), Delivery = ask.Str("delivery"), Video = ask.Str("part") == "video",
                CatalogEtag = ask.Str("catalogEtag"),
                Subtitles = Strings(ask["catalogSubtitles"]).Select(id => new CopySubtitle(id, "")).ToList(),
            };
            var a = Playback.ReadPlayAnswer(Encoding.UTF8.GetBytes(c.Str("text")), asked);
            if (a is null != !c.Valid())
            {
                failures.Add($"{c.Str("name")}: kept {a is not null}");
            }
            else if (a is not null && Canon(AnswerJson(a, root)) is var got && got != Canon(c.Str("output")))
            {
                failures.Add($"{c.Str("name")}: got {got}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, file + " answers");
    }

    private static JsonNode? AnswerJson(PlayAnswer a, string root) => Node(w => w
        .String("id", a.Id).String("ticket", a.Ticket).String("protocol", a.Protocol).String("method", a.Method)
        .String("etag", a.Etag).Integer("positionMs", a.PositionMs).String("versionId", a.VersionId)
        .String("audioTrack", a.AudioTrack).Bool("audioRenditions", a.AudioRenditions)
        .Integer("maxBitrateKbps", a.MaxBitrateKbps).Integer("scanBytes", a.ScanBytes)
        .Objects("subtitles", a.Subtitles.Count > 0 ? a.Subtitles : null, (o, s) => o.String("id", s.Id).String("form", s.Form).Bool("ass", s.Ass))
        .Integer("fontCount", a.FontCount).Strings("urls", a.Urls(root)).Bool("readTitleAgain", a.ReadTitleAgain)
        .Integer("progressSeconds", a.ProgressSeconds));

    // The caller's reading of a play refusal (MSFP Media playing 2.5, base 4).
    [Fact]
    public void Refusals()
    {
        var cases = TestVectors.Cases(Media, "refusals");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var r = Problems.ReadPlayRefusal(c["status"]!.GetValue<int>(), c["signed"]!.GetValue<bool>(), Encoding.UTF8.GetBytes(c.Str("body")),
                c.Str("purpose"));
            var got = Canon(Node(w => w.String("tells", r.Tells).Bool("dropTitle", r.DropTitle).Integer("retryAfter", r.RetryAfter)
                .Objects("instead", r.Instead.Count > 0 ? r.Instead : null, (o, i) => o.String("delivery", i.Delivery)
                    .String("mediaType", i.MediaType.Length > 0 ? i.MediaType : null).Object("transcode", i.Transcode is { } t ? t.Write : null))));
            if (got != Canon(c.Str("output")))
            {
                failures.Add($"{c.Str("name")}: got {got}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "refusals");
    }

    [Fact]
    public void Restarts()
    {
        var cases = TestVectors.Cases(Media, "restarts");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var rd = c["reading"]!.AsObject();
            var reading = new FileReading(rd.Str("versionId"), rd.Str("etag"), rd.Str("purpose"), rd.Str("delivery"),
                rd["offset"]?.GetValue<long>() ?? 0);
            var req = Playback.ReadPlay(Encoding.UTF8.GetBytes(c.Str("request")), _ => true);
            var o = Playback.Restart(reading, req, c["status"]!.GetValue<int>(), Encoding.UTF8.GetBytes(c.Str("answer")));
            var got = Canon(Node(w => w.Bool("join", o.Join).Integer("offset", o.Offset).Bool("stopNew", o.StopNew)
                .Bool("failRead", o.FailRead).Bool("readTitles", o.ReadTitles)));
            if (o.Join != c.Valid() || got != Canon(c.Str("output")))
            {
                failures.Add($"{c.Str("name")}: got {got}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "restarts");
    }

    [Fact]
    public void Limits()
    {
        var cases = TestVectors.Cases(Carrier, "limits");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var k = Playback.ReadLimits(Encoding.UTF8.GetBytes(c.Str("text")));
            if (k is null != !c.Valid())
            {
                failures.Add($"{c.Str("name")}: kept {k is not null}");
                continue;
            }

            if (k is null)
            {
                continue;
            }

            var l = k.Limits;
            var o = new JsonObject
            {
                ["downloads"] = l.Downloads, ["downloadsPerDay"] = l.DownloadsPerDay, ["downloadKbps"] = l.DownloadKbps,
                ["scansPerDay"] = l.ScansPerDay, ["offersDownloads"] = k.OffersDownloads,
                ["offersScans"] = k.OffersScans,
            };
            foreach (var (name, has, value) in new[]
            {
                ("streams", k.HasStreams, l.Streams), ("transcodes", k.HasTranscodes, l.Transcodes), ("bitrateKbps", k.HasBitrate, l.BitrateKbps),
                ("audioStreams", k.HasAudioStreams, l.AudioStreams), ("audioTranscodes", k.HasAudioTranscodes, l.AudioTranscodes),
            })
            {
                if (has)
                {
                    o[name] = value;
                }
            }

            // Compared exactly: a 0 or a false kept says something here.
            var want = JsonNode.Parse(c.Str("output"))!.AsObject();
            if (Sorted(o) != Sorted(want))
            {
                failures.Add($"{c.Str("name")}: got {o.ToJsonString()}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "limits");
    }

    private static string Sorted(JsonObject o) =>
        string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value!.ToJsonString()));

    // A request a viewer's server builds is one ReadPlay reads back as it is.
    [Fact]
    public void RequestRoundTrip()
    {
        var input = new PlayRequest
        {
            Title = "movie/tmdb/949", Delivery = Playback.DeliveryTranscode,
            Transcode = new TranscodeTargets { VideoCodec = "av1", MaxHeight = Video.StepHeight(1600), BurnSubtitle = "s4", Range = "keep" },
            MaxBitrateKbps = 25999, AudioTrack = "a2", AudioRenditions = true,
        };
        var body = input.ToJson();
        Assert.DoesNotContain("null", Encoding.UTF8.GetString(body), StringComparison.Ordinal);
        var got = Playback.ReadPlay(body);
        Assert.Equal(Encoding.UTF8.GetString((input with { Purpose = Playback.PurposePlay }).ToJson()), Encoding.UTF8.GetString(got.ToJson()));
        Assert.True(got.TrackIdsOk);
        Assert.Equal(25999, got.BitrateCap(0, 0));
        Assert.Equal(0, new PlayRequest { Title = "movie/tmdb/949" }.BitrateCap(0, 0));
        Assert.Equal(new TranscodeTargets { VideoCodec = "av1", MaxHeight = 1440, BurnSubtitle = "s4", Range = "keep", AudioCodec = "aac", AudioChannels = 2 },
            got.TargetsServed(true));
        Assert.Equal(new TranscodeTargets { AudioCodec = "aac", AudioChannels = 2 }, got.TargetsServed(false));

        // copy takes none of the targets that would change the stream.
        var copied = new TranscodeTargets { VideoCodec = Names.Copy, MaxHeight = 720, Range = "keep", AudioCodec = Names.Copy, AudioChannels = 6 };
        Assert.Equal(new TranscodeTargets { VideoCodec = Names.Copy, AudioCodec = Names.Copy }, copied.WithBaseline(true));

        // An unknown delivery or media type is read (refused at step 7);
        // one that isn't well formed is refused at once.
        var copy = new Copy("v", 1000, "video/matroska", 1, "t", []) { Video = new VideoStream("h264", 1920, 1080) };
        var dash = Playback.ReadPlay("""{"title":"movie/tmdb/949","delivery":"dash"}"""u8);
        Assert.Equal(409, Playback.RefuseDelivery(dash, copy, "", 0)?.Status);
        var mkv = Playback.ReadPlay("""{"title":"movie/tmdb/949","delivery":"remux","mediaType":"video/matroska"}"""u8);
        Assert.Equal(Playback.ProblemTargetRefused, Playback.RefuseDelivery(mkv, copy, "", 0)?.Problem);
        Assert.Equal(400, Assert.Throws<PlayRefusal>(() => Playback.ReadPlay("""{"title":"movie/tmdb/949","delivery":"File"}"""u8)).Status);
        Assert.Equal(400, Assert.Throws<PlayRefusal>(() =>
            Playback.ReadPlay("""{"title":"movie/tmdb/949","delivery":"remux","mediaType":"video/MP4"}"""u8)).Status);
    }

    [Fact]
    public void LimitsInRange()
    {
        var ok = new PlaybackLimits { Streams = 2, Transcodes = 1, Downloads = 1, DownloadsPerDay = 10, DownloadKbps = 20_000, ScansPerDay = 1000 };
        Assert.True(ok.InRange);
        Assert.True((ok with { Streams = 1000, Downloads = 1000 }).InRange);
        Assert.Equal(Playback.ScanBytesFloor, Playback.ScanCapOf(0));
        Assert.Equal(Playback.ScanBytesTop, Playback.ScanCapOf(Playback.ScanBytesTop));
        Assert.Equal(Playback.ScanBytesFloor, Playback.ScanCapOf(Playback.ScanBytesTop + 1L));
        PlaybackLimits[] bad =
        [
            ok with { Transcodes = 3 }, ok with { Streams = 1001, Transcodes = 0 }, ok with { ScansPerDay = 1_000_001 },
            ok with { DownloadKbps = -1 }, ok with { AudioTranscodes = 1 }, ok with { Downloads = 1001 },
        ];
        foreach (var l in bad)
        {
            Assert.False(l.InRange, $"{l} accepted");
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    [InlineData(7, 6)]
    [InlineData(64, 8)]
    public void StepDown(int v, int want) => Assert.Equal(want, Playback.StepDown(v, Playback.ChannelSteps));
}
