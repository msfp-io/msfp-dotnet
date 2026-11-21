using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// A hello message, request or answer (core section 9.2), as it is read:
/// what its sender says of the pairing, the versions it speaks, the
/// certificates its address serves and its address; whether it can be
/// called; and the members that go one way only.
/// </summary>
public sealed record Hello
{
    /// <summary>The state "paired".</summary>
    public const string StatePaired = "paired";

    /// <summary>The state "waiting".</summary>
    public const string StateWaiting = "waiting";

    /// <summary>The state "ended".</summary>
    public const string StateEnded = "ended";

    /// <summary>What a hello request from a server that can't be called says of a rotation it refused (core section 12.4, step 3).</summary>
    public const string RotationRefused = "refused";

    private static readonly string[] Members =
        ["state", "core", "parts", "certificates", "address", "nextCommitment", "inbound", "individual", "knownKey", "rotation", "revocation", "callsFailed"];

    private static readonly string[] VersionMembers = ["core", "parts"];

    /// <summary>The sender's view: "paired", "waiting", "ended", or any other string, which counts as "waiting".</summary>
    public string State { get; init; } = "";

    /// <summary>The versions it lists, absent ones read as section 10.2 says.</summary>
    public Versions Versions { get; init; } = Versions.None;

    /// <summary>Its pins, cleaned (<see cref="Pins.Clean"/>): empty when it named none.</summary>
    public IReadOnlyList<string> Certificates { get; init; } = [];

    /// <summary>Its address now; null when it gives none.</summary>
    public string? Address { get; init; }

    /// <summary>Its next key commitment (section 12.5), 32 bytes; null when it gives none.</summary>
    public byte[]? NextCommitment { get; init; }

    /// <summary>It says inbound false: it can't be called (section 7.5). Absent or true reads as false.</summary>
    public bool Uncallable { get; init; }

    /// <summary>Whether it wants the individual relation (section 7.7); null when it doesn't say.</summary>
    public bool? Individual { get; init; }

    /// <summary>
    /// In a request from a server that can't be called only: the key it
    /// checks the receiver's answers against now (section 12.4); null when
    /// it gives none, or a string that isn't a key.
    /// </summary>
    public byte[]? KnownKey { get; init; }

    /// <summary>In a request from a server that can't be called only: "pending" or "refused"; any other string reads as null.</summary>
    public string? RotationState { get; init; }

    /// <summary>In an answer only: the answering server's rotation statement, as it came (an object only), for <see cref="Pull"/>.</summary>
    public JsonObject? Rotation { get; init; }

    /// <summary>In an answer only: the answering server's revocation statement, as it came (an object only).</summary>
    public JsonObject? Revocation { get; init; }

    /// <summary>In an answer only: "my last calls to you couldn't connect" (true only). For the panel, nothing else.</summary>
    public bool CallsFailed { get; init; }

    /// <summary>
    /// Reads a hello's body: a request, or with answer, an answer. A
    /// request without state, or with a member of the wrong type, is a bad
    /// body, as is an answer with one, but for certificates that aren't a
    /// list, which in an answer only count as none (section 3.2). A
    /// nextCommitment that isn't 32 bytes of base64url is a bad body too.
    /// </summary>
    /// <remarks>
    /// The members that go one way only (section 9.2): in a request,
    /// knownKey and rotation are read, and type-checked (strings), only
    /// from a server that says it can't be called; from any other they are
    /// ignored, whatever their value, null included. revocation and
    /// callsFailed are ignored in a request whatever their value. In an
    /// answer, knownKey is ignored; a rotation or a revocation that isn't
    /// an object, and a callsFailed that isn't true, count as absent.
    /// Whether an inbound false goes with an address of the right form
    /// needs the address stored for the sender: <see cref="Uncallables.CheckInbound"/>.
    /// </remarks>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.Json"/>.</exception>
    public static Hello Read(ReadOnlySpan<byte> body, bool answer)
    {
        var m = answer
            ? StrictJson.ReadAnswer(body, keepNestedNulls: true)
            : StrictJson.ReadRequest(body, Members, ["knownKey", "rotation", "revocation", "callsFailed"], VersionMembers);
        if (!answer && !m.ContainsKey("state"))
        {
            throw StrictJson.Bad();
        }

        var state = Str(m, "state") ?? "";
        var versions = Versions.Read(m);
        IReadOnlyList<string> certificates = [];
        if (m.TryGetPropertyValue("certificates", out var certs))
        {
            if (certs is JsonArray list)
            {
                // Entries that aren't pins, strings or not, are skipped.
                certificates = Pins.Clean(list.Select(JsonMembers.AsString).OfType<string>());
            }
            else if (!answer)
            {
                throw StrictJson.Bad();
            }
        }

        var address = Str(m, "address");
        var inbound = true;
        if (m.TryGetPropertyValue("inbound", out var inb))
        {
            inbound = inb?.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => throw StrictJson.Bad(),
            };
        }

