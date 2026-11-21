using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Msfp.Stores;

/// <summary>The settings of a <see cref="JsonFileStore"/>; the defaults are the default.</summary>
public sealed record JsonFileStoreOptions
{
    /// <summary>
    /// Where the sealing key is kept: sealing.key in the directory by default.
    /// Made on first use.
    /// </summary>
    public string? KeyFile { get; init; }
}

/// <summary>
/// An <see cref="IStore"/> in JSON files, in one directory: for small hosts,
/// with no database. The same files, in the same form, as the Go engine's
/// store/jsonfile: a directory of one opens in the other.
/// </summary>
/// <remarks>
/// Every commit writes the files it changed whole, each to a temporary file
/// then renamed over the old one, so a file is always either the old or the
/// new; files are mode 0600 and the directory 0700. One process at a time:
/// Open takes a lock on the directory, and fails while another holds it.
/// <para>The files:</para>
/// <list type="table">
/// <item><term>state.json</term><description>the server record, peers, codes, forgotten keys, library IDs, leftovers</description></item>
/// <item><term>peers/&lt;id&gt;.json</term><description>a peer's titles, and the keys of ours it was sent</description></item>
/// <item><term>own.json</term><description>the change log of the host's own catalog, when the engine keeps it</description></item>
/// <item><term>sealing.key</term><description>the key the seeds and secrets are sealed with (KeyFile)</description></item>
/// <item><term>lock</term><description>the lock against a second process</description></item>
/// </list>
/// <para>
/// Seeds and secrets are never in the JSON in clear: they are sealed with
/// AES-256-GCM (<see cref="KeyFileSealer"/>) under a key kept in its own
/// file, sealing.key in the directory unless the options say elsewhere.
/// Beside the data, it keeps them out of a state.json copied alone (a
/// backup, a file sent with a bug report). Elsewhere (another disk, a path
/// that isn't backed up with the data), it also keeps them from whoever
/// copies the directory. A host with a secret store of its own gives
/// MsfpConfig.Sealer instead, and the key file is never made.
/// </para>
/// </remarks>
public sealed partial class JsonFileStore : IStore, ISealerProvider, IDisposable
{
    private const UnixFileMode FileMode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirMode700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string dir;
    private readonly string keyFile;
    private readonly FileStream lockFile;
    private readonly StateStore store;
    private readonly Lazy<ISealer> sealer;

    private JsonFileStore(string dir, string keyFile, FileStream lockFile, State state)
    {
        this.dir = dir;
        this.keyFile = keyFile;
        this.lockFile = lockFile;
        store = new StateStore(state) { Commit = Commit };
        sealer = new Lazy<ISealer>(MakeSealer, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Opens the store in dir, made when it doesn't exist.</summary>
    /// <param name="dir">The directory.</param>
    /// <param name="options">The settings; null for the defaults.</param>
    /// <exception cref="IOException">Another process uses the directory, or it can't be read.</exception>
    public static JsonFileStore Open(string dir, JsonFileStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        var keyFile = options?.KeyFile is { Length: > 0 } k ? k : Path.Combine(dir, "sealing.key");
        MakeDirectory(dir);
        MakeDirectory(Path.Combine(dir, "peers"));
        var lockFile = TakeLock(dir);
        try
        {
            return new JsonFileStore(dir, keyFile, lockFile, Load(dir));
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A <see cref="KeyFileSealer"/> on the key file, made on first use. A
    /// sealer that can't be made fails every seal, and the engine's opening
    /// with it.
    /// </summary>
    public ISealer Sealer => sealer.Value;

    /// <summary>Runs fn on a consistent view.</summary>
    /// <param name="fn">What reads.</param>
    /// <param name="ct">Cancels.</param>
    public ValueTask ReadAsync(Func<IStoreTx, ValueTask> fn, CancellationToken ct) => store.ReadAsync(fn, ct);

    /// <summary>Runs fn in a transaction: all of it is written, or nothing.</summary>
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

    /// <summary>Lets another process open the directory.</summary>
    public void Dispose()
    {
        lockFile.Dispose();
        store.Dispose();
    }

    private ISealer MakeSealer()
    {
        try
        {
            return KeyFileSealer.Open(keyFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new FailedSealer(e);
        }
    }

    private sealed class FailedSealer(Exception error) : ISealer
    {
        public ValueTask<byte[]> SealAsync(byte[] plain, string context, CancellationToken ct) => throw Fail();

        public ValueTask<byte[]> OpenAsync(byte[] @sealed, string context, CancellationToken ct) => throw Fail();

        private MsfpException Fail() => new(MsfpError.Internal, "jsonfile: no sealing key: " + error.Message, error);
    }

    private static void MakeDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, DirMode700);
        }
    }

    // TakeLock takes an exclusive lock on the directory's lock file, or fails
    // at once when another process holds it. .NET's FileShare.None is an
    // exclusive flock where there is one.
    private static FileStream TakeLock(string dir)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode600;
        }
        try
        {
            return new FileStream(Path.Combine(dir, "lock"), options);
        }
        catch (IOException e)
        {
            throw new IOException("jsonfile: another process uses this directory: " + dir, e);
        }
    }

