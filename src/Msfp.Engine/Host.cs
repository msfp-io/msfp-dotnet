using System.Net;
using Msfp.Protocol;

namespace Msfp;

// What the host implements for the engine: its storage, its catalog, its
// playback and media, sealing, and optionally a dialer, a notifier and a
// sheet encoder (the clock is a TimeProvider). Only IStore is required: the
// shipped stores (Msfp.Stores.MemoryStore, Msfp.Stores.JsonFileStore) bring their own ISealer, and
// everything else has a default or may be left out.
//
// A host that leaves out ICatalog, IPlayback and IMedia offers nothing and
// plays nothing here: it only watches what its peers share.
//
// Host callbacks return ValueTask, so that synchronous data costs nothing,
// and take a CancellationToken last. Errors are exceptions: an
// MsfpException with its code where the engine reads one (host.md), any
// other exception fails the engine's action (toward a peer, a signed 500).

/// <summary>
/// Keeps the engine's records. The engine owns the meaning of every field;
/// the store keeps them and gives them back as they were.
/// </summary>
/// <remarks>
/// <see cref="ReadAsync"/> gives fn a consistent view.
/// <see cref="UpdateAsync"/> runs fn in a transaction: everything fn wrote
/// is kept, or nothing (when fn throws, or the commit fails). Two Updates
/// never interleave their effects: one at a time is enough. The engine
/// never calls a peer, nor the host's media, inside fn, and keeps fn short.
/// The transaction is valid only inside fn.
/// <para>
/// Any exception a store throws fails the engine's action; toward a peer,
/// it is a signed 500. Lookups that find nothing return null (or false),
/// they don't throw.
/// </para>
/// </remarks>
public interface IStore
{
    /// <summary>Runs fn on a consistent view; its writes fail.</summary>
    /// <param name="fn">What reads.</param>
    /// <param name="ct">Cancels the wait (not fn once it runs: fn is given the same token).</param>
    ValueTask ReadAsync(Func<IStoreTx, ValueTask> fn, CancellationToken ct);

    /// <summary>Runs fn in a transaction: all of it is kept, or nothing.</summary>
    /// <param name="fn">What reads and writes.</param>
    /// <param name="ct">Cancels the wait for the transaction.</param>
    ValueTask UpdateAsync(Func<IStoreTx, ValueTask> fn, CancellationToken ct);

    /// <summary>
    /// Runs fn on a consistent view, and gives what it gave. By default, on
    /// <see cref="ReadAsync(Func{IStoreTx, ValueTask}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="T">What fn gives.</typeparam>
    /// <param name="fn">What reads.</param>
    /// <param name="ct">Cancels the wait.</param>
    async ValueTask<T> ReadAsync<T>(Func<IStoreTx, ValueTask<T>> fn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fn);
        T result = default!;
        await ReadAsync(async tx => { result = await fn(tx).ConfigureAwait(false); }, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Runs fn in a transaction, and gives what it gave once the transaction
    /// is kept. By default, on
    /// <see cref="UpdateAsync(Func{IStoreTx, ValueTask}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="T">What fn gives.</typeparam>
    /// <param name="fn">What reads and writes.</param>
    /// <param name="ct">Cancels the wait.</param>
    async ValueTask<T> UpdateAsync<T>(Func<IStoreTx, ValueTask<T>> fn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fn);
        T result = default!;
        await UpdateAsync(async tx => { result = await fn(tx).ConfigureAwait(false); }, ct).ConfigureAwait(false);
        return result;
    }
}

/// <summary>
/// What the engine reads and writes inside a transaction. Getters return
/// copies: changing what they return changes nothing until it is put back.
/// </summary>
/// <remarks>
/// Its calls take no CancellationToken: a store that needs one for its
/// queries keeps the one given to ReadAsync or UpdateAsync.
/// </remarks>
public interface IStoreTx
{
    /// <summary>The one server record; null on the very first start.</summary>
    ValueTask<ServerRecord?> ServerAsync();

    /// <summary>Puts the server record.</summary>
    /// <param name="record">The record.</param>
    ValueTask PutServerAsync(ServerRecord record);

