using System.Buffers.Text;

namespace Msfp.Protocol;

/// <summary>
/// base64url in MSFP's one form (core section 2.2): RFC 4648 section 5,
/// no padding, no white space, the unused bits of the last character zero,
/// so that each value has exactly one text.
/// </summary>
public static class Base64UrlText
{
    /// <summary>Writes bytes as base64url without padding.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    /// <summary>
    /// Decodes base64url in its one form, of exactly <paramref name="length"/>
    /// bytes (any length when it is negative): re-encoding gives the same
    /// text. Null for anything else.
    /// </summary>
    public static byte[]? Decode(string? text, int length = -1)
    {
        if (text is null || text.Length % 4 == 1)
        {
            return null;
        }

        foreach (var c in text)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return null;
            }
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            return null;
        }

        if ((length >= 0 && bytes.Length != length) || Encode(bytes) != text)
        {
            return null;
        }

        return bytes;
    }
}
