using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// Federation bodies are I-JSON (RFC 7493), read as MSFP Media base 2.2 says,
/// both ways: the requests peers send here and the answers they give.
/// </summary>
/// <remarks>
/// <para>A body is UTF-8 without a byte order mark; it holds no surrogate
/// (escaped or not) and no noncharacter; no object has the same member
/// name twice, compared once their escapes are decoded; it nests no value
/// deeper than level 64 (its object at level 0); it holds no number past
/// the range of an IEEE 754 double; it is an object. Member names match exactly: "State" is an
/// unknown member, ignored, never "state".</para>
/// <para>null is never a valid value (MSFP Media base 2.2). A request that
/// holds one in a member its reader knows is refused
/// (<see cref="ReadRequest"/>); unknown members are ignored whatever they
/// hold. In an answer, null at its top fails the whole answer, and inside
/// it reads as the member left out (<see cref="ReadAnswer(ReadOnlySpan{byte})"/>).
/// <see cref="JsonOut"/> writes bodies without any.</para>
/// <para>Readers then take each member with <see cref="JsonMembers"/>: a
/// member of the wrong type is a bad body, an absent one is null.</para>
/// </remarks>
public static class StrictJson
{
    /// <summary>The largest integer MSFP writes, 2^53 − 1.</summary>
    public const long MaxInteger = 9_007_199_254_740_991;

    /// <summary>
    /// How deeply a body may nest (MSFP Media base 2.2): the body's object at
    /// level 0, a member's value or an array's entry one level deeper than
    /// what holds it; a value at level 65 is refused.
    /// </summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// Reads a request, a JSON object: <paramref name="ignored"/> members
    /// are dropped first, whatever their value; null is refused anywhere
    /// inside the <paramref name="known"/> members, but for the
    /// <paramref name="lenient"/> ones, refused only when null themselves
    /// (their reader judges what they hold). Other members are kept, not
    /// checked: an unknown member is ignored whatever it holds.
    /// </summary>
    /// <exception cref="ProtocolException">The body isn't a valid request (<see cref="ProtocolError.Json"/>).</exception>
    public static JsonObject ReadRequest(ReadOnlySpan<byte> body, IEnumerable<string> known,
        IEnumerable<string>? ignored = null, IEnumerable<string>? lenient = null)
    {
        ArgumentNullException.ThrowIfNull(known);
        var obj = ParseObject(body, nulls: true);
        if (ignored is not null)
        {
            foreach (var name in ignored)
            {
                obj.Remove(name);
            }
        }

        var lenientSet = lenient is null ? [] : new HashSet<string>(lenient, StringComparer.Ordinal);
        foreach (var name in known)
        {
            if (!obj.TryGetPropertyValue(name, out var value))
            {
                continue;
            }

            if (value is null || (!lenientSet.Contains(name) && HoldsNull(value)))
            {
                throw Bad();
            }
        }

        return obj;
    }

    /// <summary>
    /// Reads a federation answer: an object; null at its top refused, null
    /// inside it read as the member left out (the reader checks each
    /// member's type, as each call's section says).
    /// </summary>
    /// <exception cref="ProtocolException">The body isn't a valid answer.</exception>
    public static JsonObject ReadAnswer(ReadOnlySpan<byte> body) => ReadAnswer(body, keepNestedNulls: false);

    /// <summary>
    /// Reads a federation answer, null at its top refused; with
    /// <paramref name="keepNestedNulls"/>, the nulls inside it are kept for
    /// a reader that judges them itself (a hello's version lists, which
    /// refuse a known part that is null, and ignore a null entry).
    /// </summary>
    /// <exception cref="ProtocolException">The body isn't a valid answer.</exception>
    public static JsonObject ReadAnswer(ReadOnlySpan<byte> body, bool keepNestedNulls)
    {
        var obj = ParseObject(body, nulls: true);
        foreach (var (_, value) in obj)
        {
            if (value is null)
            {
                throw Bad();
            }
        }

        if (!keepNestedNulls)
        {
            DropNulls(obj);
        }

        return obj;
    }

    /// <summary>
    /// Reads an entry of an answer's list (a title, a library), already
    /// parsed as part of the answer: an object, null anywhere read as the
    /// member left out. Null when it isn't an object (the entry is skipped).
    /// </summary>
    public static JsonObject? ReadEntry(JsonNode? entry)
    {
        if (entry is not JsonObject obj)
        {
            return null;
        }

        DropNulls(obj);
        return obj;
    }

