using System.Globalization;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// A well-formed Range header (media section 2.1): bytes
/// <see cref="First"/> to <see cref="Last"/>, both included (Last -1: to
/// the end), or, when <see cref="Suffix"/> is above 0, the last Suffix
/// bytes. <see cref="Media.TryParseRange"/> reads one.
/// </summary>
/// <param name="First">The first byte asked.</param>
/// <param name="Last">The last byte asked, included; -1 for the end.</param>
/// <param name="Suffix">How many bytes at the end are asked; 0 when First and Last say what is.</param>
public readonly record struct ByteRange(long First, long Last, long Suffix)
{
    /// <summary>
    /// What this range asks of <paramref name="size"/> bytes: start and end,
    /// both included; false when it can't be satisfied (a 416): its first at
    /// or past the end, or any range of an empty file. A last past the end
    /// means the end, a count above the size the whole.
    /// </summary>
    public bool TrySpan(long size, out long start, out long end)
    {
        if (Suffix > 0)
        {
            start = Math.Max(0, size - Suffix);
            end = size - 1;
            return size > 0;
        }

        if (First >= size)
        {
            start = end = 0;
            return false;
        }

        start = First;
        end = size - 1;
        if (Last >= 0 && Last < end)
        {
            end = Last;
        }

        return true;
    }
}

/// <summary>
/// What a viewer's server checks of an answer for the file or a segment
/// (media sections 2.1 and 3.2): its status and headers, null for one absent.
/// </summary>
/// <param name="Status">The answer's status.</param>
/// <param name="ETag">Its ETag header.</param>
/// <param name="ContentRange">Its Content-Range header.</param>
/// <param name="ContentLength">Its Content-Length header.</param>
public sealed record MediaAnswer(int Status, string? ETag = null, string? ContentRange = null, string? ContentLength = null);

// The file and segments, read by byte ranges (media sections 2.1, 3.2
// and 4.2): the Range a request may carry, the answers a viewer's server
// relays, and the cap of a scan ticket.
public static partial class Media
{
    [GeneratedRegex(@"^bytes=(?:([0-9]{1,15})-([0-9]{1,15})?|-([0-9]{1,15}))\z")]
    private static partial Regex RangeForm();

    [GeneratedRegex(@"^bytes ([0-9]+)-([0-9]+)/([0-9]+|\*)\z")]
    private static partial Regex ContentRangeForm();

    /// <summary>
    /// Reads a Range header in one of media section 2.1's three forms,
    /// bytes=&lt;first&gt;-&lt;last&gt; (first ≤ last), bytes=&lt;first&gt;- and
    /// bytes=-&lt;count&gt; (count ≥ 1): numbers of 1 to 15 digits, leading
    /// zeros accepted; false for any other, several ranges and bytes=-
    /// included. A malformed Range is answered 416, without reading the file.
    /// </summary>
    public static bool TryParseRange(string? header, out ByteRange range)
    {
        range = default;
        var m = header is null ? null : RangeForm().Match(header);
        if (m is null || !m.Success)
        {
            return false;
        }

        static long N(Group g) => long.Parse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture);
        if (m.Groups[3].Success)
        {
            range = new ByteRange(0, 0, N(m.Groups[3]));
            return range.Suffix >= 1;
        }

        if (m.Groups[2].Success)
        {
            range = new ByteRange(N(m.Groups[1]), N(m.Groups[2]), 0);
            return range.First <= range.Last;
        }

