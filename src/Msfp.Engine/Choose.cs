using System.Text;
using System.Text.RegularExpressions;
using Msfp.Protocol;

namespace Msfp;

/// <summary>
/// The codecs a server's <see cref="IPlayback"/> converts to, as ffmpeg names
/// them (<see cref="MsfpConfig.Encoders"/>): a peer's transcode to another is
/// refused, 409 delivery-refused (MSFP playback section 2.7). The baseline's,
/// h264 and aac, are always among them.
/// </summary>
public sealed record Encoders
{
    /// <summary>The video codecs besides h264 (hevc, av1…).</summary>
    public IReadOnlyList<string> Video { get; init; } = [];

    /// <summary>The audio codecs besides aac (ac3, eac3, opus…).</summary>
    public IReadOnlyList<string> Audio { get; init; } = [];

    internal IReadOnlyList<string> AllVideo => [Names.BaselineVideoCodec, .. Video];

    internal IReadOnlyList<string> AllAudio => [Names.BaselineAudioCodec, .. Audio];
}

/// <summary>
/// What the owning server chooses for a peer, by default (MSFP playback
/// section 2.6, video section 3.6): the copy and the tracks a play plays when
/// it names none, and what a copy's name and a track's title say. Each is an
/// <see cref="MsfpConfig"/> hook; these are the recommended defaults, and an
/// owning server that chooses otherwise still conforms.
/// </summary>
public static partial class MsfpDefaults
{
    /// <summary>
    /// <see cref="MsfpConfig.ChooseCopy"/>'s default, playback section 2.6's
    /// recommended default (<see cref="Playback.ChooseCopy"/>): the first copy
    /// whose audio, the title's own sound, has the request's audioLanguage
    /// (the same tag looked for in every copy before the same base language),
    /// else the first, the host's preferred.
    /// </summary>
    /// <param name="copies">The title's copies, in the host's order.</param>
    /// <param name="request">The peer's request.</param>
    public static int ChooseCopy(IReadOnlyList<Copy> copies, Protocol.PlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(request);
        // P17b: no language in the request since proposal 17: the first copy.
        return Math.Max(Playback.ChooseCopy(copies, ""), 0);
    }

    /// <summary>
    /// <see cref="MsfpConfig.ChooseTracks"/>' default, playback section 2.6's
    /// and video section 5.4's recommended defaults: the audio of the title's
    /// own sound in audioLanguage (the same tag, then the same base language),
    /// else the track the file marks default, else the first
    /// (<see cref="Playback.ChooseAudio"/>); the subtitles in
    /// subtitleLanguage, a regular track before a forced one, else none.
    /// </summary>
    /// <param name="copy">The copy chosen.</param>
    /// <param name="request">The peer's request.</param>
    public static (string Audio, string Subtitle) ChooseTracks(Copy copy, Protocol.PlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(request);
        // P17b: no language in the request since proposal 17: the file's
        // default audio, no subtitles.
        return (Playback.ChooseAudio(copy, ""), MsfpEngine.ChooseSubtitles(copy.SubtitleTracks ?? [], "", ""));
    }

    // The signs of a file name or a release's name in a label: a path, an
    // extension, words joined by dots or underscores, brackets, a release
    // group after a dash, a release's source or encoder.
    [GeneratedRegex(@"[/\\_\[\]{}]|\.[A-Za-z0-9]{2,4}\z|[A-Za-z0-9_]\.[A-Za-z0-9_]+\.[A-Za-z0-9_]|\S-[A-Za-z0-9]+\z|(?i)(?<![A-Za-z0-9_])(x26[45]|h\.?26[45]|web-?dl|web-?rip|blu-?ray|bdrip|brrip|hdtv|dvdrip)(?![A-Za-z0-9_])")]
    private static partial Regex FileLike();

    /// <summary>
    /// <see cref="MsfpConfig.CopyName"/>'s default, a copy's name as sent
    /// (MSFP video section 3.6): an edition or quality label, plain text of at
    /// most 64 characters; "" for one that looks like a file name or a
    /// release's name, which is never sent. It is the owner's choice, made for
    /// its privacy: a host may keep names another way.
    /// </summary>
    /// <param name="name">The host's name of the copy.</param>
    public static string CopyName(string name)
    {
        var s = Music.CleanText(name ?? "", (name ?? "").Length);
        return FileLike().IsMatch(s) ? "" : Music.CleanText(s, 64); // P17b: Copies.MaxName went with proposal 17
    }