    /// <summary>Every peer, in any state.</summary>
    ValueTask<IReadOnlyList<PeerRecord>> PeersAsync();

    /// <summary>One peer; null for none.</summary>
    /// <param name="id">The peer's ID.</param>
    ValueTask<PeerRecord?> PeerAsync(string id);

    /// <summary>Finds a peer by its current key only; null for none.</summary>
    /// <param name="key">The key.</param>
    ValueTask<PeerRecord?> PeerByKeyAsync(byte[] key);

    /// <summary>Adds or replaces a peer.</summary>
    /// <param name="peer">The peer.</param>
    ValueTask PutPeerAsync(PeerRecord peer);

    /// <summary>
    /// Removes a peer, its catalog, the keys it was sent and its leftovers. A
    /// peer that isn't there is no error.
    /// </summary>
    /// <param name="id">The peer's ID.</param>
    ValueTask DeletePeerAsync(string id);

    /// <summary>Every pairing code.</summary>
    ValueTask<IReadOnlyList<CodeRecord>> CodesAsync();

    /// <summary>The code whose secret hashes to this; null for none.</summary>
    /// <param name="secretHash">The hash.</param>
    ValueTask<CodeRecord?> CodeByHashAsync(byte[] secretHash);

    /// <summary>Adds or replaces a code.</summary>
    /// <param name="code">The code.</param>
    ValueTask PutCodeAsync(CodeRecord code);

    /// <summary>Removes a code.</summary>
    /// <param name="id">The code's ID.</param>
    ValueTask DeleteCodeAsync(string id);

    /// <summary>
    /// The entry of a key whose pairing ended, by the SHA-256 of the key (MSFP
    /// core section 7.4); null for none.
    /// </summary>
    /// <param name="keyHash">The key's SHA-256.</param>
    ValueTask<ForgottenKey?> ForgottenAsync(byte[] keyHash);

    /// <summary>Adds or replaces an entry.</summary>
    /// <param name="key">The entry.</param>
    ValueTask ForgetAsync(ForgottenKey key);

    /// <summary>Removes an entry.</summary>
    /// <param name="keyHash">The key's SHA-256.</param>
    ValueTask UnforgetAsync(byte[] keyHash);

    /// <summary>Drops entries forgotten before then (a year old).</summary>
    /// <param name="before">The time.</param>
    ValueTask TrimForgottenAsync(DateTimeOffset before);

    /// <summary>
    /// The host's library IDs mapped to the public IDs peers see, those of
    /// gone libraries included: a public ID is never reused.
    /// </summary>
    ValueTask<IReadOnlyDictionary<string, string>> LibraryIdsAsync();

    /// <summary>Notes a library's public ID.</summary>
    /// <param name="hostId">The host's ID.</param>
    /// <param name="publicId">The public ID.</param>
    ValueTask PutLibraryIdAsync(string hostId, string publicId);

    /// <summary>
    /// Which of these title keys the peer was ever sent; a changes answer
    /// names in gone only those (MSFP catalog section 3.2).
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="keys">The keys.</param>
    ValueTask<IReadOnlyList<string>> SentAsync(string peer, IReadOnlyList<string> keys);

    /// <summary>Notes keys as sent to the peer.</summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="keys">The keys.</param>
    ValueTask MarkSentAsync(string peer, IReadOnlyList<string> keys);

    /// <summary>Playbacks this server started on peers, to stop after a restart.</summary>
    ValueTask<IReadOnlyList<Leftover>> LeftoversAsync();

    /// <summary>Notes a leftover.</summary>
    /// <param name="leftover">The leftover.</param>
    ValueTask PutLeftoverAsync(Leftover leftover);

    /// <summary>Removes a leftover.</summary>
    /// <param name="leftover">The leftover.</param>
    ValueTask DeleteLeftoverAsync(Leftover leftover);

    // Peers' catalogs. The engine only writes them; the host reads them as
    // it likes (counts, joins, search), in its own storage.

    /// <summary>Adds or replaces titles, marked with the reading.</summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="reading">The reading.</param>
    /// <param name="titles">The titles.</param>
    ValueTask PutPeerTitlesAsync(string peer, long reading, IReadOnlyList<Title> titles);