    // The form of the engine's peer IDs, checked all the same before one
    // names a file.
    [GeneratedRegex("^[a-z0-9]{1,64}$")]
    private static partial Regex PeerIdForm();

    // PeerFile is a peer's catalog file.
    private string PeerFile(string id)
    {
        if (!PeerIdForm().IsMatch(id))
        {
            throw new InvalidOperationException($"jsonfile: a peer ID that can't be a file name: \"{id}\"");
        }
        return Path.Combine(dir, "peers", id + ".json");
    }

    // Load reads the files, those absent read as empty. A peer's catalog
    // whose peer isn't in state.json (a crash between the two writes of a
    // commit that deleted it) is removed.
    private static State Load(string dir)
    {
        var s = new State();
        if (ReadJson<StateFile>(Path.Combine(dir, "state.json")) is { } file)
        {
            s.Server = file.Server is { ValueKind: not JsonValueKind.Null } server ? server : null;
            s.Peers = Copy(file.Peers);
            s.Codes = Copy(file.Codes);
            s.Forgotten = Copy(file.Forgotten);
            s.LibraryIds = Copy(file.LibraryIds);
            s.Leftovers = Copy(file.Leftovers);
            s.Groups = Copy(file.Groups);
        }
        if (ReadJson<Own>(Path.Combine(dir, "own.json")) is { } own)
        {
            own.Snapshot ??= new(StringComparer.Ordinal);
            own.Changes ??= [];
            s.Own = own;
        }
        foreach (var path in Directory.EnumerateFiles(Path.Combine(dir, "peers")))
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }
            var id = name[..^".json".Length];
            if (!s.Peers.ContainsKey(id))
            {
                File.Delete(path);
                continue;
            }
            var c = ReadJson<Catalog>(path) ?? new Catalog();
            c.Titles = c.Titles is null ? new(StringComparer.Ordinal) : new(c.Titles, StringComparer.Ordinal);
            c.Sent = c.Sent is null ? new(StringComparer.Ordinal) : new(c.Sent, StringComparer.Ordinal);
            s.Catalogs[id] = c;
        }
        s.Index();
        return s;
    }

    private static Dictionary<string, T> Copy<T>(IDictionary<string, T>? map) =>
        map is null ? new(StringComparer.Ordinal) : new(map, StringComparer.Ordinal);

    // Commit writes what an Update changed: the catalogs first, then
    // state.json, so that a crash between leaves at worst a catalog without
    // its peer, which Load removes.
    private void Commit(State s, Dirty d)
    {
        foreach (var id in d.Catalogs)
        {
            var path = PeerFile(id);
            if (!s.Catalogs.TryGetValue(id, out var c))
            {
                File.Delete(path); // no error when it isn't there
                continue;
            }
            WriteJson(path, new Catalog
            {
                Titles = new SortedDictionary<string, Stored>(c.Titles, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal),
                Sent = c.Sent,
            });
        }
        if (d.Own)
        {
            WriteJson(Path.Combine(dir, "own.json"), s.Own);
        }
        if (d.Records)
        {
            WriteJson(Path.Combine(dir, "state.json"), new StateFile
            {
                Server = s.Server,
                Peers = new(s.Peers, StringComparer.Ordinal),
                Codes = new(s.Codes, StringComparer.Ordinal),
                Forgotten = new(s.Forgotten, StringComparer.Ordinal),
                LibraryIds = new(s.LibraryIds, StringComparer.Ordinal),
                Leftovers = new(s.Leftovers, StringComparer.Ordinal),
                Groups = new(s.Groups, StringComparer.Ordinal),
            });
        }
    }

    // ReadJson reads path; an absent file is null.
    private static T? ReadJson<T>(string path)
        where T : class
    {
        byte[] b;
        try
        {
            b = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(b, MsfpJson.Options);
        }
        catch (JsonException e)
        {
            throw new IOException($"jsonfile: {path}: {e.Message}", e);
        }
    }

    // WriteJson writes v to path atomically: to a temporary file in the same
    // directory, synced, then renamed over path.
    private static void WriteJson<T>(string path, T v)
    {
        var b = JsonSerializer.SerializeToUtf8Bytes(v, MsfpJson.Options);
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, ".tmp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode600;
        }
        try
        {
            using (var f = new FileStream(tmp, options))
            {
                f.Write(b);
                f.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            File.Delete(tmp); // nothing once renamed
        }
    }
}
