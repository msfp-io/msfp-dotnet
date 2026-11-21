using Msfp.Protocol;

namespace Msfp.Tests;

// Groups (msfp-go's groups_test.go): joining with a group's code and
// pairing through the group, bans, leaving and dissolving, key rotation,
// offers and pools, accepting after the group's days, a newcomer that
// can't be called, roles, refusals, and group-join for a peer already.
public sealed class GroupTests
{
    private static async Task<TestServer> ServerAsync(Network net, string name, FakeTime? clock = null, Func<MsfpConfig, MsfpConfig>? with = null) =>
        await TestServer.StartAsync(net, $"https://{name.ToLowerInvariant()}.example.org", name, clock: clock, with: with);

    // Waits until the servers' background work, which starts work on each
    // other, is over: several rounds, each a moment after the last, as work
    // one server's call starts on another may begin meanwhile.
    private static async Task SettleAsync(params TestServer[] servers)
    {
        for (var i = 0; i < 8; i++)
        {
            foreach (var s in servers)
            {
                await s.WaitAsync();
            }

            await Task.Delay(20);
        }
    }

    // Member redeems a code inviter makes for the group, and the inviter's
    // operator accepts it (MSFP groups sections 2 and 4).
    private static async Task JoinGroupAsync(TestServer inviter, TestServer member, string group, params TestServer[] others)
    {
        var (code, view) = await inviter.Engine.MakeCodeWithAsync(new CodeOptions { Group = group });
        Assert.Equal((group, 1), (view.Group, view.Uses));
        var preview = await member.Engine.ReadCodeAsync(code);
        Assert.Equal(group, preview.Group);
        Assert.NotEmpty(preview.GroupName);
        await member.Engine.RedeemCodeAsync(code);
        var ours = (await inviter.Engine.PeersAsync()).Single(p => p.State == PeerState.Ours);
        await inviter.Engine.AcceptAsync(ours.Id);
        await SettleAsync([inviter, member, .. others]);
    }

    private static List<string> MemberIds(GroupView v) => v.Members.Select(m => m.Id).Order(StringComparer.Ordinal).ToList();

    private static async Task<PeerView?> PeerNamedAsync(TestServer s, string name) =>
        (await s.Engine.PeersAsync()).FirstOrDefault(p => p.Name == name);

    private static GroupMemberView? MemberNamed(GroupView v, string name) => v.Members.FirstOrDefault(m => m.Name == name);

    private static async Task<MsfpError> ErrorAsync(Func<Task> f) => (await Assert.ThrowsAsync<MsfpException>(f)).Code;

    // A (the owner, founding Family), B and N, both invited by A, N's
    // request to B waiting.
    private static async Task<(TestServer A, TestServer B, TestServer N, GroupView G)> TrioAsync(Network net,
        Func<MsfpConfig, MsfpConfig>? withB = null, FakeTime? clock = null)
    {
        var a = await ServerAsync(net, "A", clock);
        var b = await ServerAsync(net, "B", clock, withB);
        var n = await ServerAsync(net, "N", clock);
        var g = await a.Engine.CreateGroupAsync("Family");
        await JoinGroupAsync(a, b, g.Id);
        await JoinGroupAsync(a, n, g.Id, b);
        await SettleAsync(a, b, n);
        return (a, b, n, g);
    }

