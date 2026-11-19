using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Msfp;

/// <summary>
/// Marks a property left out of a record's JSON when it is empty: null, "",
/// false, 0, or an empty collection. It is Go's <c>omitempty</c> (and
/// <c>omitzero</c> for times, which are null for "never" here), so that the
/// shipped stores write the records as the Go engine's do.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OmitEmptyAttribute : Attribute
{
}

/// <summary>
/// The JSON the shipped stores read and write the records with: the same
/// member names and forms as the Go engine's, so that a directory of the
/// Go JSON file store opens here, and the other way round.
/// </summary>
public static class MsfpJson
{
    /// <summary>
    /// The options: times as Go writes them (RFC 3339 in UTC, "Z", the
    /// fraction without trailing zeros), byte arrays in standard base64,
    /// members marked <see cref="OmitEmptyAttribute"/> left out when empty.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = MakeOptions();

    private static JsonSerializerOptions MakeOptions()
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitEmpty } },
            Converters = { new UtcTimeConverter() },
        };
        options.MakeReadOnly();
        return options;
    }

    // OmitEmpty gives the properties marked OmitEmpty Go's omitempty.
    private static void OmitEmpty(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }
        foreach (var property in info.Properties)
        {
            if (property.AttributeProvider?.IsDefined(typeof(OmitEmptyAttribute), inherit: false) == true)
            {
                property.ShouldSerialize = static (_, value) => !IsEmpty(value);
            }
        }
    }

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        bool b => !b,
        int i => i == 0,
        long l => l == 0,
        double d => d == 0,
        ICollection c => c.Count == 0,
        PeerState s => s.Value.Length == 0,
        Inbound s => s.Value.Length == 0,
        _ => false,
    };
}

/// <summary>
/// Times as Go's encoding/json writes a UTC <c>time.Time</c>
/// (RFC 3339 with nanoseconds, trailing zeros dropped, "Z"). Go keeps
/// nanoseconds; .NET keeps 100-nanosecond ticks, so the last two digits of
/// a Go time are dropped when read.
/// </summary>
internal sealed class UtcTimeConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? throw new JsonException("a time that is null");
        return Parse(text);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value));

    internal static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset Parse(string text)
    {
        // Go writes up to nine digits of fraction; .NET reads seven.
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }
            if (end - dot - 1 > 7)
            {
                text = string.Concat(text.AsSpan(0, dot + 8), text.AsSpan(end));
            }
        }
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
        {
            throw new JsonException("not a time: " + text);
        }
        return t.ToUniversalTime();
    }
}

/// <summary>
/// An <see cref="OwnChange"/> as Go's stores write it:
/// <c>{"seq","key","title","at"}</c>, its title as
/// <see cref="TitleJson.Marshal"/> writes it, absent for a title gone.
/// </summary>
internal sealed class OwnChangeConverter : JsonConverter<OwnChange>
{
    public override OwnChange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("an own change that isn't an object");
        }
        long seq = 0;
        var key = "";
        Title? title = null;
        var at = default(DateTimeOffset);
        foreach (var member in root.EnumerateObject())
        {
            switch (member.Name)
            {
                case "seq":
                    seq = member.Value.GetInt64();
                    break;
                case "key":
                    key = member.Value.GetString() ?? "";
                    break;
                case "title" when member.Value.ValueKind != JsonValueKind.Null:
                    title = TitleJson.Unmarshal(member.Value);
                    break;
                case "at":
                    at = UtcTimeConverter.Parse(member.Value.GetString() ?? "");
                    break;
            }
        }
        return new OwnChange(seq, key, title, at);
    }

    public override void Write(Utf8JsonWriter writer, OwnChange value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("seq", value.Seq);
        writer.WriteString("key", value.Key);
        if (value.Title is not null)
        {
            writer.WritePropertyName("title");
            TitleJson.ToElement(value.Title).WriteTo(writer);
        }
        writer.WriteString("at", UtcTimeConverter.Format(value.At));
        writer.WriteEndObject();
    }
}

/// <summary>
/// Reads and writes a string-valued type (<see cref="PeerState"/>,
/// <see cref="Inbound"/>) as its plain string.
/// </summary>
internal abstract class StringValueConverter<T> : JsonConverter<T>
    where T : struct
{
    protected abstract T Make(string value);

    protected abstract string ValueOf(T value);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Make(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ValueOf(value));
}

internal sealed class PeerStateConverter : StringValueConverter<PeerState>
{
    protected override PeerState Make(string value) => new(value);

    protected override string ValueOf(PeerState value) => value.Value;
}

internal sealed class InboundConverter : StringValueConverter<Inbound>
{
    protected override Inbound Make(string value) => new(value);

    protected override string ValueOf(Inbound value) => value.Value;
}