    /// <summary>After a whole-list reading, drops the peer's titles not marked with it.</summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="reading">The reading.</param>
    ValueTask EndReadingAsync(string peer, long reading);

    /// <summary>Drops titles by key (a changes reading's gone).</summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="keys">The keys.</param>
    ValueTask DropPeerTitlesAsync(string peer, IReadOnlyList<string> keys);

    /// <summary>
    /// A peer's titles with keys greater than after (ordinal), in key order,
    /// at most limit.
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="after">The key after which to list.</param>
    /// <param name="limit">The most.</param>
    ValueTask<IReadOnlyList<Title>> PeerTitlesAsync(string peer, string after, int limit);

    /// <summary>One of a peer's titles; null for none.</summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="key">The key.</param>
    ValueTask<Title?> PeerTitleAsync(string peer, string key);
}

/// <summary>
/// What a transaction implements, besides <see cref="IStoreTx"/>, for the
/// part groups (MSFP groups part): the groups this server is in. The
/// shipped stores implement it; without it, the engine has no groups.
/// </summary>
public interface IGroupsTx
{
    /// <summary>Every group, by ID.</summary>
    ValueTask<IReadOnlyList<GroupRecord>> GroupsAsync();

    /// <summary>One group; null for none.</summary>
    ValueTask<GroupRecord?> GroupAsync(string id);

    /// <summary>Adds or replaces a group.</summary>
    ValueTask PutGroupAsync(GroupRecord group);

    /// <summary>Deletes a group.</summary>
    ValueTask DeleteGroupAsync(string id);
}

/// <summary>
/// What a transaction implements, besides <see cref="IStoreTx"/>, when the
/// engine keeps the change log of the host's own catalog (an ICatalog
/// without <see cref="IChangeLog"/>, host.md section 2). The shipped stores
/// implement it.
/// </summary>
public interface IOwnLogTx
{
    /// <summary>The titles as the last refresh read them, by key.</summary>
    ValueTask<IReadOnlyDictionary<string, Title>> OwnSnapshotAsync();

    /// <summary>Replaces the snapshot.</summary>
    /// <param name="titles">The titles, by key.</param>
    ValueTask PutOwnSnapshotAsync(IReadOnlyDictionary<string, Title> titles);

    /// <summary>
    /// For each key greater than after changed after since, its latest change,
    /// in key order, at most limit.
    /// </summary>
    /// <param name="since">The change number after which to list.</param>
    /// <param name="after">The key after which to list.</param>
    /// <param name="limit">The most.</param>
    ValueTask<IReadOnlyList<OwnChange>> OwnChangesAsync(long since, string after, int limit);

    /// <summary>Appends changes to the log.</summary>
    /// <param name="changes">The changes.</param>
    ValueTask AppendOwnChangesAsync(IReadOnlyList<OwnChange> changes);

    /// <summary>Drops changes noted before then.</summary>
    /// <param name="before">The time.</param>
    ValueTask TrimOwnChangesAsync(DateTimeOffset before);

    /// <summary>The last seq no longer kept (0 when none was dropped).</summary>
    ValueTask<long> OwnFloorAsync();

    /// <summary>The highest seq ever appended (0 for none).</summary>
    ValueTask<long> OwnSeqAsync();

    /// <summary>
    /// Takes a new change number without a change, and gives it: what a peer
    /// sees changed otherwise (its offers, the parts on).
    /// </summary>
    ValueTask<long> BumpOwnSeqAsync();
}