    // Three servers: A founds Family and invites B, then N; N's request
    // reaches B through A, B accepts, and B and N pair through the group.
    [Fact]
    public async Task JoinAndPair()
    {
        var net = new Network();
        using var a = await ServerAsync(net, "A");
        using var b = await ServerAsync(net, "B");
        using var n = await ServerAsync(net, "N");
        Assert.Equal(MsfpError.CodeOptions, await ErrorAsync(() => a.Engine.CreateGroupAsync(" ")));
        Assert.Equal(MsfpError.CodeOptions, await ErrorAsync(() => a.Engine.CreateGroupAsync("Family", new GroupSettings(31, 5))));
        var g = await a.Engine.CreateGroupAsync("Family");
        Assert.Equal(("owner", "Family", 1), (g.Role, g.Name, g.Members.Count));

        await JoinGroupAsync(a, b, g.Id);
        var gb = await b.Engine.GroupAsync(g.Id);
        Assert.Equal(("member", 2, g.Me), (gb.Role, gb.Members.Count, gb.Owner));
        var joined = Assert.Single(b.Events.Of<GroupJoined>());
        Assert.Equal((g.Id, "Family"), (joined.Group, joined.GroupName));

        await JoinGroupAsync(a, n, g.Id, b);
        await SettleAsync(a, b, n);
        var ga = await a.Engine.GroupAsync(g.Id);
        var gn = await n.Engine.GroupAsync(g.Id);
        gb = await b.Engine.GroupAsync(g.Id);
        Assert.Equal(3, ga.Members.Count);
        Assert.Equal(MemberIds(ga), MemberIds(gn));
        Assert.Equal(MemberIds(ga), MemberIds(gb));
        var request = Assert.Single(gb.Requests);
        Assert.Equal("N", request.Name);
        var waiting = Assert.Single(b.Events.Of<GroupRequestWaiting>());
        Assert.Equal((request.Mail, "N"), (waiting.Mail, waiting.Name));
        Assert.Contains(b.Events.Of<GroupChanged>(), c => c.Types.Contains("joined"));

        await b.Engine.AcceptRequestAsync(g.Id, request.Mail);
        await SettleAsync(a, b, n);
        var onB = await PeerNamedAsync(b, "N");
        var onN = await PeerNamedAsync(n, "B");
        Assert.Equal(PeerState.Paired, onB?.State);
        Assert.Equal(PeerState.Paired, onN?.State);
        Assert.False(onB!.ByHand);
        Assert.Equal([g.Id], onB.Groups);
    }

    // A ban ends the banned server's links made only through the group, and
    // its copy of the group; a member that leaves keeps its links by hand;
    // dissolving removes the group everywhere.
    [Fact]
    public async Task BanLeaveDissolve()
    {
        var net = new Network();
        var (a, b, n, g) = await TrioAsync(net);
        using var _a = a;
        using var _b = b;
        using var _n = n;
        var gb = await b.Engine.GroupAsync(g.Id);
        await b.Engine.AcceptRequestAsync(g.Id, Assert.Single(gb.Requests).Mail);
        await SettleAsync(a, b, n);
        var nId = MemberNamed(gb, "N")!.Id;
        await a.Engine.BanAsync(g.Id, nId, "testing");
        await SettleAsync(a, b, n);
        Assert.Null(await PeerNamedAsync(b, "N"));
        Assert.Equal(MsfpError.NotFound, await ErrorAsync(() => n.Engine.GroupAsync(g.Id)));
        var left = Assert.Single(n.Events.Of<GroupLeft>());
        Assert.Equal(("banned", "testing"), (left.Why, left.Reason));
        var ban = Assert.Single((await a.Engine.GroupAsync(g.Id)).Banned);
        Assert.Equal(("testing", "N"), (ban.Reason, ban.Name));
        Assert.NotEmpty(ban.Fingerprint);

        await b.Engine.LeaveGroupAsync(g.Id);
        await SettleAsync(a, b, n);
        Assert.Equal(MsfpError.NotFound, await ErrorAsync(() => b.Engine.GroupAsync(g.Id)));
        Assert.Equal("left", Assert.Single(b.Events.Of<GroupLeft>()).Why);
        var kept = Assert.Single(await b.Engine.PeersAsync());
        Assert.Equal(PeerState.Paired, kept.State); // A, by hand
        Assert.Single((await a.Engine.GroupAsync(g.Id)).Members);

        await a.Engine.DissolveGroupAsync(g.Id, true);
        await SettleAsync(a, b, n);
        Assert.Equal(MsfpError.NotFound, await ErrorAsync(() => a.Engine.GroupAsync(g.Id)));
        Assert.Equal("dissolved", Assert.Single(a.Events.Of<GroupLeft>()).Why);
    }

