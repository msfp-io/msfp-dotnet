using System.Globalization;
using System.Text.RegularExpressions;
using Msfp.Protocol;

namespace Msfp;

/// <summary>What the engine knows of a playback's own stream: the copy, its audio track played, and what is served of it.</summary>
/// <param name="Copy">The copy, as the catalog sends it.</param>
/// <param name="Audio">The audio track played.</param>
/// <param name="Served">What is served of it.</param>
internal sealed record ServedFacts(Copy Copy, string Audio, Served Served);

// The master playlist's attributes (MSFP Media fetching 3.1, MSFP
// Media playing 2.7): each #EXT-X-STREAM-INF carries BANDWIDTH, CODECS and, for video,
// RESOLUTION; VIDEO-RANGE (PQ or HLG) when the picture served is PQ or HLG;
// SUPPLEMENTAL-CODECS when Dolby Vision is kept; an E-AC-3 rendition whose
// objects is dolby-atmos may say CHANNELS "16/JOC". They are the host's to
// write, its transcoder knowing what it makes. The engine fills, for a
// playback's own stream, those the copy's facts tell when what is served of
// them is the copy's own (a stream copied, the range kept), and logs those
// that stay missing.
internal static partial class MasterPlaylist
{
    [GeneratedRegex(@"(^|,)([A-Z0-9-]+)=")]
    private static partial Regex AttrName();

    private static bool Has(string list, string name) =>
        AttrName().Matches(list).Any(m => m.Groups[2].Value == name);

    // An attribute's quoted value in a list, unquoted; "" when it has none.
    private static string QuotedValue(string list, string name)
    {
        var i = ("," + list).IndexOf("," + name + "=\"", StringComparison.Ordinal);
        if (i < 0)
        {
            return "";
        }

        var rest = list[(i + name.Length + 2)..];
        var end = rest.IndexOf('"', StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    private static string Quote(string s) => "\"" + s + "\"";

    /// <summary>Fills the attributes the facts tell into a master playlist, and names those still missing.</summary>
    public static (byte[] Playlist, IReadOnlyList<string> Missing) Fill(byte[] playlist, ServedFacts f)
    {
        var v = f.Copy.Video;
        var videoCopied = v is not null && f.Served.Video == Names.Copy;
        var audioCopied = f.Served.Audio == Names.Copy;
        var track = f.Copy.AudioTracks.LastOrDefault(a => a.Id == f.Audio);
        // What the facts tell.
        var codecs = new List<string>();
        if (videoCopied && !string.IsNullOrEmpty(v!.Codecs))
        {
            codecs.Add(v.Codecs);
        }

        if (audioCopied && !string.IsNullOrEmpty(track?.Codecs))
        {
            codecs.Add(track.Codecs);
        }

        var codecsKnown = (v is null || (videoCopied && !string.IsNullOrEmpty(v.Codecs))) && audioCopied && !string.IsNullOrEmpty(track?.Codecs);
        var rangeKept = v is not null && (videoCopied || f.Served.Transcode is { Range: Names.RangeKeep });
        var videoRange = rangeKept ? v!.Colour?.Transfer switch
        {
            VideoColour.TransferPq => "PQ",
            VideoColour.TransferHlg => "HLG",
            _ => "",
        } : "";
        var supplemental = "";
        if (videoCopied && v!.Dv is not null && Rfc6381.Read(v.Codec, v.Codecs, v.BitDepth, v.Dv, out _) is { DolbyVision: { Length: > 0 } dv })
        {
            supplemental = dv;
        }

        long bandwidth = 0;
        if ((v is null || videoCopied) && audioCopied && f.Copy.DurationMs > 0 && f.Copy.AudioTracks.Count <= 1)
        {
            bandwidth = f.Copy.Size * 8 * 1000 / f.Copy.DurationMs;
        }

        var lines = System.Text.Encoding.UTF8.GetString(playlist).Split('\n');
        var missing = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var head = line[..colon];
            var list = line[(colon + 1)..];
            if (head == "#EXT-X-STREAM-INF")
            {
                void Add(string name, string value)
                {
                    if (!Has(list, name) && value.Length > 0)
                    {
                        list += "," + name + "=" + value;
                    }

                    if (!Has(list, name))
                    {
                        missing.Add(name);
                    }
                }

                Add("CODECS", codecsKnown ? Quote(string.Join(',', codecs)) : "");
                Add("BANDWIDTH", bandwidth > 0 ? bandwidth.ToString(CultureInfo.InvariantCulture) : "");
                if (v is not null)
                {
                    Add("RESOLUTION", videoCopied ? v.Width.ToString(CultureInfo.InvariantCulture) + "x" + v.Height.ToString(CultureInfo.InvariantCulture) : "");
                }

                if (videoRange.Length > 0 && !Has(list, "VIDEO-RANGE"))
                {
                    list += ",VIDEO-RANGE=" + videoRange;
                }

                if (supplemental.Length > 0 && !Has(list, "SUPPLEMENTAL-CODECS"))
                {
                    list += ",SUPPLEMENTAL-CODECS=" + Quote(supplemental);
                }

                lines[i] = head + ":" + list;
            }
            else if (head == "#EXT-X-MEDIA" && list.Contains("TYPE=AUDIO", StringComparison.Ordinal) && !Has(list, "CHANNELS") && audioCopied)
            {
                // A rendition of an E-AC-3 track with Atmos, left unchanged.
                var id = QuotedValue(list, "NAME");
                if (id.Length > 0 && f.Copy.AudioTracks.Any(a => a.Id == id && a.Codec == "eac3" && a.Objects == "dolby-atmos"))
                {
                    lines[i] = line + ",CHANNELS=\"16/JOC\"";
                }
            }
        }

        var names = new[] { "BANDWIDTH", "CODECS", "RESOLUTION" }.Where(missing.Contains).ToList();
        return (System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines)), names);
    }
}
