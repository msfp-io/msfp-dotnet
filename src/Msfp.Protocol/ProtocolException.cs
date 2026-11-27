namespace Msfp.Protocol;

/// <summary>What the protocol layer refuses, by kind: the Go toolkit's error values.</summary>
public enum ProtocolError
{
    /// <summary>A body that isn't I-JSON, or doesn't fit (MSFP Media base 2.2).</summary>
    Json,

    /// <summary>A request or answer whose signature, date, nonce or key is missing or wrong (MSFP core section 6).</summary>
    Signature,

    /// <summary>A valid request from a server whose pairing with this one ended (MSFP core section 7.4).</summary>
    Forgotten,

    /// <summary>Text that isn't a valid pairing code (MSFP core section 5.1).</summary>
    Code,

    /// <summary>A plain http address: MSFP is HTTPS only (MSFP core section 3).</summary>
    PlainHttp,

    /// <summary>An address not in MSFP's one form (MSFP core section 5.2).</summary>
    Address,

    /// <summary>A rotate or revoke body that isn't valid (MSFP core sections 9.3 and 9.4).</summary>
    Statement,

    /// <summary>An address that isn't one of a server that can't be called, where one must be (MSFP core section 5.2).</summary>
    UncallableAddress,

    /// <summary>A peer's certificate neither publicly trusted nor one it named (MSFP core section 3.2).</summary>
    UntrustedCertificate,

    /// <summary>A base path not in MSFP's form (MSFP core section 15).</summary>
    Base,

    /// <summary>A playlist outside what MSFP allows (MSFP Media fetching 3.1).</summary>
    Playlist,

    /// <summary>A PGS track that isn't well-formed (video section on PGS).</summary>
    Pgs,
}

/// <summary>
/// The protocol layer's one exception: <see cref="Error"/> says what was
/// refused. Why exactly is never told to a peer: the receiver answers
/// every refusal of a kind the same way.
/// </summary>
public sealed class ProtocolException : Exception
{
    /// <summary>A refusal of the given kind.</summary>
    public ProtocolException(ProtocolError error)
        : base(DefaultMessage(error))
    {
        Error = error;
    }

    /// <summary>A refusal of the given kind, with its own wording.</summary>
    public ProtocolException(ProtocolError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>What was refused.</summary>
    public ProtocolError Error { get; }

    private static string DefaultMessage(ProtocolError error) => error switch
    {
        ProtocolError.Json => "federation: not a valid body",
        ProtocolError.Signature => "federation: not a valid signature",
        ProtocolError.Forgotten => "federation: a server this one no longer knows",
        ProtocolError.Code => "federation: not a valid pairing code",
        ProtocolError.PlainHttp => "federation: a plain http address; MSFP is HTTPS only",
        ProtocolError.Address => "federation: an address is https://host[:port], with nothing after it",
        ProtocolError.Statement => "federation: not a valid rotation or revocation",
        ProtocolError.UncallableAddress => "federation: a server that can't be called has an address https://msfp-<26 characters>.invalid",
        ProtocolError.UntrustedCertificate => "federation: the other server's certificate is neither publicly trusted nor one it named",
        ProtocolError.Base => "federation: not a base path in MSFP's form",
        ProtocolError.Playlist => "a playlist outside what MSFP allows",
        ProtocolError.Pgs => "playback: not a well-formed PGS track",
        _ => "federation: refused",
    };
}
