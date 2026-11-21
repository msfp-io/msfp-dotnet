namespace Msfp.Tests;

// Relations (MSFP core section 7.7), as msfp-go's TestRelations: a plain
// code gives the individual relation; a group's code the group relation
// only. Between group members, one asks for the individual relation and the
// other accepts or refuses; ending it keeps the link while the group holds
// it. Leaving ends a link nothing else holds.
public sealed class RelationsTests
{
    private static async Task<TestServer> ServerAsync(Network net, string name) =>
        await TestServer.StartAsync(net, $"https://{name.ToLowerInvariant()}.example.org", name);

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

    private static async Task<PeerView?> ViewAsync(TestServer s, string name) =>
        (await s.Engine.PeersAsync()).FirstOrDefault(p => p.Name == name);

    private static async Task JoinGroupAsync(TestServer inviter, TestServer member, string group)
    {
        var (code, _) = await inviter.Engine.MakeCodeWithAsync(new CodeOptions { Group = group });
        await member.Engine.RedeemCodeAsync(code);
        var ours = (await inviter.Engine.PeersAsync()).Single(p => p.State == PeerState.Ours);
        await inviter.Engine.AcceptAsync(ours.Id);
        await SettleAsync(inviter, member);
    }

    [Fact]
    public async Task Relations()
    {
        var net = new Network();
        using var a = await ServerAsync(net, "A");
        using var b = await ServerAsync(net, "B");
        using var c = await ServerAsync(net, "C");

        // A plain code: individual on both sides.
        await Kit.LinkAsync(a, c);
        await SettleAsync(a, c);
        var aC = (await ViewAsync(a, "C"))!;
        Assert.True(aC.Individual);
        Assert.Empty(aC.Groups);
        Assert.True((await ViewAsync(c, "A"))!.Individual);

        // A group's code: the group relation only.
        var g = await a.Engine.CreateGroupAsync("Family");
        await JoinGroupAsync(a, b, g.Id);
        var aB = (await ViewAsync(a, "B"))!;
        var bA = (await ViewAsync(b, "A"))!;
        Assert.Equal((false, false, 1), (aB.Individual, aB.IndividualAsks, aB.Groups.Count));
        Assert.Equal((false, false, 1), (bA.Individual, bA.IndividualAsked, bA.Groups.Count));

        // B asks, A accepts.
        var askedView = await b.Engine.AskIndividualAsync(bA.Id);
        Assert.True(askedView.IndividualAsked, $"{askedView}");
        await SettleAsync(a, b);
        Assert.True((await ViewAsync(a, "B"))!.IndividualAsks);
        await a.Engine.AskIndividualAsync(aB.Id);
        await SettleAsync(a, b);
        Assert.True((await ViewAsync(a, "B"))!.Individual);
        Assert.True((await ViewAsync(b, "A"))!.Individual);

        // A ends it: the group still holds the link, and B's side ends too.
        await a.Engine.EndIndividualAsync(aB.Id);
        await SettleAsync(a, b);
        var after = (await ViewAsync(b, "A"))!;
        Assert.Equal((false, false, false, 1), (after.Individual, after.IndividualAsks, after.IndividualAsked, after.Groups.Count));
        Assert.False((await ViewAsync(a, "B"))!.Individual);

        // B asks again, A refuses: privately, B's ask stays shown on B.
        await b.Engine.AskIndividualAsync(bA.Id);
        await SettleAsync(a, b);
        await a.Engine.RefuseIndividualAsync(aB.Id);
        await SettleAsync(a, b);
        var refused = (await ViewAsync(a, "B"))!;
        Assert.Equal((false, false), (refused.IndividualAsks, refused.Individual));
        Assert.True((await ViewAsync(b, "A"))!.IndividualAsked);

        // B leaves without asking: the link nothing else holds ends, on both
        // sides; A's individual link with C stays.
        await b.Engine.EndIndividualAsync(bA.Id);
        await SettleAsync(a, b);
        await b.Engine.LeaveGroupAsync(g.Id);
        await SettleAsync(a, b, c);
        Assert.Null(await ViewAsync(b, "A"));
        Assert.Null(await ViewAsync(a, "B"));
        Assert.True((await ViewAsync(a, "C"))!.Individual);

        // Not kept away: a new code for the group brings B back.
        await JoinGroupAsync(a, b, g.Id);
        Assert.Single((await ViewAsync(b, "A"))!.Groups);
    }
}
