namespace Msfp.Protocol;

// What an owning server chooses for a play (MSFP playback sections 2.2,
// 2.6 and 2.7): the copy, the audio, and whether it refuses the delivery
// asked of that copy. A copy or a track the request names wins; otherwise
// the choice is the owning server's own, and these are the recommended
// defaults: one that chooses otherwise still conforms.
public static partial class Playback
{
    // Section 2.6's two matches, in order: the same tag (compared without
    // case), then the same base language.
    private static readonly Func<string, string, bool>[] LanguageMatches =
    [
        (tag, want) => string.Equals(tag, want, StringComparison.OrdinalIgnoreCase),
        (tag, want) => BaseLanguage(tag) == BaseLanguage(want),
    ];

    // A tag's base language, lowercase: what comes before its first -.
    private static string BaseLanguage(string tag)
    {
        var dash = tag.IndexOf('-', StringComparison.Ordinal);
        return (dash < 0 ? tag : tag[..dash]).ToLowerInvariant();
    }

    /// <summary>
    /// Playback section 2.6's choice of the copy: the copy
    /// <paramref name="versionId"/> names (required); else, as the
    /// recommended default, the first whose audio (its own sound,
    /// <see cref="PlayCopyAudio.OwnSound"/>) has <paramref name="language"/>,
    /// the same tag looked for in every copy before the same base language;
    /// else the first, the owner's preferred. -1 when there are no copies
    /// (404).
    /// </summary>
    public static int ChooseCopy(IReadOnlyList<PlayCopy> copies, string versionId, string language)
    {
        ArgumentNullException.ThrowIfNull(copies);
        if (copies.Count == 0)
        {
            return -1;
        }

        if (!string.IsNullOrEmpty(versionId))
        {
            for (var i = 0; i < copies.Count; i++)
            {
                if (copies[i].Id == versionId)
                {
                    return i;
                }
            }
        }

        if (!string.IsNullOrEmpty(language))
        {
            foreach (var same in LanguageMatches)
            {
                for (var i = 0; i < copies.Count; i++)
                {
                    if (copies[i].Audio.Any(a => a.OwnSound && a.Language.Length > 0 && same(a.Language, language)))
                    {
                        return i;
                    }
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Playback section 2.6's choice of the audio: the track
    /// <paramref name="track"/> names, when the copy has it (required); else,
    /// as the recommended default, the first of its own sound matching
    /// <paramref name="language"/> (the same tag, then the same base
    /// language); else the default audio: the track the file marks default,
    /// else the first. "" for a copy without audio.
    /// </summary>
    public static string ChooseAudio(IReadOnlyList<PlayCopyAudio> audio, string track, string language)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (!string.IsNullOrEmpty(track) && audio.Any(a => a.Id == track))
        {
            return track;
        }

        if (!string.IsNullOrEmpty(language))
        {
            foreach (var same in LanguageMatches)
            {
                if (audio.FirstOrDefault(a => a.OwnSound && a.Language.Length > 0 && same(a.Language, language)) is { } a)
                {
                    return a.Id;
                }
            }
        }

        return (audio.FirstOrDefault(a => a.Default) ?? audio.FirstOrDefault())?.Id ?? "";
    }

    /// <summary>
    /// Step 7 of playback section 2.2, the refusals MSFP itself names: a 409
    /// delivery-refused for a delivery that isn't file, remux or transcode
    /// (a token read at step 1, core section 8.5); for a remux into another
    /// container than mpegts or mp4 (<see cref="Names.RemuxContainers"/>);
    /// for a transcode codec the owning server doesn't encode
    /// (<paramref name="videoEncoders"/>, <paramref name="audioEncoders"/>,
    /// null: not checked here; the video codec only for a title with a
    /// picture, <paramref name="picture"/>); for file of a copy of several
    /// parts (whatever the purpose); for file or remux of a play whose
    /// copy's bitrate is above <paramref name="capKbps"/>
    /// (<see cref="PlayRequest.BitrateCap"/>, 0: no cap); and for a
    /// burnSubtitle the copy doesn't have. Null when none applies; an owning
    /// server may still refuse for reasons of its own (its policy, what it
    /// can make of the copy).
    /// </summary>
    public static PlayRefusal? RefuseDelivery(PlayRequest request, PlayCopy copy, int capKbps,
        IReadOnlyCollection<string>? videoEncoders = null, IReadOnlyCollection<string>? audioEncoders = null, bool picture = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(copy);
        static PlayRefusal Refuse(string why) => new(409, ProblemDeliveryRefused, why);
        if (request.Delivery.Length > 0 && request.Delivery is not (DeliveryFile or DeliveryRemux or DeliveryTranscode))
        {
            return Refuse("a delivery the owning server doesn't make");
        }

        if (request.Delivery == DeliveryRemux && !Names.RemuxContainers.Contains(request.Container, StringComparer.Ordinal))
        {
            return Refuse("a remux container other than mpegts or mp4");
        }

        if (request.Transcode is { } targets)
        {
            if (picture && targets.VideoCodec.Length > 0 && videoEncoders is not null &&
                !videoEncoders.Contains(targets.VideoCodec, StringComparer.Ordinal))
            {
                return Refuse("a video codec the owning server doesn't encode");
            }

            if (targets.AudioCodec.Length > 0 && audioEncoders is not null && !audioEncoders.Contains(targets.AudioCodec, StringComparer.Ordinal))
            {
                return Refuse("an audio codec the owning server doesn't encode");
            }
        }

        if (request.Delivery == DeliveryFile && copy.Parts > 1)
        {
            return Refuse("a copy of several parts is never delivered as file");
        }

        if (request.Delivery is DeliveryFile or DeliveryRemux && request.Purpose is PurposePlay or "" && capKbps > 0 && copy.BitrateKbps > capKbps)
        {
            return Refuse("the copy's bitrate is above the cap");
        }

        if (request.Transcode is { BurnSubtitle.Length: > 0 } t && !copy.Subtitles.Contains(t.BurnSubtitle, StringComparer.Ordinal))
        {
            return Refuse("burnSubtitle isn't a subtitle track of the copy");
        }

        return null;
    }
}

/// <summary>
/// What the choices of playback section 2.6 need of a copy of a title.
/// </summary>
public sealed record PlayCopy
{
    /// <summary>Its version ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>Its files (parts); 1 for one file.</summary>
    public int Parts { get; init; } = 1;

    /// <summary>Its bitrate, in kbit/s.</summary>
    public int BitrateKbps { get; init; }

    /// <summary>Its audio tracks.</summary>
    public IReadOnlyList<PlayCopyAudio> Audio { get; init; } = [];

    /// <summary>The IDs of its subtitle tracks (s&lt;n&gt;).</summary>
    public IReadOnlyList<string> Subtitles { get; init; } = [];
}

/// <summary>
/// What choosing a copy's audio track needs of it (playback section 2.6).
/// </summary>
/// <param name="Id">Its ID (a&lt;n&gt;).</param>
/// <param name="Language">Its language tag; "" when none.</param>
/// <param name="Role">Its role, a token of the DASH role scheme (main, dub, commentary, description…); "" when none.</param>
/// <param name="Default">Whether the copy's file marks it default.</param>
public sealed record PlayCopyAudio(string Id, string Language = "", string Role = "", bool Default = false)
{
    /// <summary>
    /// Whether the track is the title's own sound (playback section 2.6): no
    /// role, or main or dub; never a commentary, a description or a role the
    /// owning server doesn't know, none of which is chosen by language.
    /// </summary>
    public bool OwnSound => Role is "" or null or "main" or "dub";
}
