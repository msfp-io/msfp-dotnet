using System.Text;

namespace Msfp.Tests;

public sealed class SealerTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("msfp-sealer-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] Key() => [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    public void Dispose() => Directory.Delete(dir, recursive: true);

    // The plain sealer keeps its context in front, and opens only under it.
    [Fact]
    public async Task Plain()
    {
        var s = PlainSealer.Instance;
        var box = await s.SealAsync(B("seed"), "msfp current key", Ct);
        Assert.Equal(B("msfp current key\nseed"), box);
        Assert.Equal(B("seed"), await s.OpenAsync(box, "msfp current key", Ct));
        var e = await Assert.ThrowsAsync<MsfpException>(async () => await s.OpenAsync(box, "msfp next key", Ct));
        Assert.Equal(MsfpError.Sealed, e.Code);
    }

    // A key box opens under its own key and context only, and not once
    // changed; two seals of the same differ (a random nonce).
    [Fact]
    public async Task KeyRoundTrip()
    {
        var s = new KeySealer(Key());
        var box = await s.SealAsync(B("seed"), "msfp current key", Ct);
        Assert.Equal(12 + 4 + 16, box.Length);
        Assert.NotEqual(box, await s.SealAsync(B("seed"), "msfp current key", Ct));
        Assert.Equal(B("seed"), await s.OpenAsync(box, "msfp current key", Ct));
        Assert.Empty(await s.OpenAsync(await s.SealAsync([], "msfp ids secret", Ct), "msfp ids secret", Ct));

        async Task Refused(KeySealer sealer, byte[] b, string context)
        {
            var e = await Assert.ThrowsAsync<MsfpException>(async () => await sealer.OpenAsync(b, context, Ct));
            Assert.Equal(MsfpError.Sealed, e.Code);
        }
        await Refused(s, box, "msfp next key");
        var other = Key();
        other[0] = 99;
        await Refused(new KeySealer(other), box, "msfp current key");
        var changed = (byte[])box.Clone();
        changed[13] ^= 1;
        await Refused(s, changed, "msfp current key");
        await Refused(s, box[..27], "msfp current key");
        await Refused(s, box[..5], "msfp current key");
        Assert.Throws<ArgumentException>(() => new KeySealer(new byte[31]));
    }

    // A box the Go engine's KeySealer sealed opens here.
    [Fact]
    public async Task OpensGoBox()
    {
        var s = new KeySealer(Key());
        Assert.Equal(B("hello msfp"), await s.OpenAsync(Convert.FromHexString(GoSamples.KeyBox), "msfp current key", Ct));
        await Assert.ThrowsAsync<MsfpException>(async () => await s.OpenAsync(Convert.FromHexString(GoSamples.KeyBox), "msfp old key", Ct));
    }

    // The key file: made once, 32 bytes, mode 0600; read back the same; a
    // file of another length isn't a key.
    [Fact]
    public async Task KeyFile()
    {
        var path = Path.Combine(dir, "sealing.key");
        var box = await KeyFileSealer.Open(path).SealAsync(B("seed"), "msfp code secret", Ct);
        Assert.Equal(32, new FileInfo(path).Length);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, GoSamples.Mode(path));
        Assert.Equal(B("seed"), await KeyFileSealer.Open(path).OpenAsync(box, "msfp code secret", Ct));

        await File.WriteAllBytesAsync(path, new byte[33], Ct);
        Assert.Throws<InvalidDataException>(() => KeyFileSealer.Open(path));
    }
}