/// <summary>
/// The host's own libraries and titles, by the host's library IDs. The
/// engine maps them to public IDs, filters them by offers (and local titles
/// by the local sharing switches: SetLocalAsync, SetGroupLocalAsync), pages
/// them, and checks every key and member before it leaves.
/// </summary>
/// <remarks>
/// A title is a <see cref="VideoTitle"/> or a <see cref="MusicTitle"/>.
/// Films, episodes, extras and tracks carry their copies, described as their
/// files are (MSFP video section 3.2, music section 3.3), so that a viewer's
/// server lists them as files of its own before anyone plays; each copy's ID
/// is the host's handle of it, the one <see cref="IPlayback"/> and
/// <see cref="IMedia"/> are given. play takes the title's copies as the
/// catalog sends them (best first, the version IDs made of these handles):
/// a copy is played only as it is described. Extras are titles of their
/// own, keyed under their film, series or season
/// ("movie/tmdb/348/x/&lt;opaque&gt;", kind "extra"), listed in its
/// libraries. Titles no public database names are keyed under the local
/// scheme ("movie/local/&lt;opaque&gt;"), with a name (and a year, an image)
/// of the host's; their opaque parts, and extras', are made with
/// <see cref="MsfpEngine.OpaqueKeyPart"/> or as catalog section 4.2 says.
/// </remarks>
public interface ICatalog
{
    /// <summary>The host's libraries; the operator offers some.</summary>
    /// <param name="ct">Cancels.</param>
    ValueTask<IReadOnlyList<Library>> LibrariesAsync(CancellationToken ct);

    /// <summary>
    /// The titles held in any of these libraries, keys strictly greater than
    /// after, in ascending byte order, at most limit, each once. A change of
    /// anything a title carries, its copies included (a copy added, gone, or
    /// its ETag changed), is a change of the title: an
    /// <see cref="IChangeLog"/> lists it again.
    /// </summary>
    /// <param name="libraries">The host's library IDs.</param>
    /// <param name="after">The key after which to list.</param>
    /// <param name="limit">The most.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask<TitlePage> TitlesAsync(IReadOnlyList<string> libraries, string after, int limit, CancellationToken ct);
}

/// <summary>
/// What an <see cref="ICatalog"/> implements, besides ICatalog, when it
/// shares the images of its local titles (MSFP catalog section 4.4): the
/// local films, series, artists and albums whose Image it sets. Without it,
/// no image is given, whatever Image says.
/// </summary>
public interface IImages
{
    /// <summary>
    /// The one image of a local title: a JPEG or a PNG of at most 1 MiB, 1 to
    /// 4,096 pixels wide and tall. The engine checks it
    /// (<see cref="CatalogRules.TryCheckImage(ReadOnlySpan{byte}, out string, out string)"/>)
    /// and sends nothing else. <see cref="MsfpError.NotFound"/> for none.
    /// </summary>
    /// <param name="key">The title's key.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask<byte[]> ImageAsync(string key, CancellationToken ct);
}

/// <summary>
/// What an <see cref="ICatalog"/> implements, besides ICatalog, when its
/// database notes changes itself. Without it, the engine keeps the log from
/// snapshots of Titles (<see cref="IOwnLogTx"/>).
/// </summary>
public interface IChangeLog
{
    /// <summary>The change number now.</summary>
    /// <param name="ct">Cancels.</param>
    ValueTask<long> SeqAsync(CancellationToken ct);

    /// <summary>
    /// What changed in these libraries after since; throws an
    /// <see cref="MsfpException"/> with <see cref="MsfpError.TooOld"/> when the
    /// log no longer goes back that far (the engine then sends the whole list).
    /// </summary>
    /// <param name="libraries">The host's library IDs.</param>
    /// <param name="since">The change number after which to list.</param>
    /// <param name="after">The key after which to list.</param>
    /// <param name="limit">The most.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask<ChangePage> ChangesAsync(IReadOnlyList<string> libraries, long since, string after, int limit, CancellationToken ct);
}

/// <summary>
/// What an <see cref="IChangeLog"/> implements, besides IChangeLog, when it
/// can take a change number with no change of a title. After a change of
/// what a peer sees (its offers, the parts on), the peer's readings are
/// whole until the log moves past the number noted then
/// (<see cref="PeerRecord.OffersSeq"/>, one above the log's): the engine
/// takes one at once, so that the reading after the next whole one is of
/// changes again, without waiting for a title to change.
/// </summary>
public interface ISeqBumper
{
    /// <summary>
    /// Takes a new change number, above every one before, and gives it;
    /// Changes never lists anything under it.
    /// </summary>
    /// <param name="ct">Cancels.</param>
    ValueTask<long> BumpSeqAsync(CancellationToken ct);
}

