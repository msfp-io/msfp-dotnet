using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// The discovery document (MSFP core section 11): an unsigned JSON document a
/// server may serve at GET /.well-known/msfp, saying where its federation
/// calls are (its base path) and, before any pairing, which versions it
/// speaks. It proves nothing: the versions used are always those of the
/// signed hello.
/// </summary>
public sealed class Discovery
{
    /// <summary>Where a server serves its discovery document.</summary>
    public const string Path = "/.well-known/msfp";

    /// <summary>The base path of the federation calls when a server serves no document, or one without base.</summary>
    public const string DefaultBase = "/api/v1/federation/";

    /// <summary>The most that is read of a discovery document: 16 KiB.</summary>
    public const int MaxDiscovery = 16 << 10;

    private const int MaxBase = 256; // bytes

    // The parts whose lists a document's parts must give as lists (MSFP
    // core section 10.2); any other part is ignored, whatever its value.
    private static readonly string[] KnownParts = ["catalog", "playback", "media", "video", "music"];

    private Discovery(JsonArray? core, JsonObject? parts, string basePath, IReadOnlyList<int>? msfpMedia)
    {
        Core = core;
        Parts = parts;
        Base = basePath;
        MsfpMedia = msfpMedia;
    }

    /// <summary>The versions of MSFP Media that version 0 of the parts carries (MSFP core section 10.1): MSFP Media version 0.</summary>
    public static IReadOnlyList<int> MsfpMediaVersions { get; } = [0];

    /// <summary>
    /// The versions of MSFP Media its part versions carry, read as a list of
    /// hello is (MSFP core section 10.2); null when it gives none. For
    /// information only: the part versions decide (MSFP core section 10.1).
    /// </summary>
    public IReadOnlyList<int>? MsfpMedia { get; }

    /// <summary>Its core member, a list, as sent (read as in a hello, MSFP core section 10.2); null when absent.</summary>
    public JsonArray? Core { get; }

    /// <summary>Its parts member, an object whose known parts are lists, as sent; null when absent.</summary>
    public JsonObject? Parts { get; }

    /// <summary>The versions the document lists, read as a hello's are (MSFP core section 10.2).</summary>
    public Versions Versions => Versions.Read(Core, Parts, (Core is not null, Parts is not null));

    /// <summary>Its base path, <see cref="DefaultBase"/> when it gives none.</summary>
    public string Base { get; }

    /// <summary>
    /// Checks a base path in its one form (MSFP core section 11): at most 256
    /// bytes, an absolute path of segments of A-Z a-z 0-9 . _ ~ -, none
    /// empty, "." or "..", no percent-encoding at all, ending in
    /// /api/v1/federation/. So the path means the same before and after any
    /// normalization.
    /// </summary>
    /// <exception cref="ProtocolException">It isn't such a path (<see cref="ProtocolError.Base"/>).</exception>
    public static void CheckBase(string basePath)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        if (System.Text.Encoding.UTF8.GetByteCount(basePath) > MaxBase || !basePath.StartsWith('/') ||
            !basePath.EndsWith(DefaultBase, StringComparison.Ordinal))
        {
            throw new ProtocolException(ProtocolError.Base);
        }

        var inner = basePath[1..];
        if (inner.EndsWith('/'))
        {
            inner = inner[..^1];
        }

        foreach (var s in inner.Split('/'))
        {
            if (s.Length == 0 || s == "." || s == ".." ||
                !s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '~' or '-'))
            {
                throw new ProtocolException(ProtocolError.Base);
            }
        }
    }

    /// <summary>
    /// The part of a valid base before /api/v1/federation/: the server's
    /// prefix, "" by default (MSFP core section 11). Its stream root is
    /// &lt;address&gt;&lt;prefix&gt;/api/v1/stream/.
    /// </summary>
    public static string Prefix(string basePath)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        return basePath.EndsWith(DefaultBase, StringComparison.Ordinal) ? basePath[..^DefaultBase.Length] : basePath;
    }

    /// <summary>
    /// Reads a discovery document's body. A body that isn't a JSON object, a
    /// core, parts or msfpMedia member of the wrong type, and a base that isn't a string
    /// of <see cref="CheckBase"/>'s form, make it no document: its reader
    /// then uses <see cref="DefaultBase"/>.
    /// </summary>
    /// <exception cref="ProtocolException">No document: <see cref="ProtocolError.Json"/> or <see cref="ProtocolError.Base"/>.</exception>
    public static Discovery Read(ReadOnlySpan<byte> body)
    {
        var m = StrictJson.ReadAnswer(body);
        var core = m.GetArray("core");
        var parts = m.GetObject("parts");
        if (parts is not null)
        {
            foreach (var name in KnownParts)
            {
                _ = parts.GetArray(name);
            }
        }

        IReadOnlyList<int>? msfpMedia = m.TryGetPropertyValue("msfpMedia", out var mm) ? Versions.ReadList(mm) : null;
        var basePath = m.GetString("base");
        if (basePath is not null)
        {
            CheckBase(basePath);
        }

        return new Discovery(core, parts, basePath ?? DefaultBase, msfpMedia);
    }
}
