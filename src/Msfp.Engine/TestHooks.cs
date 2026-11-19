namespace Msfp;

// What the engine's tests reach (msfp-go's export_test.go).
public sealed partial class MsfpEngine
{
    // A call to a peer as the engine makes it, and the status answered.
    internal async Task<(int Status, byte[] Body)> CallForTestAsync(string id, string call, byte[] body, CancellationToken ct = default)
    {
        var p = await PeerRecordAsync(id, ct).ConfigureAwait(false);
        var r = await CallAsync(p, call, body, ct).ConfigureAwait(false);
        return (r.Status, r.Body);
    }

    // Notes a use of a peer's playback here, as a media request would.
    internal void TouchForTest(string id)
    {
        LivePlayback? pb;
        lock (_plays)
        {
            _plays.ById.TryGetValue(id, out pb);
        }

        if (pb is not null)
        {
            Used(pb);
        }
    }
}
