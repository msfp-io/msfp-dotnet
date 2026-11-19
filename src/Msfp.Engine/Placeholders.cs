using System.Text.Json.Serialization;

namespace Msfp;

// Placeholders for types of the toolkit (Go's package wire), which
// Msfp.Protocol will hold: the records keep them, so they are here in
// their JSON form until the toolkit's own replace them.

/// <summary>
/// What a hello lists: the core versions its sender speaks, and each
/// part's. A placeholder of the toolkit's Versions (Go's wire.Versions),
/// in the same JSON.
/// </summary>
public sealed record WireVersions
{
    /// <summary>The core versions.</summary>
    [JsonPropertyName("core")]
    public List<int>? Core { get; set; }

    /// <summary>Each part's versions, by part name.</summary>
    [JsonPropertyName("parts")]
    public Dictionary<string, List<int>>? Parts { get; set; }
}

/// <summary>
/// A rotation statement (MSFP core section 12.2): a server moving from key
/// Old to key New, committing to the key after New. A placeholder of the
/// toolkit's Rotation (Go's wire.Rotation), in the same JSON: Go gives it
/// no member names of its own, so they are its field names.
/// </summary>
public sealed record WireRotation
{
    /// <summary>The key moved from.</summary>
    [JsonPropertyName("Old")]
    public byte[]? Old { get; set; }

    /// <summary>The key moved to.</summary>
    [JsonPropertyName("New")]
    public byte[]? New { get; set; }

    /// <summary>The commitment to the key after New: null for none.</summary>
    [JsonPropertyName("Next")]
    public byte[]? Next { get; set; }

    /// <summary>When it was made, Unix seconds.</summary>
    [JsonPropertyName("Date")]
    public long Date { get; set; }

    /// <summary>The statement's signature with Old.</summary>
    [JsonPropertyName("OldSignature")]
    public byte[]? OldSignature { get; set; }

    /// <summary>The statement's signature with New.</summary>
    [JsonPropertyName("NewSignature")]
    public byte[]? NewSignature { get; set; }
}
