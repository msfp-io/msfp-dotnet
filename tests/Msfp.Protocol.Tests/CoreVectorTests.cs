using System.Text;
using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>
/// MSFP's version 1 test vectors of the core (keys.json, code.json,
/// exchanges.json, rotation.json, revocation.json, strict.json) and the
/// canonical forms of v0-strict.json that hold for core 1 (core section
/// 17), recomputed with the code: msfp-go's wire/vectors_test.go,
/// json_test.go, rotation_test.go and uncallable_test.go.
/// </summary>
public sealed class CoreVectorTests
{
    private static JsonObject LoadV0() =>
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "v0-strict.json")))!.AsObject();

    internal static Dictionary<string, MsfpKey> VectorKeys()
    {
        var keys = new Dictionary<string, MsfpKey>();
        foreach (var (name, k) in TestVectors.Load("keys.json")["keys"]!.AsObject())
        {
            var key = MsfpKey.FromSeed(Convert.FromHexString(k!["seed"]!.GetValue<string>()));
            Assert.Equal(k["public"]!.GetValue<string>(), Base64UrlText.Encode(key.Public()));
            Assert.Equal(k["fingerprint"]!.GetValue<string>(), key.Fingerprint);
            Assert.Equal(k["commitment"]!.GetValue<string>(), Base64UrlText.Encode(Keys.Commitment(key.Public())));
            Assert.Equal(key.Fingerprint, Keys.CommitmentFingerprint(Keys.Commitment(key.Public())));
            keys[name] = key;
        }

        Assert.NotEmpty(keys);
        return keys;
    }

    internal sealed record Exchange(JsonObject X)
    {
        public string S(string n) => X[n]!.GetValue<string>();

        public int Status => X["status"]!.GetValue<int>();

        public byte[] Body => Encoding.UTF8.GetBytes(S("body"));

        public byte[] AnswerBody => Encoding.UTF8.GetBytes(S("answerBody"));

        public DateTimeOffset At => DateTimeOffset.FromUnixTimeSeconds(long.Parse(S("date"), System.Globalization.CultureInfo.InvariantCulture));

        public HeaderList Headers(MsfpKey signer)
        {
            var h = new HeaderList().Add("Content-Type", "application/json")
                .Add(Signing.HeaderKey, Base64UrlText.Encode(signer.Public()))
                .Add(Signing.HeaderDate, S("date")).Add(Signing.HeaderNonce, S("nonceText"))
                .Add(Signing.HeaderSignature, S("signature"));
            return h;
        }
    }

    internal static List<Exchange> Exchanges() =>
        TestVectors.Load("exchanges.json")["exchanges"]!.AsArray().Select(x => new Exchange(x!.AsObject())).ToList();

    [Fact]
    public void Keys_FromSeeds() => VectorKeys();

    [Fact]
    public void Certificates_Pins()
    {
        foreach (var (name, c) in TestVectors.Load("keys.json")["certificates"]!.AsObject())
        {
            var spki = Convert.FromHexString(c!["spki"]!.GetValue<string>());
            // An Ed25519 SubjectPublicKeyInfo (RFC 8410): a fixed DER prefix, then the key.
            var fromSeed = Convert.FromHexString("302a300506032b6570032100")
                .Concat(MsfpKey.FromSeed(Convert.FromHexString(c["seed"]!.GetValue<string>())).Public()).ToArray();
            Assert.True(spki.SequenceEqual(fromSeed), name);
            Assert.True(Base64UrlText.Encode(System.Security.Cryptography.SHA256.HashData(spki)) == c["pin"]!.GetValue<string>(), name);
            Assert.True(Pins.Valid(c["pin"]!.GetValue<string>()));
        }
    }

    [Fact]
    public void Code()
    {
        var keys = VectorKeys();
        var v = TestVectors.Load("code.json");
        var c = v["code"]!.AsObject();
        var secret = Convert.FromHexString(c.Str("secret"));
        var code = new PairingCode(c.Str("address"), keys[c.Str("key")].Public(), secret,
            DateTimeOffset.FromUnixTimeSeconds(c["expires"]!.GetValue<long>()));
        Assert.Equal(c.Str("text"), code.ToString());
        Assert.Equal(code, PairingCode.Parse(c.Str("text")));
        Assert.Equal(code, PairingCode.Parse("  " + c.Str("text").Insert(20, "\r\n ") + "\n"));
        Assert.Equal(c.Str("secretHash"), Base64UrlText.Encode(Signing.SecretHash(secret)));
        var p = v["proof"]!.AsObject();
        var redeemer = keys[p.Str("redeemer")].Public();
        Assert.Equal(p.Str("value"), Signing.PairingProof(secret, redeemer, p.Str("address")));
        Assert.True(Signing.CheckPairingProof(secret, redeemer, p.Str("address"), p.Str("value")));
        Assert.False(Signing.CheckPairingProof(secret, redeemer, p.Str("address") + "x", p.Str("value")));
    }

    [Fact]
    public void Exchanges_SignedBothWays()
    {
        var keys = VectorKeys();
        var all = Exchanges();
        Assert.NotEmpty(all);
        foreach (var x in all)
        {
            CheckExchange(keys, x);
        }
    }

    internal static void CheckExchange(Dictionary<string, MsfpKey> keys, Exchange x)
    {
        var name = x.S("name");
        var signer = keys[x.S("signer")];
        var answerer = keys[x.S("answerer")];
        Assert.True(Base64UrlText.Encode(Convert.FromHexString(x.S("nonce"))) == x.S("nonceText"), name + " nonce");
        var msg = Signing.RequestMessage(x.S("method"), x.S("host"), x.S("target"), x.S("date"), x.S("nonceText"), x.Body);
        Assert.Equal(x.S("message"), Encoding.UTF8.GetString(msg));
        Assert.Equal(x.S("signature"), Base64UrlText.Encode(signer.Sign(msg)));
        var signed = Signing.SignRequestWithNonce(x.S("method"), x.S("host"), x.S("target"), x.Body, signer, x.At,
            Convert.FromHexString(x.S("nonce")));
        Assert.Equal(x.S("signature"), signed.Signature);

        // The request as it arrives, checked as a server checks it.
        var ver = new Verifier { Host = x.S("host"), Seen = new NonceMemory(x.At.AddMinutes(-1)) };
        var got = ver.Check(x.S("method"), x.S("target"), x.Headers(signer), x.Body, x.At);
        Assert.True(Keys.Same(got.Key, signer.Public()), name);
        // Again: a replay.
        Assert.Throws<ProtocolException>(() => ver.Check(x.S("method"), x.S("target"), x.Headers(signer), x.Body, x.At));
        // With a header of the first test version beside: refused.
        var old = x.Headers(signer).Add("Overfin-Nonce", x.S("nonceText"));
        Assert.Throws<ProtocolException>(() => new Verifier { Host = x.S("host") }.Check(x.S("method"), x.S("target"), old, x.Body, x.At));
        // For another host, or with a query: refused.
        Assert.Throws<ProtocolException>(() => new Verifier { Host = "other.example.org" }.Check(x.S("method"), x.S("target"), x.Headers(signer), x.Body, x.At));
        Assert.Throws<ProtocolException>(() => new Verifier { Host = x.S("host") }.Check(x.S("method"), x.S("target") + "?a", x.Headers(signer), x.Body, x.At));
        // But under the former host while a move lasts.
        var moved = new Verifier { Host = "new.example.org", Former = () => x.S("host") };
        Assert.True(moved.Check(x.S("method"), x.S("target"), x.Headers(signer), x.Body, x.At).Former);

        var ct = x.S("answerContentType");
        Assert.Equal(x.S("answerMessage"), Encoding.UTF8.GetString(Signing.ResponseMessage(x.S("nonceText"), x.Status, ct, x.AnswerBody)));
        Assert.Equal(x.S("answerSignature"), Signing.SignResponse(answerer, x.S("nonceText"), x.Status, ct, x.AnswerBody));
        Assert.True(Signing.CheckResponse(answerer.Public(), x.S("nonceText"), x.Status, ct, x.AnswerBody, x.S("answerSignature")), name);
        // Its Content-Type changed on the way, its parameters aside: refused.
        Assert.False(Signing.CheckResponse(answerer.Public(), x.S("nonceText"), x.Status, "text/plain", x.AnswerBody, x.S("answerSignature")));
        Assert.True(Signing.CheckResponse(answerer.Public(), x.S("nonceText"), x.Status, ct + "; charset=utf-8", x.AnswerBody, x.S("answerSignature")));
        Assert.False(Signing.CheckResponse(signer.Public(), x.S("nonceText"), x.Status, ct, x.AnswerBody, x.S("answerSignature")));
    }

    [Fact]
    public void Hello_AToB()
    {
        var x = Exchanges().Single(e => e.S("name") == "hello");
        var a = Hello.Read(x.Body, answer: false);
        Assert.Equal("paired", a.State);
        Assert.Equal("https://a.example.org", a.Address);
        Assert.Equal([1], a.Versions.Core);
        Assert.Equal(4, a.Versions.Parts.Count);
        Assert.Single(a.Certificates);
        var b = Hello.Read(x.AnswerBody, answer: true);
        Assert.Equal("paired", b.State);
        Assert.Equal([1], b.Versions.Core);
        Assert.Single(b.Certificates);
        var agreed = Versions.Agreed(a.Versions, b.Versions);
        Assert.NotNull(agreed);
        Assert.Equal(1, agreed.Core);
        Assert.Equal(new Dictionary<string, int> { ["catalog"] = 1 }, agreed.Parts);
    }

    [Fact]
    public void Strict_AddressesAndCodes()
    {
        var failures = new List<string>();
        var addresses = TestVectors.Cases("strict.json", "addresses");
        foreach (var c in addresses)
        {
            if (Address.Valid(c.Str("text")) != c.Valid())
            {
                failures.Add($"address {c.Str("text")}: want valid {c.Valid()}");
            }
        }

        var codes = TestVectors.Cases("strict.json", "codes");
        foreach (var c in codes)
        {
            var ok = true;
            try
            {
                PairingCode.Parse(c.Str("text"));
            }
            catch (ProtocolException)
            {
                ok = false;
            }

            if (ok != c.Valid())
            {
                failures.Add($"code {c.Str("name")}: want valid {c.Valid()}");
            }
        }

        // With the test switch on, plain http is an address, nothing else changes.
        lock (PlainHttpLock.Gate)
        {
            Address.AllowPlainHttpForTests(true);
            try
            {
                foreach (var c in addresses)
                {
                    var valid = c.Valid() || c.Str("text") == "http://192.0.2.10:8096";
                    if (Address.Valid(c.Str("text")) != valid)
                    {
                        failures.Add($"address {c.Str("text")}, plain http allowed: want valid {valid}");
                    }
                }
            }
            finally
            {
                Address.AllowPlainHttpForTests(false);
            }
        }

        TestVectors.NoFailures(failures, addresses.Count * 2 + codes.Count, "strict.json addresses and codes");
    }

    [Fact]
    public void Strict_UncallableAddresses()
    {
        var failures = new List<string>();
        var cases = TestVectors.Cases("strict.json", "uncallableAddresses");
        foreach (var c in cases)
        {
            if (Uncallables.Valid(c.Str("text")) != c.Valid())
            {
                failures.Add($"{c.Str("text")}: want valid {c.Valid()}");
            }

            if (Uncallables.Valid(c.Str("text")) && !Address.Valid(c.Str("text")))
            {
                failures.Add($"{c.Str("text")}: not an address");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "uncallableAddresses");
        for (var i = 0; i < 50; i++)
        {
            var a = Uncallables.NewAddress();
            Assert.True(Uncallables.Valid(a), a);
            Assert.True(Address.Valid(a), a);
        }
    }

    [Fact]
    public void Strict_HelloVersions()
    {
        var failures = new List<string>();
        var cases = TestVectors.Cases("strict.json", "helloVersions");
        foreach (var c in cases)
        {
            Hello? h = null;
            try
            {
                h = Hello.Read(Encoding.UTF8.GetBytes(c.Str("text")), answer: false);
            }
            catch (ProtocolException)
            {
            }

            if ((h is not null) != c.Valid())
            {
                failures.Add($"{c.Str("name")}: want valid {c.Valid()}");
                continue;
            }

            if (h is null)
            {
                continue;
            }

            // As Go's encoding/json writes the kept versions: core, then
            // parts with names sorted, each list sorted.
            var parts = string.Join(",", h.Versions.Parts.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"\"{p.Key}\":[{string.Join(",", p.Value.Order())}]"));
            var got = $"{{\"core\":[{string.Join(",", h.Versions.Core.Order())}],\"parts\":{{{parts}}}}}";
            if (got != c.Str("output"))
            {
                failures.Add($"{c.Str("name")}: kept {got}, want {c.Str("output")}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "helloVersions");
    }

    [Fact]
    public void V0Strict_KeysSignaturesBase64Dates()
    {
        var v = LoadV0();
        var failures = new List<string>();
        var total = 0;
        foreach (var c in v["keys"]!.AsArray().Select(n => n!.AsObject()))
        {
            total++;
            var key = Base64UrlText.Decode(c.Str("text"), 32) ?? [];
            if (Ed25519Strict.ValidKey(key) != c.Valid())
            {
                failures.Add($"key {c.Str("name")}: want valid {c.Valid()}");
            }

            // As a request's MSFP-Key, and as a code's key.
            var now = DateTimeOffset.UtcNow;
            var h = new HeaderList();
            Signing.SignRequest("POST", "a.example.org", "/api/v1/federation/pair", "{}"u8, MsfpKey.Generate(), now).Apply(h);
            h.Set(Signing.HeaderKey, c.Str("text"));
            Assert.Throws<ProtocolException>(() => new Verifier { Host = "a.example.org" }.Check("POST", "/api/v1/federation/pair", h, "{}"u8, now));
            var code = new PairingCode("https://a.example.org", key, new byte[32], DateTimeOffset.FromUnixTimeSeconds(1800000000));
            var parsed = true;
            try
            {
                PairingCode.Parse(code.ToString());
            }
            catch (ProtocolException)
            {
                parsed = false;
            }

            if (parsed != c.Valid())
            {
                failures.Add($"key {c.Str("name")} in a code: want valid {c.Valid()}");
            }
        }

        foreach (var c in v["signatures"]!.AsArray().Select(n => n!.AsObject()))
        {
            total++;
            var pub = Base64UrlText.Decode(c.Str("public"), 32) ?? [];
            var sig = Base64UrlText.Decode(c.Str("signature"), 64) ?? [];
            if (Ed25519Strict.Verify(pub, Encoding.UTF8.GetBytes(c.Str("message")), sig) != c.Valid())
            {
                failures.Add($"signature {c.Str("name")}: want valid {c.Valid()}");
            }
        }

        foreach (var c in v["dates"]!.AsArray().Select(n => n!.AsObject()))
        {
            total++;
            if (Signing.TryParseDate(c.Str("text"), out _) != c.Valid())
            {
                failures.Add($"date {c.Str("text")}: want valid {c.Valid()}");
            }
        }

        foreach (var c in v["base64url"]!.AsArray().Select(n => n!.AsObject()))
        {
            total++;
            var b = Base64UrlText.Decode(c.Str("text"));
            var ok = b is not null && (b.Length == 32 || b.Length == 16);
            if (ok != c.Valid())
            {
                failures.Add($"base64url {c.Str("name")}: want valid {c.Valid()}");
            }
        }

        TestVectors.NoFailures(failures, total, "v0-strict.json keys, signatures, dates, base64url");
    }

    [Fact]
    public void V0Strict_JsonAndIntegers()
    {
        var v = LoadV0();
        var failures = new List<string>();
        var total = 0;
        foreach (var c in v["json"]!.AsArray().Select(n => n!.AsObject()))
        {
            total++;
            var body = c.ContainsKey("hex") ? c.Hex() : Encoding.UTF8.GetBytes(c.Str("text"));
            // As Go reads it into struct { state string; certificates []string }.
            var asHello = Try(() =>
            {
                var m = StrictJson.ReadRequest(body, ["state", "certificates"]);
                m.GetString("state");
                m.GetStrings("certificates");
            });
            // And into a map, which reads all of it.
            var asMap = Try(() =>
            {
                var all = StrictJson.ParseObject(body, nulls: true).Select(p => p.Key).ToList();
                StrictJson.ReadRequest(body, all);
            });
            if (asHello != c.Valid() || asMap != c.Valid())
            {
                failures.Add($"json {c.Str("name")}: want valid {c.Valid()} (as hello {asHello}, as map {asMap})");
            }
        }

        foreach (var c in v["integers"]!.AsArray().Select(n => n!.AsObject()))
        {
            total++;
            var ok = Try(() => StrictJson.ReadRequest(Encoding.UTF8.GetBytes("{\"n\":" + c.Str("text") + "}"), ["n"]).GetInteger("n"));
            if (ok != c.Valid())
            {
                failures.Add($"integer {c.Str("text")}: want valid {c.Valid()}");
            }
        }

        TestVectors.NoFailures(failures, total, "v0-strict.json json and integers");
    }

    private static bool Try(Action a)
    {
        try
        {
            a();
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    [Fact]
    public void Rotation()
    {
        var keys = VectorKeys();
        var v = TestVectors.Load("rotation.json");
        var r = v["rotation"]!.AsObject();
        MsfpKey old = keys[r.Str("from")], @new = keys[r.Str("to")], next = keys[r.Str("next")];
        var commitment = Keys.Commitment(next.Public());
        Assert.Equal(r.Str("nextCommitment"), Base64UrlText.Encode(commitment));
        var date = r["date"]!.GetValue<long>();
        Assert.Equal(r.Str("statement"), Encoding.UTF8.GetString(Rotations.RotationStatement(old.Public(), @new.Public(), commitment, date)));
        var at = DateTimeOffset.FromUnixTimeSeconds(date);
        var s = Rotations.SignRotation(old, @new, commitment, at);
        Assert.Equal(r.Str("oldSignature"), Base64UrlText.Encode(s.OldSignature));
        Assert.Equal(r.Str("newSignature"), Base64UrlText.Encode(s.NewSignature));
        var x = new Exchange(v["exchange"]!.AsObject());
        // The body, byte for byte, in the specification's member order.
        Assert.Equal(x.S("body"), Encoding.UTF8.GetString(s.Body()));
        var got = Rotations.ReadRotate(x.Body, old.Public(), at);
        Assert.True(Keys.Same(got.New, @new.Public()));
        Assert.Equal(commitment, got.Next);
        Assert.Equal(date, got.Date);
        Assert.Throws<ProtocolException>(() => Rotations.ReadRotate(x.Body, keys["b"].Public(), at));
        Assert.Throws<ProtocolException>(() => Rotations.ReadRotate(x.Body, old.Public(), at.AddMinutes(6)));
        Assert.Equal("{\"state\":\"rotated\"}", x.S("answerBody"));
        CheckExchange(keys, x);
    }

    [Fact]
    public void Revocation()
    {
        var keys = VectorKeys();
        var v = TestVectors.Load("revocation.json");
        var r = v["revocation"]!.AsObject();
        var k = keys[r.Str("key")];
        var date = r["date"]!.GetValue<long>();
        Assert.Equal(r.Str("statement"), Encoding.UTF8.GetString(Rotations.RevocationStatement(k.Public(), date)));
        var at = DateTimeOffset.FromUnixTimeSeconds(date);
        var s = Rotations.SignRevocation(k, at);
        Assert.Equal(r.Str("signature"), Base64UrlText.Encode(s.Signature));
        var x = new Exchange(v["exchange"]!.AsObject());
        Assert.Equal(x.S("body"), Encoding.UTF8.GetString(s.Body()));
        Assert.Equal(date, Rotations.ReadRevoke(x.Body, k.Public(), at).Date);
        var e = Assert.Throws<ProtocolException>(() => Rotations.ReadRevoke(x.Body, keys["b"].Public(), at));
        Assert.Equal(ProtocolError.Statement, e.Error);
        CheckExchange(keys, x);
    }

    [Fact]
    public void UncallableExchanges()
    {
        var keys = VectorKeys();
        var all = Exchanges();
        Exchange Find(string name) => all.Single(e => e.S("name") == name);
        byte[] a = keys["a"].Public(), a2 = keys["a2"].Public(), a3 = keys["a3"].Public();

        var first = Find("hello, from a server that can't be called");
        var h = Hello.Read(first.Body, answer: false);
        Assert.True(h.Uncallable);
        Assert.True(Keys.Same(h.KnownKey, a));
        Assert.Equal("https://msfp-4dq6fy7e4xtop2hj5lv6z3po54.invalid", h.Address);
        Assert.Empty(h.Certificates);
        Uncallables.CheckInbound(h.Uncallable, h.Address);
        var ah = Hello.Read(first.AnswerBody, answer: true);
        Assert.False(ah.Uncallable);
        Assert.Null(ah.Rotation);

        var pulled = Find("hello, answered with a rotation and a revocation");
        Assert.True(Keys.Same(Hello.Read(pulled.Body, false).KnownKey, a));
        ah = Hello.Read(pulled.AnswerBody, answer: true);
        Assert.NotNull(ah.Rotation);
        Assert.NotNull(ah.Revocation);
        var at = pulled.At;
        var p = ah.Pull(a, at);
        Assert.NotNull(p.Revocation);
        Assert.True(Keys.Same(p.Revocation.Key, a));
        Assert.NotNull(p.Rotation);
        Assert.True(Keys.Same(p.Rotation.Old, a));
        Assert.True(Keys.Same(p.Rotation.New, a2));
        Assert.Equal(Keys.Commitment(a3), p.Rotation.Next);
        // The statements as A makes them, dated as it answers: the same bytes.
        var rot = Rotations.SignRotation(keys["a"], keys["a2"], Keys.Commitment(a3), at);
        var rev = Rotations.SignRevocation(keys["a"], at);
        Assert.Equal(ah.Rotation.ToJsonString(), Encoding.UTF8.GetString(rot.Body()));
        Assert.Equal(ah.Revocation.ToJsonString(), Encoding.UTF8.GetString(rev.Body()));
        // Under another key, or too late: neither counts.
        Assert.Equal(new Pulled(null, null), ah.Pull(a2, at));
        Assert.Equal(new Pulled(null, null), ah.Pull(a, at + Signing.MaxSkew + TimeSpan.FromSeconds(1)));

        var last = Find("hello, with the new key known");
        Assert.True(Keys.Same(Hello.Read(last.Body, false).KnownKey, a2));
        ah = Hello.Read(last.AnswerBody, answer: true);
        Assert.Null(ah.Rotation);
        Assert.Null(ah.Revocation);
    }
}

/// <summary>Tests that flip the process-wide plain http switch take this lock.</summary>
internal static class PlainHttpLock
{
    public static readonly object Gate = new();
}
