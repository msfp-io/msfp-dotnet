using Microsoft.Extensions.Logging;

namespace Msfp;

/// <summary>
/// Events go to the host's notifier one at a time, in order, outside
/// every lock and transaction (decision 10): a thread that adds events
/// while another tells them leaves them to it.
/// </summary>
internal sealed class Notifications(INotifier? notifier, ILogger log)
{
    private readonly object _lock = new();
    private readonly Queue<MsfpEvent> _queue = new();
    private bool _telling;

    public void Tell(IEnumerable<MsfpEvent> events)
    {
        if (notifier is null)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var e in events)
            {
                _queue.Enqueue(e);
            }

            if (_telling)
            {
                return;
            }

            _telling = true;
        }

        while (true)
        {
            MsfpEvent next;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out next!))
                {
                    _telling = false;
                    return;
                }
            }

            try
            {
                notifier.Notify(next);
            }
            catch (Exception e)
            {
                log.LogError(e, "msfp: the host's notifier failed on {Event}", next.GetType().Name);
            }
        }
    }
}