    /// <summary>Reads an entry of an answer's list from its bytes, as <see cref="ReadEntry(JsonNode?)"/>.</summary>
    /// <exception cref="ProtocolException">The bytes aren't I-JSON, or not an object.</exception>
    public static JsonObject ReadEntry(ReadOnlySpan<byte> raw)
    {
        var obj = ParseObject(raw, nulls: true);
        DropNulls(obj);
        return obj;
    }

    /// <summary>
    /// Parses a body as I-JSON into a tree whose top is an object, with or
    /// without null allowed as a value.
    /// </summary>
    /// <exception cref="ProtocolException">It isn't I-JSON, or isn't an object.</exception>
    public static JsonObject ParseObject(ReadOnlySpan<byte> body, bool nulls)
    {
        // Every MSFP body, request or answer, is an object (MSFP
        // core section 8.1): "null", a list or a string isn't one.
        if (Parse(body, nulls) is JsonObject obj)
        {
            return obj;
        }

        throw Bad();
    }

    /// <summary>Parses any I-JSON value (strictly, as a body is read).</summary>
    /// <exception cref="ProtocolException">It isn't I-JSON.</exception>
    public static JsonNode? Parse(ReadOnlySpan<byte> body, bool nulls)
    {
        if (body.StartsWith("\xef\xbb\xbf"u8) || !System.Text.Unicode.Utf8.IsValid(body) || !EscapesOk(body))
        {
            throw Bad();
        }

        try
        {
            // First pass: duplicates, null, depth, numbers, the grammar. The
            // reader's MaxDepth counts containers (65 of them reach a
            // container at level 64), so a scalar at level 65 is refused
            // here, by its own depth.
            var reader = new Utf8JsonReader(body, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = MaxDepth + 1,
            });
            var names = new Stack<HashSet<string>?>();
            while (reader.Read())
            {
                if (reader.TokenType is not (JsonTokenType.PropertyName or JsonTokenType.EndObject or JsonTokenType.EndArray) &&
                    reader.CurrentDepth > MaxDepth)
                {
                    throw Bad(); // a value nested too deep
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        names.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        names.Push(null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        names.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (names.Peek() is not { } seen || !seen.Add(reader.GetString()!))
                        {
                            throw Bad(); // the same member twice
                        }

                        break;
                    case JsonTokenType.Null when !nulls:
                        throw Bad(); // null is never a valid value
                    case JsonTokenType.Number when !InDoubleRange(ref reader):
                        throw Bad(); // past a double's range
                }
            }

            return JsonNode.Parse(body, null, new JsonDocumentOptions { MaxDepth = MaxDepth + 1 });
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            throw Bad();
        }
    }

