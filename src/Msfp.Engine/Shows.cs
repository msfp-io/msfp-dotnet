namespace Msfp;

/// <summary>What a group shows here, member by member.</summary>
/// <param name="Members">Each other member.</param>
public sealed record GroupShowsView(IReadOnlyList<GroupMemberShows> Members);

/// <summary>One member's libraries offered to this server, and whether the group shows each.</summary>
/// <param name="Member">Its member ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Peer">The link with it ("" for none: nothing of it is read yet).</param>
/// <param name="Libraries">Its libraries.</param>
public sealed record GroupMemberShows(string Member, string Name, string Peer, IReadOnlyList<ShownLibrary> Libraries);

/// <summary>A library and whether it is shown.</summary>
/// <param name="Id">Its public ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Shown">Whether the group shows it here.</param>
public sealed record ShownLibrary(string Id, string Name, bool Shown);

// What this server shows from a group (its members' libraries, as one
// choice for the whole group): everything its members offer it, but the
// libraries it hides. What it reads of a server is what it shows of it
// individually (SetWantsAsync, while the individual relation holds) and
// what its groups show of it. As msfp-go.
public sealed partial class MsfpEngine
{
    /// <summary>What a group shows here.</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public async Task<GroupShowsView> GroupShowsAsync(string id, CancellationToken ct = default)
    {
        var members = new List<GroupMemberShows>();
        await _store.ReadAsync(async tx =>
        {
            members.Clear();
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            var peers = await tx.PeersAsync().ConfigureAwait(false);
            var s = StateOf(g);
            foreach (var m in s.Members.Values.Where(m => m.Id != g.Me))
            {
                var p = peers.FirstOrDefault(p => IsMember(m, p) && p.Groups?.Contains(id) == true);
                var libs = p is null ? [] : (p.Wants ?? []).Where(w => !w.Gone)
                    .Select(w => new ShownLibrary(w.Id, w.Name, g.Hidden?.Contains(new HiddenLibrary(m.Id, w.Id)) != true)).ToList();
                members.Add(new GroupMemberShows(m.Id, m.Name, p?.Id ?? "", libs));
            }
        }, ct).ConfigureAwait(false);
        return new GroupShowsView([.. members.OrderBy(m => m.Name, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Sets the members' libraries a group doesn't show here; it shows
    /// everything else they offer. Each member linked through it is read
    /// again soon.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public async Task SetGroupShowsAsync(string id, IReadOnlyList<HiddenLibrary> hidden, CancellationToken ct = default)
    {
        var peers = new List<string>();
        await _store.UpdateAsync(async tx =>
        {
            peers.Clear();
            var g = await GroupOfAsync(tx, id).ConfigureAwait(false);
            var kept = hidden.Where(h => h.Member.Length > 0 && h.Library.Length > 0).Distinct().ToList();
            g.Hidden = kept.Count > 0 ? kept : null;
            peers.AddRange((await tx.PeersAsync().ConfigureAwait(false)).Where(p => p.Groups?.Contains(id) == true).Select(p => p.Id));
            await GroupsOf(tx).PutGroupAsync(g).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        foreach (var p in peers)
        {
            ReadSoon(p);
        }
    }

    // Which of a link's libraries its groups show here: shows(id) for a
    // library, and grouped, whether some group shows anything of it.
    private async Task<(Func<string, bool> Shows, bool Grouped)> ShownByAsync(PeerRecord p, CancellationToken ct)
    {
        var groups = new List<(string Member, List<HiddenLibrary> Hidden)>();
        await _store.ReadAsync(async tx =>
        {
            groups.Clear();
            if (tx is not IGroupsTx gt)
            {
                return;
            }

            foreach (var id in p.Groups ?? [])
            {
                if (await gt.GroupAsync(id).ConfigureAwait(false) is not { } g)
                {
                    continue;
                }

                foreach (var m in StateOf(g).Members.Values.Where(m => IsMember(m, p)))
                {
                    groups.Add((m.Id, g.Hidden ?? []));
                }
            }
        }, ct).ConfigureAwait(false);
        return (lib => groups.Any(h => !h.Hidden.Contains(new HiddenLibrary(h.Member, lib))), groups.Count > 0);
    }
}