    // The words a track's title may hold (video section 3.6: a description
    // of the track, such as "Commentary", "SDH", "Signs & Songs" or
    // "Forced"), lowercase. A title with any other word (a language's name, a
    // release group's) is dropped: the engine can't tell a description from
    // the rest otherwise.
    private static readonly HashSet<string> TrackWords = new(
        """
        commentary commentaries director director's directors cast crew writer writers producer producers composer
        actor actors and & + with by of the track tracks
        sdh cc hi hoh hearing impaired deaf captions closed
        forced full complete signs songs sign song dialogue dialog dialogues lyrics karaoke
        audio description descriptive described narration narrated visually
        original dub dubbed dubbing stereo mono surround atmos lossless
        music score isolated effects only simplified traditional
        commentaire commentaires forcé forcés forcée forcées complets sourds malentendants audiodescription
        kommentar erzwungen comentario comentarios forzados audiodescripción
        """.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    [GeneratedRegex(@"^[0-9]{1,2}(\.[0-9])?\z")]
    private static partial Regex TrackNumber();

    /// <summary>
    /// <see cref="MsfpConfig.TrackTitle"/>'s default, a track's title as sent
    /// (MSFP video section 3.6): plain text of at most 64 characters, made
    /// only of words that describe a track (Commentary, SDH, Forced, Signs
    /// &amp; Songs…) and channel counts ("5.1"); "" for any other, a title
    /// that is only a language's name or carries a release group's among
    /// them. It is the owner's choice, made for its privacy: a host may keep
    /// titles another way.
    /// </summary>
    /// <param name="title">The host's title of the track.</param>
    public static string TrackTitle(string title)
    {
        var s = Music.CleanText(title ?? "", 64); // P17b: Copies.MaxName went with proposal 17
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var r in s.ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsLetter(r) || Rune.IsDigit(r) || r.Value is '\'' or '.' or '&' or '+')
            {
                word.Append(r.ToString());
                continue;
            }

            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words.Count > 0 && words.TrueForAll(w => TrackWords.Contains(w) || TrackNumber().IsMatch(w)) ? s : "";
    }
}

public sealed partial class MsfpEngine
{
    // The copy a play plays (playback section 2.6): the one versionId names,
    // which wins; else the hook's, the first when it answers out of range.
    internal static int ChosenCopy(MsfpConfig cfg, IReadOnlyList<Copy> copies, Protocol.PlayRequest request)
    {
        if (request.VersionId.Length > 0)
        {
            for (var i = 0; i < copies.Count; i++)
            {
                if (copies[i].Id == request.VersionId)
                {
                    return i;
                }
            }
        }

        var n = (cfg.ChooseCopy ?? MsfpDefaults.ChooseCopy)(copies, request);
        return n >= 0 && n < copies.Count ? n : 0;
    }

    // The audio and the subtitles a play plays of the copy (playback section
    // 2.6, video section 5.4): an audioTrack and a subtitleTrack that name a
    // track of it win, as does subtitleTrack off; else the hook's, the
    // default's when it names no track of the copy. "" for no subtitles.
    internal static (string Audio, string Subtitle) ChosenTracks(MsfpConfig cfg, Copy copy, Protocol.PlayRequest request)
    {
        bool HasAudio(string id) => (copy.AudioTracks ?? []).Any(a => a.Id == id);
        bool HasSub(string id) => (copy.SubtitleTracks ?? []).Any(s => s.Id == id);
        string audio = request.AudioTrack, subtitle = ""; // P17b: no subtitleTrack in the request since proposal 17
        bool namedAudio = HasAudio(audio), namedSub = subtitle == "off" || HasSub(subtitle);
        if (!namedAudio || !namedSub)
        {
            var (a, s) = (cfg.ChooseTracks ?? MsfpDefaults.ChooseTracks)(copy, request);
            var (da, ds) = MsfpDefaults.ChooseTracks(copy, request);
            if (!namedAudio)
            {
                audio = HasAudio(a ?? "") ? a! : da;
            }

            if (!namedSub)
            {
                subtitle = string.IsNullOrEmpty(s) ? "" : HasSub(s) ? s : ds;
            }
        }

        return (audio, subtitle == "off" ? "" : subtitle);
    }
}