        bool? individual = null;
        if (m.TryGetPropertyValue("individual", out var ind))
        {
            individual = ind?.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => throw StrictJson.Bad(),
            };
        }

        byte[]? next = null;
        if (m.ContainsKey("nextCommitment"))
        {
            next = Base64UrlText.Decode(Str(m, "nextCommitment"), 32) ?? throw StrictJson.Bad();
        }

        var h = new Hello
        {
            State = state,
            Versions = versions,
            Certificates = certificates,
            Address = address,
            NextCommitment = next,
            Uncallable = !inbound,
            Individual = individual,
        };
        if (answer)
        {
            return h with
            {
                Rotation = OnlyObject(m, "rotation"),
                Revocation = OnlyObject(m, "revocation"),
                CallsFailed = m.TryGetPropertyValue("callsFailed", out var cf) && cf?.GetValueKind() == System.Text.Json.JsonValueKind.True,
            };
        }

        if (!h.Uncallable)
        {
            return h;
        }

        // From a server that can't be called: read again, its knownKey and
        // rotation kept this time, each a string or a bad body.
        m = StrictJson.ReadRequest(body, Members, ["revocation", "callsFailed"], VersionMembers);
        var known = Str(m, "knownKey");
        var rotation = Str(m, "rotation");
        return h with
        {
            KnownKey = Base64UrlText.Decode(known, Ed25519Strict.KeySize),
            RotationState = rotation is Rotations.StatePending or RotationRefused ? rotation : null,
        };
    }

    // A member that must be a string when present (null counts as the
    // wrong type in a request, refused before; in an answer, as absent).
    private static string? Str(JsonObject m, string name)
    {
        if (!m.TryGetPropertyValue(name, out var v) || v is null)
        {
            return null;
        }

        return JsonMembers.AsString(v) ?? throw StrictJson.Bad();
    }

    private static JsonObject? OnlyObject(JsonObject m, string name) =>
        m.TryGetPropertyValue(name, out var v) && v is JsonObject o ? o : null;

    /// <summary>
    /// Reads the statements of a hello answer, which verified under key,
    /// at now (core sections 12.4 and 12.6): a revocation of that key and a
    /// rotation from it, each null when the answer carries none, or one
    /// that doesn't check (section 18, point 34). They apply in this order:
    /// the revocation, then the rotation.
    /// </summary>
    public Pulled Pull(ReadOnlySpan<byte> key, DateTimeOffset now)
    {
        Revocation? revocation = null;
        Rotation? rotation = null;
        if (Revocation is not null)
        {
            try
            {
                revocation = Rotations.ReadRevoke(Bytes(Revocation), key, now);
            }
            catch (ProtocolException)
            {
            }
        }

        if (Rotation is not null)
        {
            try
            {
                rotation = Rotations.ReadRotate(Bytes(Rotation), key, now);
            }
            catch (ProtocolException)
            {
            }
        }

        return new Pulled(revocation, rotation);
    }

    private static byte[] Bytes(JsonObject o) => System.Text.Encoding.UTF8.GetBytes(o.ToJsonString());
}

/// <summary>What a hello answer to a server that can't be called brings of the answering server's keys, checked.</summary>
/// <param name="Revocation">A revocation of the key the answer verified under; null for none.</param>
/// <param name="Rotation">A rotation from it; null for none.</param>
public sealed record Pulled(Revocation? Revocation, Rotation? Rotation);
