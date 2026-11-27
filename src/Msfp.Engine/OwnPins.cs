using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// This server's own certificate pins (MSFP core section 3.2), named in its
// hellos: MsfpConfig.Pins when the host gives them, read from nothing;
// otherwise read by connecting to its own address as a peer would, with SNI
// set to its host name, and naming the pin of the leaf certificate served
// there. During a move, those of its former address too, so that a peer
// still calling it there keeps reaching it.
public sealed partial class MsfpEngine
{
    // How old a reading may be for a hello answer (a peer asking may just
    // have been refused the certificate before a renewal); for a hello this
    // server sends.
    internal static readonly TimeSpan PinsFresh = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan PinsSending = TimeSpan.FromMinutes(1);

    // After a failed reading the next waits PinsRetry, or PinsFirstRetry
    // while no reading has ever worked.
    internal static readonly TimeSpan PinsRetry = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan PinsFirstRetry = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, PinReading> _pinReadings = new(StringComparer.Ordinal);

    // The last good reading at an address, and the last attempt; Gate makes
    // the readings at one address one at a time.
    private sealed class PinReading
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<string> Pins = [];
        public DateTimeOffset? Good;
        public DateTimeOffset? Failed;
    }

    // The pins this server names in a hello it sends, or answers
    // (answering). With a Transport of the host's and no Dialer, nothing
    // connects to read them: none is named.
    private async Task<List<string>> OwnPinsAsync(bool answering, CancellationToken ct)
    {
        if (_cfg.Pins is not null)
        {
            return Pins.Clean(_cfg.Pins);
        }

        if (_cfg.Transport is not null && _cfg.Dialer is null)
        {
            return [];
        }

        var a = OurAddress();
        var addresses = new List<string> { a.Address };
        if (FormerHost() is not null)
        {
            addresses.Add(a.Former!);
        }

        var maxAge = answering ? PinsFresh : PinsSending;
        var pins = new List<string>();
        foreach (var address in addresses.Where(x => !Uncallables.Valid(x)))
        {
            pins.AddRange(await PinsAtAsync(address, maxAge, ct).ConfigureAwait(false));
        }

        return Pins.Clean(pins);
    }

    // The pins served at address, read again when the last reading is older
    // than maxAge; a failed reading keeps the last good one.
    private async Task<List<string>> PinsAtAsync(string address, TimeSpan maxAge, CancellationToken ct)
    {
        PinReading r;
        lock (_pinReadings)
        {
            if (!_pinReadings.TryGetValue(address, out r!))
            {
                r = new PinReading();
                _pinReadings[address] = r;
            }
        }

        await r.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = Now();
            var retry = r.Good is null ? PinsFirstRetry : PinsRetry;
            if ((r.Good is { } good && now - good < maxAge) || (r.Failed is { } failed && now - failed < retry))
            {
                return r.Pins;
            }

            try
            {
                r.Pins = [await ReadPinAsync(address, ct).ConfigureAwait(false)];
                r.Good = now;
                r.Failed = null;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                r.Failed = now;
                _log.LogInformation(e, "msfp: this server's certificate couldn't be read at its address: {Address}", address);
            }

            return r.Pins;
        }
        finally
        {
            r.Gate.Release();
        }
    }

    // Connects to address as a peer would (through the Dialer), with SNI set
    // to its host name (none for an IP address), and gives the pin of the
    // leaf certificate served there.
    private async Task<string> ReadPinAsync(string address, CancellationToken ct)
    {
        var uri = new Uri(address);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var endpoint = new DnsEndPoint(uri.IdnHost, uri.Port);
        var stream = _cfg.Dialer is { } dialer
            ? await dialer.DialAsync(endpoint, cts.Token).ConfigureAwait(false)
            : await PeerHandler.DialAsync(endpoint, _cfg.AllowLoopback, cts.Token).ConfigureAwait(false);
        X509Certificate2? leaf = null;
        await using var tls = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, _, _) =>
        {
            if (cert is not null)
            {
                leaf = new X509Certificate2(cert);
            }

            return true;
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = IPAddress.TryParse(uri.IdnHost, out _) ? "" : uri.IdnHost,
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
        }, cts.Token).ConfigureAwait(false);
        using (leaf)
        {
            return leaf is null ? throw new MsfpException(MsfpError.Unreachable, "no certificate") : Pins.Pin(leaf);
        }
    }
}
