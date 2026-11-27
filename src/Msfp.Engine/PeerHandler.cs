using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Msfp.Protocol;

namespace Msfp;

/// <summary>
/// The engine's own HTTP handlers for calls to peers (MSFP core section 3):
/// connections through the host's dialer or the engine's, which refuses
/// local and other special destinations (section 3.3); certificates
/// publicly trusted, or named by the peer (section 3.2), or any for pair
/// and hello; no redirects, no proxy, no compression, no cookies.
/// </summary>
internal static class PeerHandler
{
    public static SocketsHttpHandler Create(IDialer? dialer, bool allowLoopback, IReadOnlyCollection<string> pins, bool anyCertificate) =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            MaxConnectionsPerServer = 8,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = Pins.VerifyPeer(pins, anyCertificate),
            },
            ConnectCallback = async (context, ct) =>
                dialer is not null
                    ? await dialer.DialAsync(context.DnsEndPoint, ct).ConfigureAwait(false)
                    : await DialAsync(context.DnsEndPoint, allowLoopback, ct).ConfigureAwait(false),
        };

    // Resolves the host, refuses every address of it MSFP refuses, and
    // connects to the first that answers: the address checked is the one
    // connected to, so a name can't be re-resolved in between.
    internal static async ValueTask<Stream> DialAsync(DnsEndPoint endpoint, bool allowLoopback, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(a => Destinations.Refused(a, allowLoopback)))
        {
            throw new MsfpException(MsfpError.RefusedDestination, endpoint.Host);
        }

        Exception? last = null;
        foreach (var a in addresses)
        {
            var socket = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(a, endpoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
        }

        throw last ?? new MsfpException(MsfpError.Unreachable);
    }
}