/// <summary>
/// What an <see cref="ICatalog"/> implements, besides ICatalog, when it
/// shares the lyrics of its tracks (MSFP music section 6): the copies whose
/// Lyrics says "plain" or "synced".
/// </summary>
public interface ILyrics
{
    /// <summary>
    /// A copy's lyrics (its handle), at most 8 sets, times in milliseconds
    /// (MSFP music section 6). The engine cleans them as a viewer's server
    /// keeps them (<see cref="Music.ReadLyrics"/>) before they leave.
    /// <see cref="MsfpError.NotFound"/> for none.
    /// </summary>
    /// <param name="copy">The copy's handle.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask<IReadOnlyList<LyricsSet>> LyricsAsync(string copy, CancellationToken ct);
}

/// <summary>
/// The owning side of a peer's playback here. The engine has checked the
/// request (<see cref="Playback.ReadPlay"/>), found the title's copies as the
/// catalog sends them, picked the copy and the tracks, refused what MSFP
/// refuses of the delivery asked and what the pool's limits refuse early;
/// then it asks the host how to serve the copy apart (Describe), and to
/// start.
/// </summary>
public interface IPlayback
{
    /// <summary>
    /// What the catalog doesn't say of a copy: its previews' layout, its
    /// fonts, the subtitle tracks it can't serve apart.
    /// </summary>
    /// <param name="copy">The copy's handle.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask<CopyInfo> DescribeAsync(string copy, CancellationToken ct);

    /// <summary>
    /// Starts serving the copy for its purpose and delivery. The host chooses
    /// how when no delivery is asked (direct, remux, transcode), and serves
    /// exactly the delivery asked otherwise; then it calls
    /// <see cref="StartRequest.Admit"/> before starting any work. Errors:
    /// NotPlayable, DeliveryRefused, ServerBusy, StreamLimitReached.
    /// </summary>
    /// <param name="request">What to start.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask<Started> StartAsync(StartRequest request, CancellationToken ct);

    /// <summary>
    /// Passes on a peer's progress or stop, for the host's live view and its
    /// viewers' progress; a Late one is of a download's playback ended
    /// already (the viewer watched offline).
    /// </summary>
    /// <param name="report">The report.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask ProgressAsync(Report report, CancellationToken ct);

    /// <summary>Ends a playback: once for each Start that succeeded.</summary>
    /// <param name="handle">The playback's handle.</param>
    /// <param name="reason">Why.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask StopAsync(string handle, EndReason reason, CancellationToken ct);
}

/// <summary>
/// Serves the bytes of a live playback. The engine has checked the ticket,
/// the request and its Range header, and cuts the response when the
/// playback ends. For <see cref="MediaKind.File"/> it has resolved the range
/// against the copy's exact size (range null for the whole file, else First
/// to Last, both within it), writes the status and headers itself (206 or
/// 200, Content-Range, ETag, Accept-Ranges) and sends only those bytes: the
/// host writes the copy's bytes from First, as they are, and its Start's
/// content type alone is kept. Subtitles, ASS, pictures and fonts are read
/// whole and rebuilt or checked before they leave; playlists are checked
/// against MSFP's subset. Errors: NotFound, RangeNotSatisfiable, ServerBusy.
/// </summary>
public interface IMedia
{
    /// <summary>Serves what path asks for, of the playback, to output.</summary>
    /// <param name="handle">The playback's handle (a copy's, for <see cref="MediaPath.OfCopy"/>).</param>
    /// <param name="path">What is asked for.</param>
    /// <param name="range">The byte range asked for; null for the whole.</param>
    /// <param name="output">Where to write the answer.</param>
    /// <param name="ct">Cancelled when the playback ends or the request goes.</param>
    ValueTask ServeAsync(string handle, MediaPath path, ByteRange? range, IResponseSink output, CancellationToken ct);
}

/// <summary>
/// Where <see cref="IMedia"/> writes an answer: <see cref="Start"/> once,
/// then the body.
/// </summary>
public interface IResponseSink
{
    /// <summary>Starts the answer.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="contentType">The content type.</param>
    /// <param name="length">The length; -1 when unknown.</param>
    /// <param name="contentRange">The Content-Range; "" for none.</param>
    void Start(int status, string contentType, long length, string contentRange);