        range = new ByteRange(N(m.Groups[1]), -1, 0);
        return true;
    }

    /// <summary>A 206's Content-Range: bytes &lt;start&gt;-&lt;end&gt;/&lt;size&gt;.</summary>
    public static string ContentRange(long start, long end, long size) =>
        string.Create(CultureInfo.InvariantCulture, $"bytes {start}-{end}/{size}");

    /// <summary>A 416's Content-Range for the file: bytes */&lt;size&gt;.</summary>
    public static string UnsatisfiedRange(long size) => string.Create(CultureInfo.InvariantCulture, $"bytes */{size}");

    /// <summary>
    /// What a viewer's server checks of an answer for the file (media
    /// sections 2.1, 2.3 and 3.2), to the Range it sent (null or "" for
    /// none, never a malformed one), of a copy of <paramref name="size"/>
    /// bytes and the given etag: a 200 only without Range, with
    /// Content-Length the size; a 206 only to a Range, answering it, its
    /// total the size; an ETag of the etag, strong, on both. Anything else
    /// is a failed fetch, a 416 included: the viewer's server may pass a 416
    /// to a range its viewer asked for on as a 416 of its own. A different
    /// ETag or size means the copy changed.
    /// </summary>
    /// <returns>Why the answer is refused; null when it is relayed.</returns>
    public static string? CheckFileAnswer(string? rangeHeader, long size, string etag, MediaAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var ranged = !string.IsNullOrEmpty(rangeHeader);
        var r = default(ByteRange);
        if (ranged && !TryParseRange(rangeHeader, out r))
        {
            return FileRefused("a malformed Range, never sent");
        }

        if (answer.Status == 200 && ranged)
        {
            return FileRefused("a 200 to a Range");
        }

        if (answer.Status == 206 && !ranged)
        {
            return FileRefused("a 206 without a Range");
        }

        if (answer.Status is not 200 and not 206)
        {
            return FileRefused(string.Create(CultureInfo.InvariantCulture, $"status {answer.Status}"));
        }

        if (answer.ETag != "\"" + etag + "\"")
        {
            return FileRefused("an ETag other than the copy's: the copy changed");
        }

        if (answer.Status == 200)
        {
            return answer.ContentLength != size.ToString(CultureInfo.InvariantCulture)
                ? FileRefused("a Content-Length other than the copy's size")
                : null;
        }

        if (CheckPartial(r, answer, out var total) is { } why)
        {
            return why;
        }

        return total != size ? FileRefused("a total other than the copy's size: the copy changed") : null;
    }

    /// <summary>
    /// What a viewer's server checks of an answer for a segment (media
    /// section 2.1), to the Range it sent (null or "" for none): a 200 is the
    /// whole segment, accepted with or without Range; a 206, only to a
    /// Range, answers it, its total a number or "*" (unknown; never with a
    /// suffix range, whose start can't be checked).
    /// </summary>
    /// <returns>Why the answer is refused; null when it is relayed.</returns>
    public static string? CheckSegmentAnswer(string? rangeHeader, MediaAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.Status == 200)
        {
            return null;
        }

        if (answer.Status != 206)
        {
            return FileRefused(string.Create(CultureInfo.InvariantCulture, $"status {answer.Status}"));
        }

        if (string.IsNullOrEmpty(rangeHeader))
        {
            return FileRefused("a 206 without a Range");
        }

        if (!TryParseRange(rangeHeader, out var r))
        {
            return FileRefused("a malformed Range, never sent");
        }

        return CheckPartial(r, answer, out _);
    }

    /// <summary>
    /// What the owning server sends for a fetch under a scan ticket that has
    /// sent <paramref name="sent"/> bytes so far, a byte sent twice counted
    /// twice, of a copy of <paramref name="size"/> bytes (media section 4.2):
    /// a 206 of start to end, the range cut short at <paramref name="cap"/>
    /// when it would pass it. The cap is the scanBytes the owning server sends
    /// in limits (<see cref="PlaybackLimits.ScanCap"/>: the floor,
    /// <see cref="Playback.ScanBytesFloor"/>, when it sends none). False is a
    /// 416, with <see cref="UnsatisfiedRange"/>: a fetch without Range or
    /// with a malformed one, one that can't be satisfied, or one once the cap
    /// is spent.
    /// </summary>
    public static bool TryScanSpan(long sent, string? rangeHeader, long size, long cap, out long start, out long end)
    {
        start = end = 0;
        if (!TryParseRange(rangeHeader, out var r) || sent >= cap || !r.TrySpan(size, out start, out end))
        {
            start = end = 0;
            return false;
        }

        end = Math.Min(end, start + (cap - sent) - 1);
        return true;
    }

    private static string FileRefused(string why) => "msfp: the file's answer refused: " + why;

    // Checks a 206's Content-Range against the range r asked, and its
    // Content-Length when present: bytes <start>-<end>/<total>, start ≤ end
    // < total; start the range's first, or total − count (or 0); end at
    // most its last; Content-Length end − start + 1. It gives the total, -1
    // for "*". Null when it passes.
    private static string? CheckPartial(ByteRange r, MediaAnswer a, out long total)
    {
        total = -1;
        var m = a.ContentRange is null ? null : ContentRangeForm().Match(a.ContentRange);
        if (m is null || !m.Success)
        {
            return FileRefused("a Content-Range not of the form bytes <start>-<end>/<total>");
        }

        if (!long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
            (m.Groups[3].Value != "*" && !long.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out total)))
        {
            total = -1;
            return FileRefused("a Content-Range number too large");
        }

        if (start > end || (total >= 0 && end >= total))
        {
            return FileRefused("a Content-Range without start ≤ end < total");
        }

        var want = r.First;
        if (r.Suffix > 0)
        {
            if (total < 0)
            {
                return FileRefused("a suffix range answered without its total");
            }

            want = Math.Max(0, total - r.Suffix);
        }

        if (start != want)
        {
            return FileRefused("a Content-Range start other than the range's");
        }

        if (r.Suffix == 0 && r.Last >= 0 && end > r.Last)
        {
            return FileRefused("a Content-Range end past the range's last");
        }

        if (!string.IsNullOrEmpty(a.ContentLength) && a.ContentLength != (end - start + 1).ToString(CultureInfo.InvariantCulture))
        {
            return FileRefused("a Content-Length other than end − start + 1");
        }

        return null;
    }
}
