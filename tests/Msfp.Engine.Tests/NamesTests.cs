using System.Globalization;
using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp.Tests;

// What an owning server serves under a ticket, and the master playlist's
// attributes it fills (msfp-go's names_test.go).
public sealed class NamesTests
{
    // The names served under a ticket (media.json's names, MSFP media
    // sections 1 and 4): exactly those its answer opened, 404 past the
    // counts.
    [Fact]
    public void ServedNames()
    {
        var cases = Vectors.Load("media.json")["names"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.NotEmpty(cases);
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var t = c["ticket"]!.AsObject();
            var text = (string)c["text"]!;
            List<string> L(string name) => t[name]?.AsArray().Select(e => (string)e!).ToList() ?? [];
            int N(string name) => (int?)t[name] ?? 0;
            var kind = (string)t["kind"]!;
            bool ok;
            switch (kind)
            {
                case "image":
                    ok = text == "image";
                    break;
                case "previews":
                    ok = MediaPath.TryParse(text, out var p) && p.Kind == MediaKind.Sheet && p.Sheet < N("sheetCount");
                    break;
                default:
                    var purpose = kind switch { "playback" => Playback.PurposePlay, "download" => Playback.PurposeDownload, _ => Playback.PurposeScan };
                    var pb = new LivePlayback { Purpose = purpose, Fonts = N("fontCount"), Sheets = purpose == Playback.PurposePlay ? N("sheetCount") : 0 };
                    pb.Entries[0] = new EntryServed((string)t["protocol"]!, 0, "", "");
                    if (purpose != Playback.PurposeScan)
                    {
                        foreach (var s in t["subtitles"]?.AsArray() ?? [])
                        {
                            var form = (string)s!["form"]!;
                            pb.Subtitles[(string)s["id"]!] = new SubtitleServed(form == Video.FormText, (bool?)s["ass"] ?? false, form == Video.FormPicture);
                        }
                    }

                    var queue = L("queue");
                    for (var n = 0; n < queue.Count; n++)
                    {
                        if (queue[n].Length > 0)
                        {
                            pb.Entries[n + 1] = new EntryServed(queue[n], 0, "", "");
                        }
                    }

                    for (var e = 0; e <= queue.Count; e++)
                    {
                        foreach (var name in L("names"))
                        {
                            pb.Names.Add(e.ToString(CultureInfo.InvariantCulture) + "/" + name);
                        }
                    }

                    ok = MsfpEngine.MediaPathOf(text, pb, out _);
                    break;
            }

            if (ok != (bool)c["valid"]!)
            {
                failures.Add($"{c["name"]} ({kind} {text}): served {ok}");
            }
        }

        Assert.Empty(failures);
    }

    // The master playlist's attributes the copy's facts tell, filled when the
    // host leaves them out (media section 3.1).
    [Fact]
    public void MasterAttributes()
    {
        var c = new Copy("v", 1000, "video/mp4", 1000, "e", [new CopyAudio("a1", "eac3") { Codecs = "ec-3", Objects = "dolby-atmos" }])
        {
            Video = new VideoStream("hevc", 3840, 2160)
            {
                Codecs = "hvc1.2.4.L153.B0", BitDepth = 10, Colour = new VideoColour(1, VideoColour.TransferPq, null), Dv = new VideoDv(8, 6, 1),
            },
        };
        var f = new ServedFacts(c, "a1", new Served { Method = Playback.MethodRemux, Video = Names.Copy, Audio = Names.Copy });
        const string In = "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"a1\",URI=\"a1.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=9000\nv0.m3u8\n";
        var (output, missing) = MasterPlaylist.Fill(HostKit.Bytes(In), f);
        Assert.Equal("#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"a1\",URI=\"a1.m3u8\",CHANNELS=\"16/JOC\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=9000,CODECS=\"hvc1.2.4.L153.B0,ec-3\",RESOLUTION=3840x2160,VIDEO-RANGE=PQ,SUPPLEMENTAL-CODECS=\"dvh1.08.06\"\nv0.m3u8\n",
            System.Text.Encoding.UTF8.GetString(output));
        Assert.Empty(missing);
        // Converted to the baseline: nothing the facts tell, CODECS and RESOLUTION missing.
        (output, missing) = MasterPlaylist.Fill(HostKit.Bytes(In), f with { Served = new Served { Method = Playback.MethodTranscode, Video = "h264", Audio = "aac" } });
        Assert.DoesNotContain("PQ", System.Text.Encoding.UTF8.GetString(output), StringComparison.Ordinal);
        Assert.Equal(["CODECS", "RESOLUTION"], missing);
    }
}