    /// <summary>Writes body bytes, after <see cref="Start"/>.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
}

/// <summary>
/// What an <see cref="IResponseSink"/> a host gives
/// <see cref="MsfpEngine.FetchAsync"/> implements besides, when it presents
/// a copy as a file of its own: the file's ETag is given through it before
/// Start, so that the host may give it to its clients.
/// </summary>
public interface IETagSink
{
    /// <summary>The file's etag (without quotes).</summary>
    /// <param name="etag">The etag.</param>
    void SetETag(string etag);
}

/// <summary>
/// Seals the seeds and secrets the engine stores, each under a context
/// ("msfp current key", "msfp next key", "msfp old key", "msfp revoked
/// key", "msfp ids secret", "msfp code secret"): a value sealed under one
/// context never opens under another. Open throws an
/// <see cref="MsfpException"/> with <see cref="MsfpError.Sealed"/> when the
/// box is tampered with, of another context, or of another key.
/// </summary>
/// <remarks>
/// The host chooses how: a key in its configuration
/// (<see cref="KeySealer"/>), a key file apart from its data
/// (<see cref="KeyFileSealer"/>), a secret store. <see cref="PlainSealer"/>
/// seals nothing, for tests.
/// </remarks>
public interface ISealer
{
    /// <summary>Seals plain under context.</summary>
    /// <param name="plain">What to seal.</param>
    /// <param name="context">The context.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask<byte[]> SealAsync(byte[] plain, string context, CancellationToken ct);

    /// <summary>Opens what was sealed under context.</summary>
    /// <param name="sealed">The box.</param>
    /// <param name="context">The context.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask<byte[]> OpenAsync(byte[] @sealed, string context, CancellationToken ct);
}

/// <summary>
/// What an <see cref="IStore"/> implements when it brings its own
/// <see cref="ISealer"/>, used when the configuration gives none (the
/// shipped stores do).
/// </summary>
public interface ISealerProvider
{
    /// <summary>The store's sealer.</summary>
    ISealer Sealer { get; }
}

/// <summary>
/// Makes the connections to peers. The default is the engine's own, which
/// looks a name up once, refuses the destinations of MSFP core section 3.3,
/// and connects to an address it checked. A host whose connections go out
/// elsewhere (through a gateway) gives its own, which must make the same
/// check where the connection is made. The engine still does TLS, pins, no
/// redirects and the time bounds on top.
/// </summary>
public interface IDialer
{
    /// <summary>
    /// A TCP connection to the endpoint (host and port). A refused destination
    /// throws an <see cref="MsfpException"/> with
    /// <see cref="MsfpError.RefusedDestination"/>, told apart from a peer that
    /// can't be reached.
    /// </summary>
    /// <param name="endpoint">The host and port.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask<Stream> DialAsync(DnsEndPoint endpoint, CancellationToken ct);
}

/// <summary>
/// Is told the engine's events, for the operator's panel and the viewers'
/// pages. Notify is called in order, one at a time, outside the engine's
/// locks; it must return quickly (a host with slow work queues it). Events
/// are notices: what they speak of is in the queries too.
/// </summary>
public interface INotifier
{
    /// <summary>Tells an event.</summary>
    /// <param name="e">The event.</param>
    void Notify(MsfpEvent e);
}

/// <summary>
/// Re-encodes a relayed preview sheet, so that only pixels reach the
/// viewer. A sheet that isn't a JPEG within the size throws an
/// <see cref="MsfpException"/> with <see cref="MsfpError.NotAnImage"/>.
/// </summary>
public interface ISheetEncoder
{
    /// <summary>The sheet, decoded and encoded again.</summary>
    /// <param name="jpeg">The sheet as relayed.</param>
    /// <param name="maxWidth">The widest it may be.</param>
    /// <param name="maxHeight">The tallest it may be.</param>
    /// <param name="ct">Cancels.</param>
    ValueTask<byte[]> ReencodeAsync(ReadOnlyMemory<byte> jpeg, int maxWidth, int maxHeight, CancellationToken ct);
}
