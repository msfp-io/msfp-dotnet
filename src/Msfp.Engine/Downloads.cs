using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// Downloads kept for their late reports (MSFP Media playing 6): a
// download's progress and stop with at count for 30 days after its play, on
// both sides, across restarts. A store that implements IDownloadsTx keeps
// them; the engine keeps them in memory besides, read back at OpenAsync. A
// peer's download here that was live at a restart is ended then: its ticket
// worked in memory only, its late reports still count.
public sealed partial class MsfpEngine
{
    // Reads back the downloads the store keeps, those past their 30 days
    // removed.
    private async Task LoadDownloadsAsync(CancellationToken ct)
    {
        IReadOnlyList<DownloadRecord> kept = [];
        await _store.ReadAsync(async tx =>
        {
            if (tx is IDownloadsTx d)
            {
                kept = await d.DownloadsAsync().ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
        var now = Now();
        var past = new List<DownloadRecord>();
        lock (_plays)
        {
            lock (_remotes)
            {
                foreach (var d in kept)
                {
                    if (now - d.PlayedAt > Playback.LateReports)
                    {
                        past.Add(d);
                    }
                    else if (d.Viewer)
                    {
                        _downloads[d.Id] = new PastDownload(d.Peer, d.TheirId, d.Title, d.Queue ?? [], d.PlayedAt);
                    }
                    else
                    {
                        _plays.Ended[d.Id] = new EndedPlay(d.Peer, EndReason.Stopped, d.PlayedAt, Playback.PurposeDownload, d.Title,
                            d.Queue ?? [], d.Handle, d.PlayedAt) { LastAt = d.LastAt };
                    }
                }
            }
        }

        await DropDownloadsAsync(past, ct).ConfigureAwait(false);
    }

    // Adds or replaces a download in the store, when it keeps them; a
    // failure is logged: the download is still kept in memory.
    private async Task KeepDownloadAsync(DownloadRecord d, CancellationToken ct)
    {
        d = d with { Queue = d.Queue is null ? null : [.. d.Queue] };
        try
        {
            await _store.UpdateAsync(async tx =>
            {
                if (tx is IDownloadsTx k)
                {
                    await k.PutDownloadAsync(d).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: a download couldn't be kept for its late reports");
        }
    }

    // Removes downloads past their 30 days from the store.
    private async Task DropDownloadsAsync(List<DownloadRecord> past, CancellationToken ct)
    {
        if (past.Count == 0)
        {
            return;
        }

        try
        {
            await _store.UpdateAsync(async tx =>
            {
                if (tx is not IDownloadsTx k)
                {
                    return;
                }

                foreach (var d in past)
                {
                    await k.DeleteDownloadAsync(d.Viewer, d.Id).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: downloads past their late reports couldn't be removed");
        }
    }
}
