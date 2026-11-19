namespace Msfp.Stores;

/// <summary>
/// An <see cref="IStore"/> that keeps everything in memory: for tests, and
/// for runs whose pairings needn't outlive the process. Nothing is written
/// anywhere, so it seals nothing (<see cref="PlainSealer"/>).
/// </summary>
public sealed class MemoryStore : IStore, ISealerProvider, IDisposable
{
    private readonly StateStore store = new(new State());

    /// <summary>The store's sealer: <see cref="PlainSealer"/>, what is kept in memory only needs no sealing.</summary>
    public ISealer Sealer => PlainSealer.Instance;

    /// <summary>Runs fn on a consistent view.</summary>
    /// <param name="fn">What reads.</param>
    /// <param name="ct">Cancels.</param>
    public ValueTask ReadAsync(Func<IStoreTx, ValueTask> fn, CancellationToken ct) => store.ReadAsync(fn, ct);

    /// <summary>Runs fn in a transaction: all of it is kept, or nothing.</summary>
    /// <param name="fn">What reads and writes.</param>
    /// <param name="ct">Cancels the wait for the transaction.</param>
    public ValueTask UpdateAsync(Func<IStoreTx, ValueTask> fn, CancellationToken ct) => store.UpdateAsync(fn, ct);

    /// <summary>Runs fn on a consistent view, and gives what it gave.</summary>
    /// <typeparam name="T">What fn gives.</typeparam>
    /// <param name="fn">What reads.</param>
    /// <param name="ct">Cancels.</param>
    public ValueTask<T> ReadAsync<T>(Func<IStoreTx, ValueTask<T>> fn, CancellationToken ct) => store.ReadAsync(fn, ct);

    /// <summary>Runs fn in a transaction, and gives what it gave once it is kept.</summary>
    /// <typeparam name="T">What fn gives.</typeparam>
    /// <param name="fn">What reads and writes.</param>
    /// <param name="ct">Cancels the wait for the transaction.</param>
    public ValueTask<T> UpdateAsync<T>(Func<IStoreTx, ValueTask<T>> fn, CancellationToken ct) => store.UpdateAsync(fn, ct);

    /// <summary>Frees the store's lock.</summary>
    public void Dispose() => store.Dispose();
}
