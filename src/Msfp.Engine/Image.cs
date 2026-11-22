using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// The image of a local title (MSFP catalog section 4.4, media sections 3.4
// and 4): the image call, served with a ticket of its own that opens that
// image only for 10 minutes, and asked for as a viewer, the image checked
// and re-encoded before the host gets it.
//
// The short tickets of media section 4, of an image call and of a
// previews call (Previews.cs), are kept here, apart from playbacks'.
public sealed partial class MsfpEngine
{
    internal const string CallImage = "image";

    // How long an image ticket works (media section 4).
    private static readonly TimeSpan ImageTicketTime = TimeSpan.FromMinutes(10);

    // What a short ticket opens, for one peer, for 10 minutes: of kind
    // image, one title's image, while the library it was sent in is still
    // shared with it; of kind previews, one copy's sheets (its handle, their
    // count and layout's full sheet), while the title is still sent to it.
    private sealed record ImageTicket(string Kind, string Peer, string Key, DateTimeOffset Expires, string Handle = "", int Sheets = 0,
        int SheetWidth = 0, int SheetHeight = 0);

    // The kinds of short tickets.
    private const string TicketImage = "image";
    private const string TicketPreviews = "previews";

    // The live image tickets, by the SHA-256 of each (hex), and the locks of
    // the image calls made, by peer.
    private readonly Dictionary<string, ImageTicket> _imageTickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _imageLocks = new(StringComparer.Ordinal);

