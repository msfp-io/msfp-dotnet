using System.Text;
using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>
/// The groups part against msfp-spec's vectors/v0/groups.json: its events
/// judged as one set, the state they give, the code with a group, the mail,
/// the join proof (msfp-go's wire/groups_test.go).
/// </summary>
public sealed class GroupTests
{
    [Fact]
    public void Vectors()
    {
        var v = TestVectors.Load("msfp/v0/groups.json");
        var keys = v["keys"]!.AsObject().ToDictionary(k => k.Key, k => k.Value!.GetValue<string>());
        var now = DateTimeOffset.FromUnixTimeSeconds(v["now"]!.GetValue<long>());
        var group = v.Str("group");
        var failures = new List<string>();
        var events = v["events"]!.AsArray().Select(e => e!.AsObject()).ToList();
        var kept = new List<GroupEnvelope>();
        foreach (var e in events)
        {
            Assert.Equal(e.Str("event"), Base64UrlText.Encode(Encoding.UTF8.GetBytes(e.Str("payload"))));
            var ev = new GroupEnvelope { Event = e.Str("event"), Signature = e.Str("signature") };
            var check = GroupEvents.Check(ev, group, now);
            if (check.Kept)
            {
                kept.Add(ev);
            }

            if (check.Id.Length > 0 && check.Id != e.Str("id"))
            {
                failures.Add($"{e.Str("name")}: id {check.Id}, want {e.Str("id")}");
            }
        }

        // valid: the event counts in the state the whole set gives.
        var (s, results) = GroupState.Compute(group, kept);
        foreach (var e in events)
        {
            var r = results.GetValueOrDefault(e.Str("id"));
            if ((r?.Verdict == GroupVerdict.Counted) != e.Valid())
            {
                failures.Add($"{e.Str("name")}: {r?.Verdict.ToString() ?? "not kept"} ({r?.Reason}), the vector says valid {e.Valid()}");
            }
        }

        TestVectors.NoFailures(failures, events.Count, "groups.json events");
        var st = v["state"]!.AsObject();
        List<string> Sorted(string name) => st[name]!.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(st.Str("id"), s.Id);
        Assert.Equal(st.Str("name"), s.Name);
        Assert.Equal(st.Str("owner"), s.Owner);
        Assert.Equal(st.Str("heir"), s.Heir);
        var settings = st["settings"]!.AsObject();
        Assert.Equal(new GroupSettings(settings["waitDays"]!.GetValue<long>(), settings["capPerDay"]!.GetValue<long>()), s.Settings);
        Assert.Equal(Sorted("members"), s.Members.Keys.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(Sorted("admins"), s.Members.Values.Where(m => m.Admin).Select(m => m.Id).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(Sorted("banned"), s.Banned.Keys.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(Sorted("excluded"), s.Excluded.Order(StringComparer.Ordinal).ToList());
        foreach (var (id, key) in st["keys"]!.AsObject())
        {
            if (s.Members.TryGetValue(id, out var m))
            {
                Assert.Equal(key!.GetValue<string>(), m.Key);
            }
        }

        // The code, made by B, with the group's ID and name.
        var c = v["code"]!.AsObject();
        var code = PairingCode.Parse(c.Str("text"));
        Assert.Equal(v.Str("group"), Base64UrlText.Encode(code.Group));
        Assert.Equal(v.Str("groupName"), code.GroupName);
        Assert.Equal(c.Str("address"), code.Address);
        Assert.Equal(c.Str("text"), code.ToString());
        Assert.Equal(c.Str("secretHash"), Base64UrlText.Encode(Signing.SecretHash(code.Secret)));

        // The join proof, by D.
        var jp = v["joinProof"]!.AsObject();
        var redeemer = Base64UrlText.Decode(keys[jp.Str("redeemer")], 32)!;
        Assert.Equal(jp.Str("value"), GroupEvents.JoinProof(code.Secret, redeemer));
        Assert.True(GroupEvents.CheckJoinProof(code.Secret, redeemer, jp.Str("value")));
        Assert.False(GroupEvents.CheckJoinProof(code.Secret, Base64UrlText.Decode(keys["a"], 32)!, jp.Str("value")));

        // N's mail to C: read under N's key, opened with C's private key.
        var mv = v["mail"]!.AsObject();
        var env = new GroupEnvelope { Mail = mv.Str("mail"), Signature = mv.Str("signature") };
        Assert.Null(GroupEvents.ReadMail(env, Base64UrlText.Decode(keys["c"], 32)));
        var mail = GroupEvents.ReadMail(env, Base64UrlText.Decode(keys["n"], 32));
        Assert.NotNull(mail);
        Assert.Equal(mv.Str("payload"), Encoding.UTF8.GetString(Base64UrlText.Decode(mv.Str("mail"))!));
        var opened = GroupEvents.OpenMail(mail, Convert.FromHexString(mv.Str("recipientX25519Private")));
        Assert.NotNull(opened);
        var want = JsonNode.Parse(mv.Str("opened"))!.AsObject();
        Assert.Equal(new SealedContent
        {
            Address = want.Str("address"), NextCommitment = want.Str("nextCommitment"),
            Inbound = want["inbound"]!.GetValue<bool>(),
        }, opened);
    }

    // A mail sealed here opens with the recipient's key, and only with it.
    [Fact]
    public void MailRoundTrip()
    {
        var from = MsfpKey.Generate();
        var (priv, pub) = GroupEvents.MailKeyPair(new byte[32]);
        var other = new byte[32];
        other[31] = 1;
        var m = new GroupMail
        {
            Group = Base64UrlText.Encode(new byte[32]), Id = Base64UrlText.Encode(new byte[16]), Kind = "request",
            From = Base64UrlText.Encode(from.Public()), To = Base64UrlText.Encode(new byte[32]), Expires = 1800000000,
        };
        var content = new SealedContent
        {
            Address = "https://n.example.org", NextCommitment = Base64UrlText.Encode(new byte[32]), Inbound = true,
            Certificates = ["JN-hf1COiASAAUsiYeHXMNVvNnxYR2dKYnBT2Zx_6Fo"],
        };
        var env = GroupEvents.SealMail(from, m, pub, content);
        var read = GroupEvents.ReadMail(env, from.Public());
        Assert.NotNull(read);
        Assert.Equal(content, GroupEvents.OpenMail(read, priv));
        Assert.Null(GroupEvents.OpenMail(read, other));
        Assert.Null(GroupEvents.OpenMail(read with { From = Base64UrlText.Encode(new byte[32]) }, priv)); // the aad binds from
    }

    // Events signed here read back as they were, and a state built from
    // them follows a rotation: the member's new key signs from then on.
    [Fact]
    public void SignedEventsAndRotation()
    {
        var a = MsfpKey.Generate();
        var a2 = MsfpKey.Generate();
        var b = MsfpKey.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var aId = Base64UrlText.Encode(a.Public());
        var bId = Base64UrlText.Encode(b.Public());
        var (founded, gid) = GroupEvents.Sign(a, new GroupEvent
        {
            Type = GroupEvents.Founded, Signer = aId, Member = aId, Seq = 1, Date = 1800000000, OwnerSeq = 1, Name = "Family",
            Settings = GroupSettings.Default, Nonce = Base64UrlText.Encode(new byte[16]),
        });
        Assert.True(GroupEvents.Check(founded, gid, now).Kept);
        var (s, results) = GroupState.Compute(gid, [founded, founded]);
        Assert.Equal(GroupVerdict.Counted, Assert.Single(results).Value.Verdict); // once, however often it came
        Assert.Equal(gid, s.Id);
        var rot = Rotations.SignRotation(a, a2, null, now);
        var statement = new JsonObject
        {
            ["oldKey"] = aId, ["newKey"] = Base64UrlText.Encode(a2.Public()), ["date"] = rot.Date,
            ["oldSignature"] = Base64UrlText.Encode(rot.OldSignature), ["newSignature"] = Base64UrlText.Encode(rot.NewSignature),
        };
        var a2Id = Base64UrlText.Encode(a2.Public());
        var (rotated, rotatedId) = GroupEvents.Sign(a2, new GroupEvent { Group = gid, Type = GroupEvents.Rotated, Signer = a2Id, Member = aId, Seq = 2, Date = 1800000000, Statement = statement });
        // The invitation under the old key, after the rotation, is forged; under the new one, it counts.
        var (joinedOld, oldId) = GroupEvents.Sign(a, new GroupEvent { Group = gid, Type = GroupEvents.Joined, Signer = aId, Member = aId, Seq = 3, Date = 1800000000, OwnerSeq = 2, NewMember = bId, Name = "B" });
        var (joinedNew, newId) = GroupEvents.Sign(a2, new GroupEvent { Group = gid, Type = GroupEvents.Joined, Signer = a2Id, Member = aId, Seq = 3, Date = 1800000000, OwnerSeq = 2, NewMember = bId, Name = "B" });
        (_, results) = GroupState.Compute(gid, [founded, joinedNew]);
        Assert.Equal(GroupVerdict.Pending, results[newId].Verdict); // the rotation isn't there
        (s, results) = GroupState.Compute(gid, [joinedOld, joinedNew, rotated, founded]);
        Assert.Equal(GroupVerdict.Counted, results[rotatedId].Verdict);
        Assert.Equal(a2Id, s.Members[aId].Key);
        Assert.Equal(GroupVerdict.Forged, results[oldId].Verdict);
        Assert.Equal(GroupVerdict.Counted, results[newId].Verdict);
        Assert.Empty(s.Excluded); // an old key's event is no equivocation
        Assert.Equal(bId, s.Successor());
        Assert.Equal("B", s.Members[bId].Name);
    }

    // A member's revoked event names one of its own keys only (section 3.3).
    [Fact]
    public void RevokedOwnKeyOnly()
    {
        var a = MsfpKey.Generate();
        var other = MsfpKey.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var aId = Base64UrlText.Encode(a.Public());
        var (founded, gid) = GroupEvents.Sign(a, new GroupEvent
        {
            Type = GroupEvents.Founded, Signer = aId, Member = aId, Seq = 1, Date = 1800000000, OwnerSeq = 1, Name = "F",
            Settings = GroupSettings.Default, Nonce = Base64UrlText.Encode(new byte[16]),
        });
        JsonObject Statement(MsfpKey k)
        {
            var r = Rotations.SignRevocation(k, now);
            return new JsonObject { ["key"] = Base64UrlText.Encode(r.Key), ["date"] = r.Date, ["signature"] = Base64UrlText.Encode(r.Signature) };
        }

        var (foreign, foreignId) = GroupEvents.Sign(a, new GroupEvent { Group = gid, Type = GroupEvents.Revoked, Signer = aId, Member = aId, Seq = 2, Date = 1800000000, LastSeq = 1, Statement = Statement(other) });
        Assert.Equal(GroupVerdict.Refused, GroupState.Compute(gid, [founded, foreign]).Results[foreignId].Verdict);
        var (own, ownId) = GroupEvents.Sign(a, new GroupEvent { Group = gid, Type = GroupEvents.Revoked, Signer = aId, Member = aId, Seq = 2, Date = 1800000000, LastSeq = 1, Statement = Statement(a) });
        Assert.Equal(GroupVerdict.Counted, GroupState.Compute(gid, [founded, own]).Results[ownId].Verdict);
    }

    // An owner event replayed without counting takes its ownerSeq all the
    // same (groups section 3.5): the owner names B admin while B leaves, B's
    // left replayed first, so the admin event can't count; the owner's next
    // event is still ready, and counts.
    [Fact]
    public void OwnerEventNotCountingTakesOwnerSeq()
    {
        var a = MsfpKey.Generate();
        var b = MsfpKey.Generate();
        var am = Base64UrlText.Encode(a.Public());
        var bm = Base64UrlText.Encode(b.Public());
        const long now = 1800000000;
        var (founded, gid) = GroupEvents.Sign(a, new GroupEvent
        {
            Type = GroupEvents.Founded, Signer = am, Member = am, Seq = 1, Date = now, OwnerSeq = 1, Name = "F",
            Settings = GroupSettings.Default, Nonce = Base64UrlText.Encode(new byte[16]),
        });
        var (joined, _) = GroupEvents.Sign(a, new GroupEvent { Group = gid, Type = GroupEvents.Joined, Signer = am, Member = am, Seq = 2, Date = now, OwnerSeq = 2, NewMember = bm, Name = "B" });
        var (left, leftId) = GroupEvents.Sign(b, new GroupEvent { Group = gid, Type = GroupEvents.Left, Signer = bm, Member = bm, Seq = 1, Date = now });
        GroupEnvelope admin;
        string adminId;
        for (var d = now; ; d++)
        {
            (admin, adminId) = GroupEvents.Sign(a, new GroupEvent { Group = gid, Type = GroupEvents.Admin, Signer = am, Member = am, Seq = 3, Date = d, OwnerSeq = 3, Target = bm, On = true });
            if (string.CompareOrdinal(leftId, adminId) < 0)
            {
                break; // B's left is replayed first
            }
        }

        var (settings, settingsId) = GroupEvents.Sign(a, new GroupEvent
        {
            Group = gid, Type = GroupEvents.Settings, Signer = am, Member = am, Seq = 4, Date = now, OwnerSeq = 4, Name = "G",
            Settings = new GroupSettings(1, 5),
        });
        var r = GroupState.Compute(gid, [founded, joined, left, admin, settings]);
        Assert.NotEqual(GroupVerdict.Counted, r.Results[adminId].Verdict);
        Assert.Equal(GroupVerdict.Counted, r.Results[settingsId].Verdict);
        Assert.Equal((4L, "G"), (r.State.OwnerSeq, r.State.Name));
    }

    // The vectors' events, in any order: the same state and the same
    // verdicts (groups section 3.5).
    [Fact]
    public void OrderIndependent()
    {
        var v = TestVectors.Load("msfp/v0/groups.json");
        var group = v.Str("group");
        var envs = v["events"]!.AsArray().Select(e => e!.AsObject()).Select(e => new GroupEnvelope { Event = e.Str("event"), Signature = e.Str("signature") }).ToList();
        static string Summary(GroupState s, IReadOnlyDictionary<string, GroupEventResult> r) => string.Join("\n",
            s.Owner, s.Heir, s.OwnerSeq, s.Settings, s.Name, s.Banned.Count,
            string.Join(",", s.Members.Values.Select(m => $"{m.Id}:{m.Admin}:{m.Key}").Order(StringComparer.Ordinal)),
            string.Join(",", s.Excluded.Order(StringComparer.Ordinal)),
            string.Join(",", r.Select(x => $"{x.Key}:{x.Value.Verdict}").Order(StringComparer.Ordinal)));
        var (state, results) = GroupState.Compute(group, envs);
        var want = Summary(state, results);
        var rng = new Random(12);
        for (var i = 0; i < 50; i++)
        {
            var shuffled = envs.ToArray();
            rng.Shuffle(shuffled);
            (state, results) = GroupState.Compute(group, shuffled);
            Assert.Equal(want, Summary(state, results));
        }
    }

    // A member signs its next event after its highest seq kept, counted or
    // not; group-sync's seq is the last one replayed.
    [Fact]
    public void HighestCountsEveryKeptSeq()
    {
        var a = MsfpKey.Generate();
        var aId = Base64UrlText.Encode(a.Public());
        var (founded, gid) = GroupEvents.Sign(a, new GroupEvent
        {
            Type = GroupEvents.Founded, Signer = aId, Member = aId, Seq = 1, Date = 1800000000, OwnerSeq = 1, Name = "F",
            Settings = GroupSettings.Default, Nonce = Base64UrlText.Encode(new byte[16]),
        });
        // seq 3 waits for a seq 2 that never came.
        var (later, laterId) = GroupEvents.Sign(a, new GroupEvent { Group = gid, Type = GroupEvents.Profile, Signer = aId, Member = aId, Seq = 3, Date = 1800000000, Name = "A" });
        var (s, results) = GroupState.Compute(gid, [founded, later]);
        Assert.Equal(GroupVerdict.Pending, results[laterId].Verdict);
        Assert.Equal(1, s.Seq(aId));
        Assert.Equal(3, s.Highest(aId));
    }
}
