using Msfp.Protocol;

namespace Msfp;

/// <summary>The discovery document this server serves (core section 11).</summary>
public sealed partial class MsfpEngine
{
    private volatile bool _discovery;

    /// <summary>
    /// This server's discovery document at <c>/.well-known/msfp</c>, on GET
    /// (and HEAD), while it is on (<see cref="MsfpConfig.Discovery"/>,
    /// <see cref="SetDiscovery"/>): where its federation calls are, and the
    /// versions it speaks. Otherwise, and for anything else, the plain 404
    /// (<see cref="MsfpResponse.PlainNotFound"/>): the host answers as for a
    /// path that doesn't exist. Mount it at <c>/.well-known/msfp</c> on the
    /// address's root, whatever the prefix.
    /// </summary>
    public MsfpResponse HandleDiscovery(MsfpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_discovery || request.Target.Split('?', 2)[0] != Discovery.Path || request.Method is not ("GET" or "HEAD"))
        {
            return MsfpResponse.Plain404;
        }

        var body = JsonOut.Object(o =>
        {
            OurVersions().WriteTo(o);
            o.String("base", _cfg.Prefix + Discovery.DefaultBase);
        });
        return new MsfpResponse
        {
            Status = 200,
            Headers = [new("Content-Type", "application/json")],
            Body = request.Method == "HEAD" ? ReadOnlyMemory<byte>.Empty : body,
        };
    }

    /// <summary>Turns the discovery document on or off (off by default: a server that serves it tells anyone it federates).</summary>
    public void SetDiscovery(bool on) => _discovery = on;
}