    // A member that rotates its key tells its groups (rotated), and its
    // events under the new key count on the others' copies.
    [Fact]
    public async Task KeyRotation()
    {
        var net = new Network();
        using var a = await ServerAsync(net, "A");
        using var b = await ServerAsync(net, "B");
        var g = await a.Engine.CreateGroupAsync("Family");
        await JoinGroupAsync(a, b, g.Id);
        await a.Engine.RotateAsync();
        await SettleAsync(a, b);
        await a.Engine.SetGroupSettingsAsync(g.Id, "Family and friends", GroupSettings.Default);
        await SettleAsync(a, b);
        var gb = await b.Engine.GroupAsync(g.Id);
        Assert.Equal("Family and friends", gb.Name);
        var owner = gb.Members.Single(m => m.Role == "owner");
        Assert.Equal((await a.Engine.ServerAsync()).Fingerprint, owner.Fingerprint);
    }

    // B offers its films to the group, with a pool of one stream: N, linked
    // to B through the group alone, sees them and plays one; a second is
    // refused while the first plays (groups section 9).
    [Fact]
    public async Task OffersAndPool()
    {
        var net = new Network();
        var h = HostKit.FilmHost();
        h.Libs = [new Library { Id = "films", Name = "Films", Kind = "movies" }];
        var (a, b, n, g) = await TrioAsync(net, c => c with { Catalog = h, Playback = h, Media = h, SheetEncoder = new TestSheets() });
        using var _a = a;
        using var _b = b;
        using var _n = n;
        var gb = await b.Engine.GroupAsync(g.Id);
        await b.Engine.AcceptRequestAsync(g.Id, Assert.Single(gb.Requests).Mail);
        await SettleAsync(a, b, n);
        await b.Engine.SetGroupOffersAsync(g.Id, ["films"]);
        Assert.Contains(await b.Engine.GroupOffersAsync(g.Id), l => l.Id == "films" && l.Offered);
        await b.Engine.SetGroupPoolAsync(g.Id, new Limits { Streams = 1, Transcodes = 0 });
        var onN = await PeerNamedAsync(n, "B");
        Assert.Equal(PeerState.Paired, onN?.State);
        await n.Engine.CheckAsync(onN!.Id);
        var libs = await n.Engine.TheirLibrariesAsync(onN.Id);
        Assert.Equal("Films", Assert.Single(libs).Name);
        Assert.Equal(1, (await n.Engine.PeerAsync(onN.Id)).TheirLimits?.Streams);
        await n.Engine.SetWantsAsync(onN.Id, [libs[0].Id]);
        await n.Engine.ReadNowAsync(onN.Id);
        await n.Engine.PlayAsync(onN.Id, new PlayRequest { Title = "movie/tmdb/949" });
        Assert.Equal(MsfpError.StreamLimitReached,
            await ErrorAsync(() => n.Engine.PlayAsync(onN.Id, new PlayRequest { Title = "movie/tmdb/949" })));
    }

