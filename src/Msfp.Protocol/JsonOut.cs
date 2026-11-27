using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// Writes a federation body: compact JSON, its members in the order they
/// are written (the order of the specification's tables), and never a
/// null (MSFP Media base 2.2): a member given null is left out, as Go's
/// omitempty would.
/// </summary>
/// <example>
/// <code>
/// byte[] body = JsonOut.Object(o => o.String("name", "Server A").Integer("date", 1800000000));
/// </code>
/// </example>
public sealed class JsonOut
{
    private static readonly JsonWriterOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };

    private readonly Utf8JsonWriter _w;

    private JsonOut(Utf8JsonWriter w) => _w = w;

    /// <summary>The bytes of one object, its members written by <paramref name="fill"/>.</summary>
    public static byte[] Object(Action<JsonOut> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            fill(new JsonOut(w));
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A string member; left out when null.</summary>
    public JsonOut String(string name, string? value)
    {
        if (value is not null)
        {
            _w.WriteString(name, value);
        }

        return this;
    }

    /// <summary>An integer member; left out when null.</summary>
    public JsonOut Integer(string name, long? value)
    {
        if (value is { } n)
        {
            _w.WriteNumber(name, n);
        }

        return this;
    }

    /// <summary>A boolean member; left out when null.</summary>
    public JsonOut Bool(string name, bool? value)
    {
        if (value is { } b)
        {
            _w.WriteBoolean(name, b);
        }

        return this;
    }

    /// <summary>Bytes as base64url (MSFP core section 2.2); left out when null.</summary>
    public JsonOut Bytes(string name, ReadOnlySpan<byte> value, bool present = true)
    {
        if (present)
        {
            _w.WriteString(name, Base64UrlText.Encode(value));
        }

        return this;
    }

    /// <summary>Bytes as base64url; left out when null.</summary>
    public JsonOut Bytes(string name, byte[]? value) => value is null ? this : Bytes(name, value, true);

    /// <summary>A list of strings; left out when null.</summary>
    public JsonOut Strings(string name, IEnumerable<string>? values)
    {
        if (values is not null)
        {
            _w.WriteStartArray(name);
            foreach (var v in values)
            {
                ArgumentNullException.ThrowIfNull(v, nameof(values)); // a null in a list is an error, never written
                _w.WriteStringValue(v);
            }

            _w.WriteEndArray();
        }

        return this;
    }

    /// <summary>A list of integers; left out when null.</summary>
    public JsonOut Integers(string name, IEnumerable<long>? values)
    {
        if (values is not null)
        {
            _w.WriteStartArray(name);
            foreach (var v in values)
            {
                _w.WriteNumberValue(v);
            }

            _w.WriteEndArray();
        }

        return this;
    }

    /// <summary>An object member written by <paramref name="fill"/>; left out when fill is null.</summary>
    public JsonOut Object(string name, Action<JsonOut>? fill)
    {
        if (fill is not null)
        {
            _w.WriteStartObject(name);
            fill(this);
            _w.WriteEndObject();
        }

        return this;
    }

    /// <summary>A list of objects, each written by <paramref name="fill"/>; left out when items is null.</summary>
    public JsonOut Objects<T>(string name, IEnumerable<T>? items, Action<JsonOut, T> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (items is not null)
        {
            _w.WriteStartArray(name);
            foreach (var item in items)
            {
                _w.WriteStartObject();
                fill(this, item);
                _w.WriteEndObject();
            }

            _w.WriteEndArray();
        }

        return this;
    }

    /// <summary>A member holding JSON already made (an object a peer sent, kept as it came); left out when null.</summary>
    /// <exception cref="ArgumentException">The JSON holds a null.</exception>
    public JsonOut Node(string name, JsonNode? value)
    {
        if (value is not null)
        {
            if (StrictJson.HoldsNull(value))
            {
                throw new ArgumentException("federation: a null in a body", nameof(value));
            }

            _w.WritePropertyName(name);
            value.WriteTo(_w);
        }

        return this;
    }

    /// <summary>A member holding raw JSON bytes, written as they are; left out when null.</summary>
    public JsonOut Raw(string name, byte[]? json)
    {
        if (json is not null)
        {
            _w.WritePropertyName(name);
            _w.WriteRawValue(json, skipInputValidation: false);
        }

        return this;
    }
}
