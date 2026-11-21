using System.Text;
using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>
/// The groups part against msfp-spec's vectors/v0/groups.json: each
/// event's verdict, the state they give, the code with a group, the mail,
/// the join proof (msfp-go's wire/groups_test.go).
/// </summary>
public sealed class GroupTests
{
    [Fact]
    public void Vectors()
    {
        var v = TestVectors.Load("groups.json");
        var keys = v["keys"]!.AsObject().ToDictionary(k => k.Key, k => k.Value!.GetValue<string>());
        var now = DateTimeOffset.FromUnixTimeSeconds(v["now"]!.GetValue<long>());
        var s = new GroupState();
        var failures = new List<string>();
        var events = v["events"]!.AsArray().Select(e => e!.AsObject()).ToList();
        foreach (var e in events)
        {
            Assert.Equal(e.Str("event"), Base64UrlText.Encode(Encoding.UTF8.GetBytes(e.Str("payload"))));
            var got = s.Apply(new GroupEnvelope { Event = e.Str("event"), Signature = e.Str("signature") }, now);
            if ((got.Verdict == GroupVerdict.Kept) != e.Valid())
            {
                failures.Add($"{e.Str("name")}: {got.Verdict} ({got.Reason}), the vector says valid {e.Valid()}");
            }

            if (got.Id.Length > 0 && got.Id != e.Str("id"))
            {
                failures.Add($"{e.Str("name")}: id {got.Id}, want {e.Str("id")}");
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
            Address = want.Str("address"), Name = want.Str("name"), NextCommitment = want.Str("nextCommitment"),
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
        var s = new GroupState();
        var (founded, gid) = GroupEvents.Sign(a, new GroupEvent
        {
            Type = GroupEvents.Founded, Signer = aId, Member = aId, Seq = 1, Date = 1800000000, OwnerSeq = 1, Name = "Family",
            Settings = GroupSettings.Default, Nonce = Base64UrlText.Encode(new byte[16]),
        });
        Assert.Equal(GroupVerdict.Kept, s.Apply(founded, now).Verdict);
        Assert.Equal(GroupVerdict.Had, s.Apply(founded, now).Verdict);
        Assert.Equal(gid, s.Id);
        var rot = Rotations.SignRotation(a, a2, null, now);
        var statement = new JsonObject
        {
            ["oldKey"] = aId, ["newKey"] = Base64UrlText.Encode(a2.Public()), ["date"] = rot.Date,
            ["oldSignature"] = Base64UrlText.Encode(rot.OldSignature), ["newSignature"] = Base64UrlText.Encode(rot.NewSignature),
        };
        var a2Id = Base64UrlText.Encode(a2.Public());
        var (rotated, _) = GroupEvents.Sign(a2, new GroupEvent { Group = gid, Type = GroupEvents.Rotated, Signer = a2Id, Member = aId, Seq = 2, Date = 1800000000, Statement = statement });
        // The invitation under the old key, after the rotation, is refused; under the new one, kept.
        var (joinedOld, _) = GroupEvents.Sign(a, new GroupEvent { Group = gid, Type = GroupEvents.Joined, Signer = aId, Member = aId, Seq = 3, Date = 1800000000, OwnerSeq = 2, NewMember = bId, Name = "B" });
        var (joinedNew, _) = GroupEvents.Sign(a2, new GroupEvent { Group = gid, Type = GroupEvents.Joined, Signer = a2Id, Member = aId, Seq = 3, Date = 1800000000, OwnerSeq = 2, NewMember = bId, Name = "B" });
        Assert.Equal(GroupVerdict.Held, s.Apply(joinedNew, now).Verdict);
        Assert.Equal(GroupVerdict.Kept, s.Apply(rotated, now).Verdict);
        Assert.Equal(a2Id, s.Members[aId].Key);
        Assert.Equal(GroupVerdict.Refused, s.Apply(joinedOld, now).Verdict);
        Assert.Equal(GroupVerdict.Kept, s.Apply(joinedNew, now).Verdict);
        Assert.Equal(bId, s.Successor());
        Assert.Equal("B", s.Members[bId].Name);
    }
}
