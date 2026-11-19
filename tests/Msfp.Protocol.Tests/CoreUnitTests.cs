using System.Text;

namespace Msfp.Protocol.Tests;

/// <summary>msfp-go's wire unit tests of signing, hellos and statements.</summary>
public sealed class CoreUnitTests
{
    private static Hello Req(string body) => Hello.Read(Encoding.UTF8.GetBytes(body), answer: false);

    private static Hello Ans(string body) => Hello.Read(Encoding.UTF8.GetBytes(body), answer: true);

    [Fact]
    public void SignatureHeadersOnce()
    {
        var k = MsfpKey.Generate();
        var now = DateTimeOffset.UtcNow;
        bool Ok(Action<HeaderList> edit)
        {
            var h = new HeaderList();
            Signing.SignRequest("POST", "a.example.org", "/api/v1/federation/hello", "{}"u8, k, now).Apply(h);
            edit(h);
            try
            {
                new Verifier { Host = "a.example.org" }.Check("POST", "/api/v1/federation/hello", h, "{}"u8, now);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        Assert.True(Ok(_ => { }));
        Assert.False(Ok(h => h.Add(Signing.HeaderDate, h.Get(Signing.HeaderDate)!)));
        Assert.False(Ok(h => h.Set(Signing.HeaderNonce, h.Get(Signing.HeaderNonce) + ",x")));
        Assert.False(Ok(h => h.Add("Content-Encoding", "gzip")));
        Assert.True(Ok(h => h.Add("Content-Encoding", "identity")));
        Assert.False(Ok(h => h.Set(Signing.HeaderDate, "0" + h.Get(Signing.HeaderDate))));
    }

    [Fact]
    public void DatesFarOff()
    {
        var k = MsfpKey.Generate();
        var now = DateTimeOffset.UtcNow;
        foreach (var date in new[] { "9007199254740991", "1", "253402300800" })
        {
            var h = new HeaderList();
            Signing.SignRequest("POST", "a.example.org", "/x", "{}"u8, k, now).Apply(h);
            h.Set(Signing.HeaderDate, date);
            Assert.Throws<ProtocolException>(() => new Verifier { Host = "a.example.org" }.Check("POST", "/x", h, "{}"u8, now));
        }
    }

    [Fact]
    public void NoncesBeforeTheStart()
    {
        var k = MsfpKey.Generate();
        var start = DateTimeOffset.FromUnixTimeSeconds(1800000000).AddMilliseconds(500);
        var h = new HeaderList();
        Signing.SignRequest("POST", "a.example.org", "/x", "{}"u8, k, start).Apply(h);
        // Signed in the second the memory began, after its start: refused.
        Assert.Throws<ProtocolException>(() =>
            new Verifier { Host = "a.example.org", Seen = new NonceMemory(start) }.Check("POST", "/x", h, "{}"u8, start));
        new Verifier { Host = "a.example.org", Seen = new NonceMemory(start.AddSeconds(-1)) }.Check("POST", "/x", h, "{}"u8, start);
    }

    [Fact]
    public void AdmitAndForgotten()
    {
        var k = MsfpKey.Generate();
        var now = DateTimeOffset.UtcNow;
        var h = new HeaderList();
        Signing.SignRequest("POST", "a.example.org", "/x", "{}"u8, k, now).Apply(h);
        Assert.Throws<ProtocolException>(() => new Verifier { Host = "a.example.org", Admit = _ => new(false, false) }
            .Check("POST", "/x", h, "{}"u8, now));
        var seen = new NonceMemory();
        var v = new Verifier { Host = "a.example.org", Admit = _ => new(false, false), Forgotten = _ => true, Seen = seen };
        var e = Assert.Throws<ForgottenException>(() => v.Check("POST", "/x", h, "{}"u8, now));
        Assert.True(Keys.Same(e.Verified.Key, k.Public()));
        // A replay gets the plain refusal.
        Assert.Throws<ProtocolException>(() => v.Check("POST", "/x", h, "{}"u8, now));
        var stranger = new Verifier { Host = "a.example.org", Admit = _ => new(true, false) }.Check("POST", "/x", h, "{}"u8, now);
        Assert.False(stranger.Known);
    }

    [Fact]
    public async Task AdmitAndForgottenAsync()
    {
        var k = MsfpKey.Generate();
        var now = DateTimeOffset.UtcNow;
        var h = new HeaderList();
        Signing.SignRequest("POST", "a.example.org", "/x", "{}"u8, k, now).Apply(h);
        var v = new Verifier
        {
            Host = "a.example.org",
            AdmitAsync = async (_, _) => { await Task.Yield(); return new Admission(false, false); },
            ForgottenAsync = async (_, _) => { await Task.Yield(); return true; },
        };
        var e = await Assert.ThrowsAsync<ForgottenException>(async () => await v.CheckAsync("POST", "/x", h, "{}"u8.ToArray(), now));
        Assert.True(Keys.Same(e.Verified.Key, k.Public()));
        await Assert.ThrowsAsync<ProtocolException>(async () => await v.CheckAsync("POST", "/x", h, "{}"u8.ToArray(), now));
        var heard = new Verifier { Host = "a.example.org", AdmitAsync = (_, _) => ValueTask.FromResult(new Admission(true, true)) };
        Assert.True((await heard.CheckAsync("POST", "/x", h, "{}"u8.ToArray(), now)).Known);
    }

    [Fact]
    public void AnswerMediaTypes()
    {
        Assert.Equal("-", Signing.AnswerMediaType(new HeaderList()));
        Assert.Equal("-", Signing.AnswerMediaType(new HeaderList().Add("Content-Type", " ")));
        Assert.Equal("application/json", Signing.AnswerMediaType(new HeaderList().Add("Content-Type", "Application/JSON ; charset=utf-8")));
        Assert.Throws<ProtocolException>(() => Signing.AnswerMediaType(new HeaderList().Add("Content-Type", "a/b").Add("content-type", "a/b")));
        Assert.Throws<ProtocolException>(() => Signing.AnswerMediaType(new HeaderList().Add("Content-Type", "json")));
        Assert.Throws<ProtocolException>(() => Signing.AnswerMediaType(new HeaderList().Add("Content-Type", "a /b")));
    }

    [Fact]
    public void HelloOneWay()
    {
        var key = MsfpKey.Generate().Public();
        var known = "\"" + Base64UrlText.Encode(key) + "\"";
        const string Invalid = "\"address\":\"https://msfp-4dq6fy7e4xtop2hj5lv6z3po54.invalid\"";
        var h = Req("{\"state\":\"paired\"," + Invalid + ",\"inbound\":false,\"knownKey\":" + known + ",\"rotation\":\"pending\"}");
        Assert.True(h.Uncallable);
        Assert.True(Keys.Same(h.KnownKey, key));
        Assert.Equal(Rotations.StatePending, h.RotationState);
        Assert.Equal(Hello.RotationRefused, Req("{\"state\":\"paired\"," + Invalid + ",\"inbound\":false,\"rotation\":\"refused\"}").RotationState);
        h = Req("{\"state\":\"paired\"," + Invalid + ",\"inbound\":false,\"knownKey\":\"x\",\"rotation\":\"rotated\"}");
        Assert.Null(h.KnownKey);
        Assert.Null(h.RotationState);
        foreach (var bad in new[] { "\"knownKey\":5", "\"rotation\":{\"newKey\":\"x\"}", "\"knownKey\":null", "\"inbound\":\"no\"" })
        {
            var e = Assert.Throws<ProtocolException>(() => Req("{\"state\":\"paired\"," + Invalid + ",\"inbound\":false," + bad + "}"));
            Assert.Equal(ProtocolError.Json, e.Error);
        }

        foreach (var inbound in new[] { "", "\"inbound\":true," })
        {
            h = Req("{\"state\":\"paired\"," + inbound + "\"knownKey\":5,\"rotation\":null}");
            Assert.False(h.Uncallable);
            Assert.Null(h.KnownKey);
            Assert.Null(h.RotationState);
        }

        h = Req("{\"state\":\"paired\"," + Invalid + ",\"inbound\":false,\"revocation\":null,\"callsFailed\":\"x\"}");
        Assert.False(h.CallsFailed);
        Assert.Null(h.Revocation);

        h = Ans("{\"state\":\"paired\",\"rotation\":{\"a\":1},\"revocation\":{\"b\":2},\"callsFailed\":true,\"knownKey\":5}");
        Assert.Equal("{\"a\":1}", h.Rotation!.ToJsonString());
        Assert.Equal("{\"b\":2}", h.Revocation!.ToJsonString());
        Assert.True(h.CallsFailed);
        Assert.Null(h.KnownKey);
        foreach (var other in new[] { "\"rotation\":\"pending\",\"revocation\":[1],\"callsFailed\":1", "\"rotation\":5,\"revocation\":\"x\",\"callsFailed\":\"true\"", "\"callsFailed\":false" })
        {
            h = Ans("{\"state\":\"paired\"," + other + "}");
            Assert.Null(h.Rotation);
            Assert.Null(h.Revocation);
            Assert.False(h.CallsFailed);
        }

        // A request needs a state; an answer doesn't. Certificates that
        // aren't a list: a bad request, none in an answer.
        Assert.Throws<ProtocolException>(() => Req("{\"core\":[1]}"));
        Assert.Equal("", Ans("{\"core\":[1]}").State);
        Assert.Throws<ProtocolException>(() => Req("{\"state\":\"paired\",\"certificates\":\"x\"}"));
        Assert.Empty(Ans("{\"state\":\"paired\",\"certificates\":\"x\"}").Certificates);
        // A known part null: a bad body, both ways; a null entry ignored.
        Assert.Throws<ProtocolException>(() => Ans("{\"state\":\"paired\",\"parts\":{\"video\":null}}"));
        Assert.Equal([1], Ans("{\"state\":\"paired\",\"core\":[null,1]}").Versions.Core);
        Assert.Throws<ProtocolException>(() => Req("{\"state\":\"paired\",\"nextCommitment\":\"abc\"}"));
    }

    [Fact]
    public void ReadRotateRefuses()
    {
        var old = MsfpKey.Generate();
        var @new = MsfpKey.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var good = Rotations.SignRotation(old, @new, null, now);
        Assert.Null(Rotations.ReadRotate(good.Body(), old.Public(), now).Next);
        var other = Rotations.SignRotation(old, old, null, now);
        Assert.Throws<ProtocolException>(() => Rotations.ReadRotate(other.Body(), old.Public(), now));
        foreach (var body in new[]
                 {
                     "{}", "{\"newKey\":null}", "[]",
                     Encoding.UTF8.GetString(good.Body()).Replace("\"date\":1800000000", "\"date\":1800000000.0", StringComparison.Ordinal),
                 })
        {
            var e = Assert.Throws<ProtocolException>(() => Rotations.ReadRotate(Encoding.UTF8.GetBytes(body), old.Public(), now));
            Assert.Equal(ProtocolError.Statement, e.Error);
        }
    }

    [Fact]
    public void NextKeyText()
    {
        var k = MsfpKey.Generate();
        var text = Rotations.ExportNextKey(k);
        Assert.StartsWith("msfp-next-key-1.", text, StringComparison.Ordinal);
        Assert.True(Keys.Same(k.Public(), Rotations.ParseNextKey(" " + text + "\n").Public()));
        Assert.Throws<ProtocolException>(() => Rotations.ParseNextKey("msfp-next-key-0." + text[16..]));
    }

    [Fact]
    public void HelloSchedule()
    {
        Assert.Equal(Uncallables.HelloGone, Uncallables.HelloWait(true, true, TimeSpan.Zero));
        Assert.Equal(Uncallables.HelloWaiting, Uncallables.HelloWait(true, false, TimeSpan.FromHours(1)));
        Assert.Equal(Uncallables.HelloPaired, Uncallables.HelloWait(true, false, TimeSpan.FromHours(25)));
        Assert.Equal(Uncallables.HelloPaired, Uncallables.HelloWait(false, false, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(4), Uncallables.Vary(TimeSpan.FromMinutes(5), 0));
        Assert.Equal(TimeSpan.FromMinutes(5), Uncallables.Vary(TimeSpan.FromMinutes(5), 0.5));
    }

    [Fact]
    public void Ipv6Addresses()
    {
        Assert.True(Address.Valid("https://[2001:db8::1]:8443"));
        Assert.False(Address.Valid("https://[2001:DB8::1]"));
        Assert.False(Address.Valid("https://[2001:db8:0:0:0:0:0:1]"));
        Assert.True(Address.Valid("https://[::ffff:192.0.2.1]"));
        Assert.False(Address.Valid("https://[fe80::1%eth0]"));
        Assert.True(Address.Valid("https://[2001:db8:0:1::1]"));
        Assert.False(Address.Valid("https://[2001:db8::1:0:0:1]") && Address.Valid("https://[2001:db8:0:0:1::1]"));
    }
}
