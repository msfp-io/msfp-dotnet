using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Msfp.Protocol.Tests;

/// <summary>The playback vectors (vectors/v0/playback.json): every array, as wire/playback_vectors_test.go runs them.</summary>
public partial class PlaybackVectorTests
{
    private const string File = "playback.json";

    // Title keys as the vectors write them (video section 4, music section
    // 2), the kinds play takes, local schemes and extras included.
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

    private static JsonObject Context => TestVectors.Load(File)["context"]!.AsObject();

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

    private static PlayCopy CopyOf(JsonObject c) => new()
    {
        Id = c.Str("id"),
        Parts = Int(c["parts"]),
        BitrateKbps = Int(c["bitrateKbps"]),
        Audio = c["audio"]?.AsArray().Select(a => a!.AsObject())
            .Select(a => new PlayCopyAudio(a.Str("id"), a.Str("language"), a.Str("role"), a["default"]?.GetValue<bool>() ?? false))
            .ToList() ?? [],
        Subtitles = c["subtitles"]?.AsArray().Select(s => s!.GetValue<string>()).ToList() ?? [],
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

    // An owning server's play, section 2.2's steps, of the context's copies and limits.
    private static JsonObject Play(JsonObject ctx, JsonObject c)
    {
        var lim = LimitsOf((c["limits"] ?? ctx["limits"])!.AsObject());
        var use = UseOf((c["inUse"] ?? ctx["inUse"])!.AsObject());

        // 1.
        var r = Playback.ReadPlay(Encoding.UTF8.GetBytes(c.Str("text")), Playable);

        // 2.
        if (lim.Open(r.Purpose) is { } open)
        {
            throw open;
        }

        // 3, 4.
        var copies = ctx["copies"]![r.Title]?.AsArray().Select(e => CopyOf(e!.AsObject())).ToList() ?? [];
        var i = Playback.ChooseCopy(copies, r.VersionId, r.AudioLanguage);
        if (i < 0)
        {
            throw new PlayRefusal(404, Playback.ProblemNotFound, "no copy");
        }

        if (!r.TrackIdsOk)
        {
            throw new PlayRefusal(400, Playback.ProblemInvalid, "a malformed track ID");
        }

        var cp = copies[i];
        var audio = Playback.ChooseAudio(cp.Audio, r.AudioTrack, r.AudioLanguage);

        // 5.
        if (lim.Room(r.Purpose, use) is { } room)
        {
            throw room;
        }

        // 6.
        var cap = r.Purpose == Playback.PurposePlay ? r.BitrateCap(Int(ctx["ownCapKbps"]), lim.BitrateKbps) : 0;

        // 7.
        if (Playback.RefuseDelivery(r, cp, cap) is { } refused)
        {
            throw refused;
        }

        // 8.
        var transcodes = "";
        if (r.Delivery == Playback.DeliveryTranscode)
        {
            if (lim.TranscodeRoom(r.Purpose, use) is { } busy)
            {
                throw busy;
            }

            transcodes = r.Purpose == Playback.PurposeDownload ? "server" : "pairing and server";
        }

        var o = new JsonObject
        {
            ["title"] = r.Title,
            ["purpose"] = r.Purpose,
            ["delivery"] = r.Delivery,
            ["container"] = r.Container,
            ["audioRenditions"] = r.AudioRenditions,
            ["versionId"] = cp.Id,
            ["audioTrack"] = audio,
            ["startPositionMs"] = r.Purpose == Playback.PurposeDownload ? 0 : r.StartPositionMs,
            ["bitrateCapKbps"] = cap,
            ["transcodes"] = transcodes,
            ["ticketSeconds"] = (long)Playback.Life(r.Purpose).TotalSeconds,
            ["idleSeconds"] = Playback.Idles(r.Purpose) ? (long)Playback.IdleEnd.TotalSeconds : 0,
        };
        if (r.Transcode is { } t)
        {
            o["transcode"] = Node(t.Write);
        }

        if (r.Capabilities is { } caps)
        {
            o["capabilities"] = Node(caps.Write);
        }

        switch (r.Purpose)
        {
            case Playback.PurposePlay:
                o["counts"] = "streams";
                break;
            case Playback.PurposeDownload:
                o["counts"] = "downloads";
                o["speedCapKbps"] = lim.DownloadKbps;
                break;
            case Playback.PurposeScan:
                o["counts"] = "scansPerDay";
                o["readCapBytes"] = Playback.ScanReadCap;
                break;
        }

        return o;
    }

    // What a viewer's server sends of a device's description: {title,
    // capabilities, maxBitrateKbps}, read leniently (it is its own).
    private static JsonObject Coarsen(string text)
    {
        var input = JsonNode.Parse(text)!.AsObject();
        var m = input["capabilities"] as JsonObject ?? [];
        List<string> Strs(string name) =>
            (m[name] as JsonArray)?.Select(JsonMembers.AsString).OfType<string>().ToList() ?? [];
        static int Num(JsonNode? v) => JsonMembers.AsInteger(v) is { } n ? (int)Math.Clamp(n, int.MinValue, int.MaxValue) : 0;
        var c = new PlayCapabilities
        {
            Containers = Strs("containers"), VideoCodecs = Strs("videoCodecs"), AudioCodecs = Strs("audioCodecs"),
            AudioProfiles = Strs("audioProfiles"), Hdr = Strs("hdr"),
            DvProfiles = (m["dvProfiles"] as JsonArray)?.Select(JsonMembers.AsInteger).OfType<long>().Select(n => (int)n).ToList() ?? [],
            MaxHeight = Num(m["maxHeight"]), MaxAudioChannels = Num(m["maxAudioChannels"]),
            MaxAudioSampleRate = Num(m["maxAudioSampleRate"]), MaxAudioBitDepth = Num(m["maxAudioBitDepth"]),
            MaxBitrateKbps = Num(m["maxBitrateKbps"]),
        };
        var o = new JsonObject { ["title"] = input["title"]?.DeepClone() };
        if (c.Coarsen() is { Empty: false } cc)
        {
            o["capabilities"] = Node(cc.Write);
        }

        if (input.ContainsKey("maxBitrateKbps"))
        {
            o["maxBitrateKbps"] = Playback.CoarsenBitrate(Num(input["maxBitrateKbps"]));
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
        var v = TestVectors.Load(File);
        int N(string s) => v[s]!.AsArray().Count;
        Assert.Equal(131 + 58 + 36 + 16, N("requests") + N("deliveries") + N("downloads") + N("scans"));
        Assert.Equal(49, N("reports"));
        Assert.Equal(59, N("answers"));
        Assert.Equal(16, N("restarts"));
        Assert.Equal(17, N("limits"));
    }

    [Theory]
    [InlineData("requests")]
    [InlineData("deliveries")]
    [InlineData("downloads")]
    [InlineData("scans")]
    [InlineData("reports")]
    public void Calls(string section)
    {
        var ctx = Context;
        var now = DateTimeOffset.FromUnixTimeSeconds(ctx["now"]!.GetValue<long>());
        var cases = TestVectors.Cases(File, section);
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var name = c.Str("name");
            if (c.Str("by") == "viewer")
            {
                if (Canon(Coarsen(c.Str("text"))) is var got && got != Canon(c.Str("output")))
                {
                    failures.Add($"{name}: got {got}");
                }

                continue;
            }

            JsonObject? output = null;
            int status;
            string problem;
            try
            {
                output = c.Str("call") == "play" ? Play(ctx, c) : Report(c, now);
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

        TestVectors.NoFailures(failures, cases.Count, section);
    }

    [Fact]
    public void Answers()
    {
        var root = Context.Str("streamRoot");
        var cases = TestVectors.Cases(File, "answers");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var a = Playback.ReadPlayAnswer(Encoding.UTF8.GetBytes(c.Str("text")), c.Str("delivery"), root, c.Str("catalogEtag"));
            if (a is null != !c.Valid())
            {
                failures.Add($"{c.Str("name")}: kept {a is not null}");
            }
            else if (a is not null && Canon(AnswerJson(a)) is var got && got != Canon(c.Str("output")))
            {
                failures.Add($"{c.Str("name")}: got {got}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "answers");
    }

    private static JsonNode? AnswerJson(PlayAnswer a) => Node(w => w
        .String("id", a.Id).String("method", a.Method)
        .Object("stream", s => s.String("url", a.Stream.Url).String("protocol", a.Stream.Protocol).String("mimeType", a.Stream.MimeType))
        .String("etag", a.Etag).Integer("positionMs", a.PositionMs).Integer("durationMs", a.DurationMs)
        .Objects("versions", a.Versions.Count > 0 ? a.Versions : null, (o, v) => o.String("id", v.Id).String("name", v.Name))
        .String("versionId", a.VersionId)
        .Objects("audioTracks", a.AudioTracks.Count > 0 ? a.AudioTracks : null, (o, t) => o.String("id", t.Id)
            .String("language", t.Language).Integer("channels", t.Channels).String("role", t.Role)
            .Bool("default", t.Default).Bool("forced", t.Forced).Bool("hearingImpaired", t.HearingImpaired))
        .String("audioTrack", a.AudioTrack).Integer("maxBitrateKbps", a.MaxBitrateKbps).Bool("readTitleAgain", a.ReadTitleAgain));

    [Fact]
    public void Restarts()
    {
        var root = Context.Str("streamRoot");
        var cases = TestVectors.Cases(File, "restarts");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var rd = c["reading"]!.AsObject();
            var reading = new FileReading(rd.Str("versionId"), rd.Str("etag"), rd.Str("purpose"), rd.Str("delivery"),
                rd["offset"]?.GetValue<long>() ?? 0);
            var req = Playback.ReadPlay(Encoding.UTF8.GetBytes(c.Str("request")), _ => true);
            var o = Playback.Restart(reading, req, c["status"]!.GetValue<int>(), Encoding.UTF8.GetBytes(c.Str("answer")), root);
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
        var cases = TestVectors.Cases(File, "limits");
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
                ["scansPerDay"] = l.ScansPerDay, ["offersDownloads"] = k.OffersDownloads, ["offersScans"] = k.OffersScans,
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
        var caps = new PlayCapabilities
        {
            Containers = ["MP4", "ts", "dash"], Hdr = ["dv"], DvProfiles = [8, 5], MaxHeight = 1600, MaxAudioChannels = 7,
            MaxBitrateKbps = 25999,
        }.Coarsen();
        Assert.Equal(["mp4", "ts"], caps.Containers);
        Assert.Equal([5, 8], caps.DvProfiles);
        Assert.Equal(1440, caps.MaxHeight);
        Assert.Equal(6, caps.MaxAudioChannels);
        Assert.Equal(25000, caps.MaxBitrateKbps);
        var input = new PlayRequest
        {
            Title = "movie/tmdb/949", Delivery = Playback.DeliveryTranscode, Capabilities = caps,
            Transcode = new TranscodeTargets { MaxHeight = 1080, BurnSubtitle = "s4" },
            MaxBitrateKbps = Playback.CoarsenBitrate(25999), AudioTrack = "a2",
        };
        var body = input.ToJson();
        Assert.DoesNotContain("null", Encoding.UTF8.GetString(body), StringComparison.Ordinal);
        var got = Playback.ReadPlay(body);
        Assert.Equal(Encoding.UTF8.GetString((input with { Purpose = Playback.PurposePlay }).ToJson()), Encoding.UTF8.GetString(got.ToJson()));
        Assert.True(got.TrackIdsOk);
        Assert.Equal(25000, got.BitrateCap(0, 0));
    }

    [Fact]
    public void LimitsInRange()
    {
        var ok = new PlaybackLimits { Streams = 2, Transcodes = 1, Downloads = 1, DownloadsPerDay = 10, DownloadKbps = 20_000, ScansPerDay = 1000 };
        Assert.True(ok.InRange);
        PlaybackLimits[] bad =
        [
            ok with { Transcodes = 3 }, ok with { Streams = 51 }, ok with { ScansPerDay = 1_000_001 }, ok with { DownloadKbps = -1 },
            ok with { AudioTranscodes = 1 },
        ];
        foreach (var l in bad)
        {
            Assert.False(l.InRange, $"{l} accepted");
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    [InlineData(1, 16)]
    [InlineData(16, 16)]
    [InlineData(23, 16)]
    [InlineData(24, 24)]
    [InlineData(64, 32)]
    public void StepDown(int v, int want) => Assert.Equal(want, Playback.StepDown(v, Playback.BitDepthSteps));
}