    private static string TicketHash(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));

    // Keeps a new ticket, dropping those that expired.
    private void AddImageTicket(string ticket, ImageTicket it)
    {
        var now = Now();
        lock (_imageTickets)
        {
            foreach (var (h, o) in _imageTickets.ToList())
            {
                if (now >= o.Expires)
                {
                    _imageTickets.Remove(h);
                }
            }

            _imageTickets[TicketHash(ticket)] = it;
        }
    }

    // The live ticket of that text; null for none.
    private ImageTicket? ImageTicketOf(string ticket)
    {
        var h = TicketHash(ticket);
        lock (_imageTickets)
        {
            if (!_imageTickets.TryGetValue(h, out var it))
            {
                return null;
            }

            if (Now() >= it.Expires)
            {
                _imageTickets.Remove(h);
                return null;
            }

            return it;
        }
    }

    // The host's title of that key whose image the peer may be given now: a
    // local film, series, artist or album, of a content part on with it, in
    // a library whose local titles it is shared, sent to it as it would be
    // in titles, with its image shared; null for none.
    private async Task<Title?> SharedImageAsync(PeerRecord p, string key, CancellationToken ct)
    {
        if (_cfg.Catalog is not IImages || p.State != PeerState.Paired || p.Suspended is not null)
        {
            return null;
        }

        var local = LocalOffered(p);
        var libs = Offered(p).Where(local.Contains).Order(StringComparer.Ordinal).ToList();
        if (await HostTitleAsync(key, libs, ct).ConfigureAwait(false) is not { } t)
        {
            return null;
        }

        var raw = await (await SenderForAsync(p, libs, ct).ConfigureAwait(false)).TitleAsync(t).ConfigureAwait(false);
        if (raw is null)
        {
            return null;
        }

        return JsonNode.Parse(raw)?["image"] is JsonValue v && v.TryGetValue<bool>(out var image) && image ? t : null;
    }

    // Answers a paired peer's image call (catalog section 4.4): a ticket of
    // its own for a local title's image.
    private async Task<Answer> ImageAsync(CallRequest req, CancellationToken ct)
    {
        var p = req.Peer;
        if (p is null || p.State != PeerState.Paired)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        // The title is looked up before the check, which takes a plain
        // function: what it says is noted here.
        string? key = null;
        try
        {
            key = StrictJson.ReadRequest(req.Body, ["title"]).GetString("title");
        }
        catch (ProtocolException)
        {
        }

        Title? shared;
        try
        {
            shared = key is null ? null : await SharedImageAsync(p, key, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: the host's title couldn't be read for its image");
            return Answer.Refuse(ProblemType.Internal);
        }

        bool On(string part) => this.On(p, Versions.PartCatalog) && this.On(p, Versions.PartMedia) && this.On(p, part);
        var (title, status, problem) = CatalogRules.CheckImageRequest(On, req.Body, k => k == key && shared is not null);
        if (status != 200)
        {
            return Answer.Refuse(problem);
        }

        var ticket = Base64UrlText.Encode(RandomBytes(32));
        AddImageTicket(ticket, new ImageTicket(TicketImage, p.Id, title, Now() + ImageTicketTime));
        // The caller builds its URL, <stream root><ticket>/image (catalog
        // section 4.4): the answer carries none.
        return Answer.Ok(CatalogRules.WriteImageAnswer(ticket));
    }

    // Serves an image ticket's one URL, <ticket>/image, checked as every
    // media request is (media section 2), and the host's image checked before
    // it leaves: what a peer would refuse isn't sent. Null when ticket isn't
    // an image ticket.
    private async Task<StreamOutcome?> ServeImageAsync(MsfpRequest request, string ticket, string name, IResponseSink output, CancellationToken ct)
    {
        if (ImageTicketOf(ticket) is not { } it)
        {
            return null;
        }

        if (MediaMethod(request, output) is { } refused)
        {
            return refused;
        }

        if (it.Kind == TicketPreviews)
        {
            return await ServePreviewSheetAsync(request, it, name, output, ct).ConfigureAwait(false);
        }

        if (name != "image" || !PlainMediaRequest(request))
        {
            return StreamOutcome.NotFound;
        }

        PeerRecord p;
        try
        {
            p = await PeerRecordAsync(it.Peer, ct).ConfigureAwait(false);
        }
        catch (MsfpException)
        {
            return StreamOutcome.NotFound;
        }

        // The pairing ended, or the title is no longer shared with it: the
        // ticket works no more (media section 4).
        try
        {
            if (await SharedImageAsync(p, it.Key, ct).ConfigureAwait(false) is null)
            {
                return StreamOutcome.NotFound;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: the host's title couldn't be read for its image");
            return StreamOutcome.NotFound;
        }

        var free = await _peerFetchSlots.TakeAsync(it.Peer, ct).ConfigureAwait(false);
        if (free is null)
        {
            return Busy(output, ct);
        }

        try
        {
            byte[] img;
            try
            {
                img = await ((IImages)_cfg.Catalog!).ImageAsync(it.Key, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return MediaError(output, e);
            }

            if (!CatalogRules.TryCheckImage(img, out var format, out var reason))
            {
                _log.LogWarning("msfp: the host's image of a local title isn't one MSFP takes; it isn't sent: {Key} {Reason}", it.Key, reason);
                output.Start(500, "", 0, "");
                return StreamOutcome.Served;
            }

            output.Start(200, "image/" + format, img.Length, "");
            await output.WriteAsync(img, ct).ConfigureAwait(false);
            return StreamOutcome.Served;
        }
        finally
        {
            free();
        }
    }

    /// <summary>
    /// Asks a peer for the image of one of its local titles kept here (a
    /// film, a series, an artist or an album whose description says it has
    /// one: MSFP catalog section 4.4), fetches it under the answer's ticket,
    /// checks it (<see cref="CatalogRules.TryCheckImage(ReadOnlySpan{byte}, out string, out string)"/>)
    /// and re-encodes it, so that only its pixels reach this server's people.
    /// The host keeps it, marked as the owner's, with the title, and asks
    /// again only when the title changes. One image at a time per peer.
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="key">The title's key.</param>
    /// <param name="ct">Cancels.</param>
    /// <returns>The image, and its format, <see cref="CatalogRules.ImageJpeg"/> or <see cref="CatalogRules.ImagePng"/>.</returns>
    /// <exception cref="MsfpException">
    /// <see cref="MsfpError.NotFound"/> (no such title kept, none with an
    /// image, or the peer's refusal), <see cref="MsfpError.NotPaired"/>,
    /// <see cref="MsfpError.Uncallable"/>, <see cref="MsfpError.Suspended"/>,
    /// <see cref="MsfpError.NotSupported"/> (a part off, or a JPEG without a
    /// <see cref="MsfpConfig.SheetEncoder"/> to re-encode it),
    /// <see cref="MsfpError.Unreachable"/> (an answer or an image refused),
    /// <see cref="MsfpError.ServerBusy"/>, <see cref="MsfpError.RateLimited"/>,
    /// <see cref="MsfpError.BackingOff"/>.
    /// </exception>
    public async Task<(byte[] Image, string Format)> TitleImageAsync(string peer, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        if (!TitleKey.TryParse(key, out var tk) || !LocalKey(key) || tk.Kind is not ("movie" or "series" or "artist" or "album"))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        UsablePeer(p, Versions.PartCatalog);
        if (!On(p, Versions.PartMedia) || !On(p, tk.Part))
        {
            throw new MsfpException(MsfpError.NotSupported);
        }

        if (await KeptTitleAsync(peer, key, ct).ConfigureAwait(false) is not (VideoTitle { Image: true } or MusicTitle { Image: true }))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var gate = ImageLock(peer);

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await CallAsync(p, CallImage, JsonOut.Object(o => o.String("title", key)), ct).ConfigureAwait(false);
            if (r.Status != 200)
            {
                throw Refused(p, r);
            }

            var root = PeerStreamRoot(p);
            if (!CatalogRules.TryReadImageAnswer(r.Body, out var ticket))
            {
                throw new MsfpException(MsfpError.Unreachable, "an image answer without a ticket");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SmallReadTime);
            byte[]? img;
            try
            {
                using var res = await RelayGetAsync(p, CatalogRules.ImageUrl(root, ticket), "", cts.Token).ConfigureAwait(false);
                var headers = new ResponseHeaders(res);
                if (headers.GetValues("Content-Encoding").Any(c => !c.Trim().Equals("identity", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new MsfpException(MsfpError.Unreachable, "a coded image");
                }

                switch ((int)res.StatusCode)
                {
                    case 200:
                        break;
                    case 503:
                        throw new MsfpException(MsfpError.ServerBusy);
                    default:
                        throw new MsfpException(MsfpError.NotFound);
                }

                if (res.Content.Headers.ContentLength > CatalogRules.MaxImageBytes)
                {
                    throw new MsfpException(MsfpError.Unreachable, "an image over 1 MiB");
                }

                img = await ReadAtMostAsync(await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), CatalogRules.MaxImageBytes, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                throw new MsfpException(MsfpError.Unreachable, e.Message);
            }

            if (img is null)
            {
                throw new MsfpException(MsfpError.Unreachable, "an image over 1 MiB");
            }

            if (!CatalogRules.TryCheckImage(img, out var format, out var reason))
            {
                throw new MsfpException(MsfpError.Unreachable, reason);
            }

            return (await ReencodeImageAsync(img, format, ct).ConfigureAwait(false), format);
        }
        finally
        {
            gate.Release();
        }
    }

    // Decodes a checked image and encodes its pixels again in its format:
    // JPEGs with the SheetEncoder, PNGs here.
    private async Task<byte[]> ReencodeImageAsync(byte[] img, string format, CancellationToken ct)
    {
        if (format == CatalogRules.ImageJpeg)
        {
            if (_cfg.SheetEncoder is not { } encoder)
            {
                throw new MsfpException(MsfpError.NotSupported, "no SheetEncoder to re-encode a JPEG");
            }

            try
            {
                return await encoder.ReencodeAsync(img, CatalogRules.MaxImageSide, CatalogRules.MaxImageSide, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new MsfpException(MsfpError.Unreachable, e.Message);
            }
        }

        return PngImage.Reencode(img) ?? throw new MsfpException(MsfpError.Unreachable, "not a PNG within 4,096 pixels");
    }

    // A peer's stream root: its address, its prefix and the stream route
    // (media section 1).
    private static string PeerStreamRoot(PeerRecord p) =>
        p.Address + Discovery.Prefix(string.IsNullOrEmpty(p.Base) ? Discovery.DefaultBase : p.Base) + StreamRoute;
}

/// <summary>
/// A PNG decoded and encoded again (msfp-go's image/png round): its pixels
/// only, every ancillary chunk dropped but its transparency, its data
/// unfiltered and compressed anew, nothing after its end.
/// </summary>
internal static class PngImage
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a];

    // Adam7's passes: x start, y start, x step, y step.
    private static readonly (int X, int Y, int Dx, int Dy)[] Passes =
        [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    /// <summary>The PNG re-encoded; null when it isn't a well-formed PNG within <see cref="CatalogRules.MaxImageSide"/>.</summary>
    public static byte[]? Reencode(byte[] png)
    {
        try
        {
            return Run(png);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static byte[]? Run(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
        {
            return null;
        }

        byte[]? ihdr = null, plte = null, trns = null;
        using var idat = new MemoryStream();
        var at = 8;
        var ended = false;
        while (!ended)
        {
            if (at + 12 > png.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            if (length > int.MaxValue || at + 12 + (long)length > png.Length)
            {
                return null;
            }

            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, (int)length);
            if (Crc(png.AsSpan(at + 4, 4 + (int)length)) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + (int)length)))
            {
                return null;
            }

            switch (type)
            {
                case "IHDR" when ihdr is null && at == 8 && length == 13:
                    ihdr = data.ToArray();
                    break;
                case "IHDR":
                    return null;
                case "PLTE" when ihdr is not null && plte is null && idat.Length == 0:
                    plte = data.ToArray();
                    break;
                case "tRNS" when ihdr is not null && trns is null && idat.Length == 0:
                    trns = data.ToArray();
                    break;
                case "IDAT" when ihdr is not null:
                    idat.Write(data);
                    break;
                case "IEND":
                    ended = true;
                    break;
                default:
                    // A critical chunk MSFP's images don't use is refused; an
                    // ancillary one is dropped.
                    if (ihdr is null || char.IsUpper(type[0]))
                    {
                        return null;
                    }

                    break;
            }

            at += 12 + (int)length;
        }

        if (ihdr is null)
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(ihdr);
        var height = BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4));
        int depth = ihdr[8], colour = ihdr[9], interlace = ihdr[12];
        if (width < 1 || height < 1 || width > CatalogRules.MaxImageSide || height > CatalogRules.MaxImageSide ||
            ihdr[10] != 0 || ihdr[11] != 0 || interlace > 1)
        {
            return null;
        }

        var channels = colour switch
        {
            0 when depth is 1 or 2 or 4 or 8 or 16 => 1,
            2 when depth is 8 or 16 => 3,
            3 when depth is 1 or 2 or 4 or 8 => 1,
            4 when depth is 8 or 16 => 2,
            6 when depth is 8 or 16 => 4,
            _ => 0,
        };
        if (channels == 0 || (colour == 3 && plte is not { Length: > 0 and <= 768 }) || (colour == 3 && plte!.Length % 3 != 0))
        {
            return null;
        }

        if (colour != 3)
        {
            plte = null; // a suggested palette, for a picture of its own colours
        }

        if (colour is 4 or 6)
        {
            trns = null;
        }

        var bitsPerPixel = channels * depth;
        var bpp = Math.Max(1, bitsPerPixel / 8);
        var passes = interlace == 0 ? [(0, 0, 1, 1)] : Passes;
        var sizes = passes.Select(q =>
        {
            var w = (width - q.Item1 + q.Item3 - 1) / q.Item3;
            var h = (height - q.Item2 + q.Item4 - 1) / q.Item4;
            return (W: Math.Max(w, 0), H: Math.Max(h, 0));
        }).ToList();
        long total = sizes.Sum(s => s.W == 0 || s.H == 0 ? 0 : (long)s.H * (1 + (((long)s.W * bitsPerPixel) + 7) / 8));

        // The data inflated, no more than its size: what follows is refused.
        var raw = new byte[total];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var n = z.Read(raw, read, raw.Length - read);
                if (n == 0)
                {
                    return null;
                }

                read += n;
            }

            if (z.Read(new byte[1], 0, 1) != 0)
            {
                return null;
            }
        }

        // Unfiltered, line by line, each pass apart; written again with no
        // filter.
        var output = new byte[total];
        var offset = 0;
        foreach (var (w, h) in sizes)
        {
            if (w == 0 || h == 0)
            {
                continue;
            }

            var stride = (int)(((long)w * bitsPerPixel + 7) / 8);
            var previous = new byte[stride];
            for (var y = 0; y < h; y++)
            {
                var filter = raw[offset];
                var line = raw.AsSpan(offset + 1, stride);
                if (!Unfilter(filter, line, previous, bpp))
                {
                    return null;
                }

                output[offset] = 0;
                line.CopyTo(output.AsSpan(offset + 1));
                line.CopyTo(previous);
                offset += 1 + stride;
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(output);
        }

        using var png2 = new MemoryStream();
        png2.Write(Signature);
        Chunk(png2, "IHDR", ihdr);
        if (plte is not null)
        {
            Chunk(png2, "PLTE", plte);
        }

        if (trns is not null)
        {
            Chunk(png2, "tRNS", trns);
        }

        Chunk(png2, "IDAT", compressed.ToArray());
        Chunk(png2, "IEND", []);
        return png2.ToArray();
    }

    private static bool Unfilter(byte filter, Span<byte> line, byte[] previous, int bpp)
    {
        for (var i = 0; i < line.Length; i++)
        {
            int a = i >= bpp ? line[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
            line[i] = filter switch
            {
                0 => line[i],
                1 => (byte)(line[i] + a),
                2 => (byte)(line[i] + b),
                3 => (byte)(line[i] + ((a + b) >> 1)),
                4 => (byte)(line[i] + Paeth(a, b, c)),
                _ => line[i],
            };
        }

        return filter <= 4;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        s.Write(word);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body, 4);
        s.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc(body));
        s.Write(word);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            }

            t[n] = c;
        }

        return t;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xffffffffu;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xff] ^ (c >> 8);
        }

        return c ^ 0xffffffffu;
    }
}
