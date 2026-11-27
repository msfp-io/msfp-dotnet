using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Msfp.Protocol;

/// <summary>
/// Certificates between servers (MSFP core section 3.2): a server's key vouches
/// for the certificates its address serves, named by pin in its signed
/// hello. A connection to a peer is accepted when its certificate is
/// publicly trusted, or is one the peer named.
/// </summary>
public static class Pins
{
    /// <summary>The most certificates a server may name.</summary>
    public const int Max = 8;

    /// <summary>
    /// How a certificate is named between servers: base64url, without
    /// padding, of the SHA-256 of its public key (its SubjectPublicKeyInfo).
    /// </summary>
    public static string Pin(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Base64UrlText.Encode(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Whether text is a pin as <see cref="Pin"/> writes it.</summary>
    public static bool Valid(string? text) => Base64UrlText.Decode(text, 32) is not null;

    /// <summary>
    /// Keeps the first <see cref="Max"/> valid pins, each once, in the order
    /// sent (MSFP core section 3.2), then sorted, so that two lists compare as sets.
    /// </summary>
    public static List<string> Clean(IEnumerable<string> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        var output = new List<string>();
        foreach (var p in pins)
        {
            if (output.Count == Max)
            {
                break;
            }

            if (Valid(p) && !output.Contains(p))
            {
                output.Add(p);
            }
        }

        output.Sort(StringComparer.Ordinal);
        return output;
    }

    /// <summary>
    /// A certificate check for a connection to a peer: its first
    /// certificate one of <paramref name="pins"/>, or, failing that,
    /// trusted by the system for the host it was dialled by. With
    /// <paramref name="anyCertificate"/>, any certificate passes (tests).
    /// Fits <see cref="SslClientAuthenticationOptions.RemoteCertificateValidationCallback"/>.
    /// </summary>
    public static RemoteCertificateValidationCallback VerifyPeer(IReadOnlyCollection<string> pins, bool anyCertificate = false)
    {
        ArgumentNullException.ThrowIfNull(pins);
        return (_, certificate, _, errors) =>
        {
            if (certificate is null)
            {
                return false;
            }

            if (anyCertificate)
            {
                return true;
            }

            // Only the leaf's: the handshake proves the server holds its
            // private key, not any other's in the chain.
            using var leaf = new X509Certificate2(certificate);
            return pins.Contains(Pin(leaf)) || errors == SslPolicyErrors.None;
        };
    }
}
