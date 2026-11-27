using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// The versions a hello lists (MSFP core section 10): the core versions its
/// sender speaks, and each part's. Two servers use, for the core and for
/// each part, the highest version both list.
/// </summary>
/// <param name="Core">The core versions.</param>
/// <param name="Parts">Each part's versions, by name.</param>
public sealed record Versions(IReadOnlyList<int> Core, IReadOnlyDictionary<string, IReadOnlyList<int>> Parts)
{
    /// <summary>The catalog part.</summary>
    public const string PartCatalog = "catalog";

    /// <summary>The playback part.</summary>
    public const string PartPlayback = "playback";

    /// <summary>The media part.</summary>
    public const string PartMedia = "media";

    /// <summary>The video content part.</summary>
    public const string PartVideo = "video";

    /// <summary>The music content part, which a server without music doesn't list.</summary>
    public const string PartMusic = "music";

    /// <summary>The groups part: groups of servers, their events, mail and pairing through a group.</summary>
    public const string PartGroups = "groups";

    /// <summary>How many entries of each list are read (MSFP core section 10.2).</summary>
    public const int MaxEntries = 8;

    // What each part needs on to be on itself (MSFP core section 10.1).
    private static readonly Dictionary<string, string[]> Needs = new(StringComparer.Ordinal)
    {
        [PartCatalog] = [],
        [PartMedia] = [],
        [PartPlayback] = [PartCatalog, PartMedia],
        [PartVideo] = [PartCatalog, PartPlayback, PartMedia],
        [PartMusic] = [PartCatalog, PartPlayback, PartMedia],
        [PartGroups] = [],
    };

    /// <summary>The parts this library knows, in the order a hello lists them (MSFP core section 10.1's table).</summary>
    public static IReadOnlyList<string> KnownParts { get; } = [PartCatalog, PartPlayback, PartMedia, PartVideo, PartMusic, PartGroups];

    /// <summary>None at all.</summary>
    public static Versions None { get; } = new([], new Dictionary<string, IReadOnlyList<int>>());

    /// <summary>
    /// Reads a hello's core and parts members (null when absent) as MSFP
    /// core section 10.2 says: absent means none. Entries beyond 8 and entries
    /// that aren't non-negative integers are ignored, duplicates count
    /// once. A part this library doesn't know is ignored, whatever its
    /// value. A core or a known part's list that isn't a list, or parts
    /// that isn't an object, is a bad body.
    /// </summary>
    /// <param name="core">The core member, or null.</param>
    /// <param name="parts">The parts member, or null.</param>
    /// <param name="present">Whether each member was present (a present null is a bad body).</param>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.Json"/>.</exception>
    public static Versions Read(JsonNode? core, JsonNode? parts, (bool Core, bool Parts) present)
    {
        var coreList = present.Core ? ReadList(core) : [];
        var partLists = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        if (present.Parts)
        {
            if (parts is not JsonObject obj)
            {
                throw StrictJson.Bad();
            }

            foreach (var (name, raw) in obj)
            {
                if (!Needs.ContainsKey(name))
                {
                    continue; // not a part we know: ignored, whatever it holds
                }

                partLists[name] = ReadList(raw);
            }
        }

        return new Versions(coreList, partLists);
    }

    /// <summary>Reads the core and parts members of an object, as <see cref="Read(JsonNode?, JsonNode?, ValueTuple{bool, bool})"/>.</summary>
    public static Versions Read(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var hasCore = body.TryGetPropertyValue("core", out var core);
        var hasParts = body.TryGetPropertyValue("parts", out var parts);
        return Read(core, parts, (hasCore, hasParts));
    }

    internal static List<int> ReadList(JsonNode? raw)
    {
        if (raw is not JsonArray entries)
        {
            throw StrictJson.Bad();
        }

        var list = new List<int>();
        for (var i = 0; i < entries.Count && i < MaxEntries; i++)
        {
            if (JsonMembers.AsInteger(entries[i]) is { } n && n >= 0 && n <= int.MaxValue && !list.Contains((int)n))
            {
                list.Add((int)n);
            }
        }

        return list;
    }

    private static int? Highest(IReadOnlyList<int> a, IReadOnlyList<int>? b)
    {
        int? best = null;
        if (b is null)
        {
            return null;
        }

        foreach (var n in a)
        {
            if (b.Contains(n) && (best is null || n > best))
            {
                best = n;
            }
        }

        return best;
    }

    /// <summary>
    /// What two servers use between them (MSFP core section 10.3): the core
    /// version, and each part that is on with its version. A part is on
    /// when both list a version of it, and every part it needs is on. Null
    /// when they have no core version in common: they can't talk, and no part is on.
    /// </summary>
    public static Agreement? Agreed(Versions ours, Versions theirs)
    {
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(theirs);
        if (Highest(ours.Core, theirs.Core) is not { } core)
        {
            return null;
        }

        var parts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, list) in ours.Parts)
        {
            if (Highest(list, theirs.Parts.GetValueOrDefault(name)) is { } n)
            {
                parts[name] = n;
            }
        }

        // A part whose need is off is off too, until nothing changes.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var name in parts.Keys.ToList())
            {
                foreach (var need in Needs.GetValueOrDefault(name, []))
                {
                    if (!parts.ContainsKey(need))
                    {
                        parts.Remove(name);
                        changed = true;
                        break;
                    }
                }
            }
        }

        return new Agreement(core, parts);
    }

    /// <summary>Writes the core and parts members, as a hello carries them.</summary>
    public void WriteTo(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.Integers("core", Core.Select(n => (long)n));
        o.Object("parts", p =>
        {
            foreach (var name in KnownParts.Concat(Parts.Keys.Where(k => !KnownParts.Contains(k)).Order(StringComparer.Ordinal)))
            {
                if (Parts.TryGetValue(name, out var list))
                {
                    p.Integers(name, list.Select(n => (long)n));
                }
            }
        });
    }
}

/// <summary>What two servers agreed on (MSFP core section 10.3).</summary>
/// <param name="Core">The core version.</param>
/// <param name="Parts">Each part that is on, with its version.</param>
public sealed record Agreement(int Core, IReadOnlyDictionary<string, int> Parts)
{
    /// <summary>Whether a part is on, at any version (0 is one).</summary>
    public bool On(string part) => Parts.ContainsKey(part);
}