    // Whether the number at the reader is within an IEEE 754 double's range
    // (MSFP Media base 2.2): one whose magnitude rounds above the largest
    // double is refused; one too small for a double counts as 0, and
    // precision is no reason to refuse.
    private static bool InDoubleRange(ref Utf8JsonReader reader)
    {
        if (reader.TryGetDouble(out var d))
        {
            return double.IsFinite(d);
        }

        var text = System.Text.Encoding.UTF8.GetString(reader.ValueSpan); // a body is one span
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d);
    }

    /// <summary>
    /// Whether a JSON number's text is an integer as MSFP Media base 2.2
    /// says: no fraction, no exponent, no leading zero, not -0, within
    /// ±(2^53 − 1).
    /// </summary>
    public static bool IsInteger(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var digits = text.StartsWith('-') ? text[1..] : text;
        if (digits.Length == 0 || digits.Length > 16 || text == "-0" || (digits.Length > 1 && digits[0] == '0'))
        {
            return false;
        }

        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= MaxInteger;
    }

    /// <summary>Whether a node holds null anywhere.</summary>
    internal static bool HoldsNull(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return true;
            case JsonObject o:
                foreach (var (_, v) in o)
                {
                    if (HoldsNull(v))
                    {
                        return true;
                    }
                }

                return false;
            case JsonArray a:
                foreach (var v in a)
                {
                    if (HoldsNull(v))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    // Removes the null members of every object in node (null entries of
    // lists stay: an entry of the wrong type, which the reader skips or
    // refuses).
    private static void DropNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var name in o.Where(p => p.Value is null).Select(p => p.Key).ToList())
                {
                    o.Remove(name);
                }

                foreach (var (_, v) in o)
                {
                    DropNulls(v);
                }

                break;
            case JsonArray a:
                foreach (var v in a)
                {
                    DropNulls(v);
                }

                break;
        }
    }

    internal static ProtocolException Bad() => new(ProtocolError.Json);

    // A body's characters and \u escapes: no surrogate unless an escaped
    // pair makes one character of it, no noncharacter. Unescaped
    // surrogates are invalid UTF-8, refused before.
    private static bool EscapesOk(ReadOnlySpan<byte> b)
    {
        for (var i = 0; i < b.Length;)
        {
            var c = b[i];
            if (c == '\\')
            {
                if (i + 1 >= b.Length)
                {
                    return false;
                }

                if (b[i + 1] != 'u')
                {
                    i += 2;
                    continue;
                }

                if (!Hex4(b[(i + 2)..], out var r))
                {
                    return false;
                }

                i += 6;
                if (r is >= 0xdc00 and <= 0xdfff)
                {
                    return false; // a low surrogate alone
                }

                if (r is >= 0xd800 and <= 0xdbff)
                {
                    if (i + 6 > b.Length || b[i] != '\\' || b[i + 1] != 'u' || !Hex4(b[(i + 2)..], out var lo) ||
                        lo is < 0xdc00 or > 0xdfff)
                    {
                        return false;
                    }

                    i += 6;
                    r = 0x10000 + ((r - 0xd800) << 10) + (lo - 0xdc00);
                }

                if (Noncharacter(r))
                {
                    return false;
                }
            }
            else if (c < 0x80)
            {
                i++;
            }
            else
            {
                System.Text.Rune.DecodeFromUtf8(b[i..], out var rune, out var n);
                if (Noncharacter(rune.Value))
                {
                    return false;
                }

                i += n;
            }
        }

        return true;
    }

    private static bool Hex4(ReadOnlySpan<byte> b, out int r)
    {
        r = 0;
        if (b.Length < 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            var d = b[i] switch
            {
                >= (byte)'0' and <= (byte)'9' => b[i] - '0',
                >= (byte)'a' and <= (byte)'f' => b[i] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b[i] - 'A' + 10,
                _ => -1,
            };
            if (d < 0)
            {
                return false;
            }

            r = (r << 4) | d;
        }

        return true;
    }

    // U+FDD0 to U+FDEF, and the last two code points of every plane.
    internal static bool Noncharacter(int r) => r is >= 0xfdd0 and <= 0xfdef || (r & 0xfffe) == 0xfffe;
}

/// <summary>
/// Typed members of a body read by <see cref="StrictJson"/>: each returns
/// null when the member is absent, and throws <see cref="ProtocolException"/>
/// (<see cref="ProtocolError.Json"/>) when it holds a value of the wrong
/// type (MSFP Media base 2.2).
/// </summary>
public static class JsonMembers
{
    /// <summary>A string member.</summary>
    public static string? GetString(this JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var v) && v is not null ? AsString(v) ?? throw StrictJson.Bad() : null;

    /// <summary>An integer member (MSFP Media base 2.2's form).</summary>
    public static long? GetInteger(this JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var v) && v is not null ? AsInteger(v) ?? throw StrictJson.Bad() : null;

    /// <summary>A boolean member.</summary>
    public static bool? GetBool(this JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var v) || v is null)
        {
            return null;
        }

        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw StrictJson.Bad(),
        };
    }

    /// <summary>An object member.</summary>
    public static JsonObject? GetObject(this JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var v) && v is not null ? v as JsonObject ?? throw StrictJson.Bad() : null;

    /// <summary>A list member.</summary>
    public static JsonArray? GetArray(this JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var v) && v is not null ? v as JsonArray ?? throw StrictJson.Bad() : null;

    /// <summary>A list of strings; an entry of another type is a bad body.</summary>
    public static List<string>? GetStrings(this JsonObject obj, string name)
    {
        var list = obj.GetArray(name);
        return list?.Select(e => e is null ? throw StrictJson.Bad() : AsString(e) ?? throw StrictJson.Bad()).ToList();
    }

    /// <summary>The member's bytes, base64url in its one form, of the given length; null when absent.</summary>
    /// <exception cref="ProtocolException">Not a string, or not such bytes.</exception>
    public static byte[]? GetBytes(this JsonObject obj, string name, int length)
    {
        var s = obj.GetString(name);
        return s is null ? null : Base64UrlText.Decode(s, length) ?? throw StrictJson.Bad();
    }

    /// <summary>A node as a string, or null when it is another type.</summary>
    public static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>A node as an integer of MSFP Media base 2.2's form, or null when it is anything else.</summary>
    public static long? AsInteger(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        var text = v.ToJsonString();
        return StrictJson.IsInteger(text) ? long.Parse(text, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>A node's raw JSON text (its bytes as read, for a value; written again, for an object or list).</summary>
    public static string Raw(JsonNode? node) => node?.ToJsonString() ?? "null";
}