    // "Accept members": a request is accepted by the server itself once its
    // sender has been in the group the group's days; before, it waits.
    [Fact]
    public async Task AcceptAfterDays()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        using var a = await ServerAsync(net, "A", clock);
        using var b = await ServerAsync(net, "B", clock);
        using var n = await ServerAsync(net, "N", clock);
        var g = await a.Engine.CreateGroupAsync("Family");
        await JoinGroupAsync(a, b, g.Id);
        await b.Engine.SetGroupAnswerAsync(g.Id, true);
        await JoinGroupAsync(a, n, g.Id, b);
        await SettleAsync(a, b, n);
        var request = Assert.Single((await b.Engine.GroupAsync(g.Id)).Requests);
        Assert.False(request.Waited);
        clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        await b.Engine.SyncGroupsNowAsync(true, CancellationToken.None);
        await SettleAsync(a, b, n);
        Assert.Equal(PeerState.Paired, (await PeerNamedAsync(b, "N"))?.State);
        Assert.Empty((await b.Engine.GroupAsync(g.Id)).Requests);
    }

    // A newcomer that can't be called: the member accepting answers by mail,
    // and the newcomer calls group-pair itself (groups section 4.2).
    [Fact]
    public async Task UncallableNewcomer()
    {
        var net = new Network();
        using var a = await ServerAsync(net, "A");
        using var b = await ServerAsync(net, "B");
        using var n = await ServerAsync(net, "N");
        await n.Engine.SetUncallableAsync(true);
        var g = await a.Engine.CreateGroupAsync("Family");
        await JoinGroupAsync(a, b, g.Id);
        await JoinGroupAsync(a, n, g.Id, b);
        // N learns it is paired by its own hello, then reads the list.
        for (var i = 0; i < 3; i++)
        {
            await n.Engine.RunOnceAsync();
            await n.Engine.SyncGroupsNowAsync(true, CancellationToken.None);
            await SettleAsync(a, b, n);
        }

        var request = Assert.Single((await b.Engine.GroupAsync(g.Id)).Requests);
        await b.Engine.AcceptRequestAsync(g.Id, request.Mail);
        await SettleAsync(a, b, n);
        await n.Engine.SyncGroupsNowAsync(true, CancellationToken.None);
        await SettleAsync(a, b, n);
        Assert.Equal(PeerState.Paired, (await PeerNamedAsync(n, "B"))?.State);
        var onB = await PeerNamedAsync(b, "N");
        Assert.Equal((PeerState.Paired, Inbound.No), (onB?.State, onB?.Inbound));
    }

    // Roles: an admin invites, a member can't; the owner gives the group to
    // its heir; an unban lets a server back; a block ends a member's link and
    // those of the members it invited.
    [Fact]
    public async Task Roles()
    {
        var net = new Network();
        var (a, b, n, g) = await TrioAsync(net);
        using var _a = a;
        using var _b = b;
        using var _n = n;
        var ga = await a.Engine.GroupAsync(g.Id);
        var bId = MemberNamed(ga, "B")!.Id;
        var nId = MemberNamed(ga, "N")!.Id;
        Assert.Equal(MsfpError.NoRight, await ErrorAsync(() => n.Engine.MakeCodeWithAsync(new CodeOptions { Group = g.Id })));
        await a.Engine.SetAdminAsync(g.Id, bId, true);
        await SettleAsync(a, b, n);
        using var c = await ServerAsync(net, "C");
        await JoinGroupAsync(b, c, g.Id, a, n);
        await SettleAsync(a, b, n, c);
        ga = await a.Engine.GroupAsync(g.Id);
        var cId = MemberNamed(ga, "C")?.Id;
        Assert.NotNull(cId);
        Assert.Equal(4, ga.Members.Count);
        // N blocks B and the members it invited: C.
        await n.Engine.BlockAsync(g.Id, bId, true);
        Assert.Null(await PeerNamedAsync(n, "B"));
        // N, a member, can't ban.
        Assert.Equal(MsfpError.NoRight, await ErrorAsync(() => n.Engine.BanAsync(g.Id, cId)));
        await a.Engine.BanAsync(g.Id, nId);
        await SettleAsync(a, b, n, c);
        await a.Engine.UnbanAsync(g.Id, nId);
        await SettleAsync(a, b, n, c);
        ga = await a.Engine.GroupAsync(g.Id);
        Assert.Empty(ga.Banned);
        Assert.Null(MemberNamed(ga, "N")); // a member again only by a new invitation
        // The owner names C its heir, then steps down: C owns the group.
        await a.Engine.SetHeirAsync(g.Id, cId);
        await a.Engine.GiveGroupAsync(g.Id);
        await SettleAsync(a, b, n, c);
        var gc = await c.Engine.GroupAsync(g.Id);
        Assert.Equal(("owner", cId), (gc.Role, gc.Owner));
        Assert.Equal(MsfpError.NoRight, await ErrorAsync(() => a.Engine.SetAdminAsync(g.Id, bId, false)));
    }

    // B refuses N's request, then lifts the refusal and asks N itself; N
    // accepts, and the two pair through the group.
    [Fact]
    public async Task RefuseThenAsk()
    {
        var net = new Network();
        var (a, b, n, g) = await TrioAsync(net);
        using var _a = a;
        using var _b = b;
        using var _n = n;
        var gb = await b.Engine.GroupAsync(g.Id);
        var gn = await n.Engine.GroupAsync(g.Id);
        var request = Assert.Single(gb.Requests);
        Assert.True(MemberNamed(gn, "B")?.Asked);
        await b.Engine.RefuseRequestAsync(g.Id, request.Mail);
        gb = await b.Engine.GroupAsync(g.Id);
        var nView = MemberNamed(gb, "N")!;
        Assert.Empty(gb.Requests);
        Assert.True(nView.Refused);
        await b.Engine.LiftRefusalAsync(g.Id, nView.Id);
        await b.Engine.RequestMemberAsync(g.Id, nView.Id);
        await SettleAsync(a, b, n);
        nView = MemberNamed(await b.Engine.GroupAsync(g.Id), "N")!;
        Assert.False(nView.Refused);
        Assert.True(nView.Asked);
        var fromB = (await n.Engine.GroupAsync(g.Id)).Requests.Single(r => r.Name == "B");
        await n.Engine.AcceptRequestAsync(g.Id, fromB.Mail);
        await SettleAsync(a, b, n);
        Assert.Equal(PeerState.Paired, (await PeerNamedAsync(b, "N"))?.State);
    }

    // A and B, paired by hand, then A's group code: B joins through the
    // pairing (group-join), A's operator accepts, and the list follows. C,
    // paired with A too, is refused, and gives up on its side.
    [Fact]
    public async Task JoinWhenPaired()
    {
        var net = new Network();
        using var a = await ServerAsync(net, "A");
        using var b = await ServerAsync(net, "B");
        using var c = await ServerAsync(net, "C");
        var (bOnA, _) = await Kit.LinkAsync(a, b);
        var (cOnA, _) = await Kit.LinkAsync(a, c);
        await SettleAsync(a, b, c);
        var g = await a.Engine.CreateGroupAsync("Family");
        var (code, _) = await a.Engine.MakeCodeWithAsync(new CodeOptions { Group = g.Id, Uses = 3 });
        Assert.Equal("Family", (await b.Engine.ReadCodeAsync(code)).GroupName);
        await b.Engine.RedeemCodeAsync(code);
        Assert.Equal(MsfpError.AlreadyMember, await ErrorAsync(() => b.Engine.RedeemCodeAsync(code)));
        Assert.True((await b.Engine.GroupAsync(g.Id)).Joining);
        var join = Assert.Single((await a.Engine.GroupAsync(g.Id)).Joins);
        Assert.Equal((bOnA, "B"), (join.Peer, join.Name));
        Assert.Equal(bOnA, Assert.Single(a.Events.Of<GroupJoinWaiting>()).Peer);

        await a.Engine.AcceptJoinAsync(g.Id, bOnA);
        await SettleAsync(a, b, c);
        var gb = await b.Engine.GroupAsync(g.Id);
        Assert.False(gb.Joining);
        Assert.Equal((2, "member"), (gb.Members.Count, gb.Role));
        var onB = await PeerNamedAsync(b, "A");
        Assert.True(onB!.ByHand);
        Assert.Contains(g.Id, onB.Groups);
        var onA = await PeerNamedAsync(a, "B");
        Assert.True(onA!.ByHand);
        Assert.Contains(g.Id, onA.Groups);
        var ga = await a.Engine.GroupAsync(g.Id);
        Assert.Empty(ga.Joins);
        Assert.Equal(2, ga.Members.Count);

        // C: refused; nothing is sent, and its operator gives up.
        await c.Engine.RedeemCodeAsync(code);
        await a.Engine.RefuseJoinAsync(g.Id, cOnA);
        Assert.Equal(MsfpError.NotFound, await ErrorAsync(() => a.Engine.RefuseJoinAsync(g.Id, cOnA)));
        await c.Engine.LeaveGroupAsync(g.Id);
        Assert.Equal(MsfpError.NotFound, await ErrorAsync(() => c.Engine.GroupAsync(g.Id)));
        Assert.Equal(PeerState.Paired, (await PeerNamedAsync(c, "A"))?.State);
    }

    // A join through group-join that nobody answers is dropped after 30
    // days, on both sides.
    [Fact]
    public async Task JoinGivenUp()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        using var a = await ServerAsync(net, "A", clock);
        using var b = await ServerAsync(net, "B", clock);
        await Kit.LinkAsync(a, b);
        await SettleAsync(a, b);
        var g = await a.Engine.CreateGroupAsync("Family");
        var (code, _) = await a.Engine.MakeCodeWithAsync(new CodeOptions { Group = g.Id });
        await b.Engine.RedeemCodeAsync(code);
        clock.Advance(TimeSpan.FromDays(29));
        await a.Engine.RunOnceAsync();
        await b.Engine.RunOnceAsync();
        Assert.Single((await a.Engine.GroupAsync(g.Id)).Joins);
        await b.Engine.GroupAsync(g.Id);
        clock.Advance(TimeSpan.FromDays(2));
        await a.Engine.RunOnceAsync();
        await b.Engine.RunOnceAsync();
        await SettleAsync(a, b);
        Assert.Empty((await a.Engine.GroupAsync(g.Id)).Joins);
        Assert.Equal(MsfpError.NotFound, await ErrorAsync(() => b.Engine.GroupAsync(g.Id)));
    }

    // Code options: uses and lifetime in range; a code of several uses
    // serves each server once, then goes.
    [Fact]
    public async Task CodeOptionsAndUses()
    {
        var net = new Network();
        using var a = await ServerAsync(net, "A");
        using var b = await ServerAsync(net, "B");
        using var c = await ServerAsync(net, "C");
        Assert.Equal(MsfpError.CodeOptions, await ErrorAsync(() => a.Engine.MakeCodeWithAsync(new CodeOptions { Uses = 101 })));
        Assert.Equal(MsfpError.CodeOptions, await ErrorAsync(() => a.Engine.MakeCodeWithAsync(new CodeOptions { Lifetime = TimeSpan.FromDays(31) })));
        Assert.Equal(MsfpError.CodeOptions, await ErrorAsync(() => a.Engine.MakeCodeWithAsync(new CodeOptions { Lifetime = TimeSpan.FromSeconds(30) })));
        Assert.Equal(MsfpError.NotFound, await ErrorAsync(() => a.Engine.MakeCodeWithAsync(new CodeOptions { Group = Base64UrlText.Encode(new byte[32]) })));
        var (code, view) = await a.Engine.MakeCodeWithAsync(new CodeOptions { Uses = 2, Lifetime = TimeSpan.FromDays(30) });
        Assert.Equal((2, ""), (view.Uses, view.Group));
        Assert.Equal(view.Created + TimeSpan.FromDays(30), view.Expires);
        await b.Engine.RedeemCodeAsync(code);
        Assert.Equal(1, Assert.Single(await a.Engine.CodesAsync()).Uses);
        await c.Engine.RedeemCodeAsync(code);
        Assert.Empty(await a.Engine.CodesAsync());
    }
}
