using System.Security.Cryptography;
using System.Text;

namespace Msfp;

/// <summary>The contexts the engine seals under (host.md section 5).</summary>
internal static class SealContext
{
    internal const string Current = "msfp current key";
    internal const string Next = "msfp next key";
    internal const string Old = "msfp old key";
    internal const string Revoked = "msfp revoked key";
    internal const string Ids = "msfp ids secret";
    internal const string Code = "msfp code secret";
}

/// <summary>
/// Seals nothing: what it "seals" is kept as it is, its context in front.
/// For tests, and for the memory store, whose records are never written
/// anywhere. Never for a store on disk.
/// </summary>
public sealed class PlainSealer : ISealer
{
    /// <summary>The one instance.</summary>
    public static PlainSealer Instance { get; } = new();

    /// <summary>The context, a line feed, and plain.</summary>
    /// <param name="plain">What to "seal".</param>
    /// <param name="context">The context.</param>
    /// <param name="ct">Unused.</param>
    public ValueTask<byte[]> SealAsync(byte[] plain, string context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plain);
        return ValueTask.FromResult<byte[]>([.. Prefix(context), .. plain]);
    }

    /// <summary>Gives back what Seal was given, if it was sealed under context.</summary>
    /// <param name="sealed">The box.</param>
    /// <param name="context">The context.</param>
    /// <param name="ct">Unused.</param>
    public ValueTask<byte[]> OpenAsync(byte[] @sealed, string context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@sealed);
        var prefix = Prefix(context);
        if (!@sealed.AsSpan().StartsWith(prefix))
        {
            throw new MsfpException(MsfpError.Sealed);
        }
        return ValueTask.FromResult(@sealed[prefix.Length..]);
    }

    private static byte[] Prefix(string context) => Encoding.UTF8.GetBytes(context + "\n");
}

/// <summary>
/// Seals with AES-256-GCM under a 32-byte key, the context (UTF-8) as the
/// additional data, so that a box opens only under its own context. A box is
/// the 12-byte nonce, then the sealed bytes, then the 16-byte tag: the Go
/// engine's KeySealer's form, so that a box of one opens in the other.
/// </summary>
public sealed class KeySealer : ISealer
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] key;

    /// <summary>A sealer on this key.</summary>
    /// <param name="key">32 bytes.</param>
    /// <exception cref="ArgumentException">The key isn't 32 bytes.</exception>
    public KeySealer(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("msfp: a sealing key is 32 bytes", nameof(key));
        }
        this.key = (byte[])key.Clone();
    }

    /// <summary>Seals plain under context, with a random nonce.</summary>
    /// <param name="plain">What to seal.</param>
    /// <param name="context">The context.</param>
    /// <param name="ct">Unused.</param>
    public ValueTask<byte[]> SealAsync(byte[] plain, string context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plain);
        var box = new byte[NonceSize + plain.Length + TagSize];
        var nonce = box.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        // An AesGcm isn't safe for calls at once: one per call.
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, box.AsSpan(NonceSize, plain.Length), box.AsSpan(NonceSize + plain.Length), Encoding.UTF8.GetBytes(context));
        return ValueTask.FromResult(box);
    }

    /// <summary>Opens a box sealed under context; Sealed when it doesn't open.</summary>
    /// <param name="sealed">The box.</param>
    /// <param name="context">The context.</param>
    /// <param name="ct">Unused.</param>
    public ValueTask<byte[]> OpenAsync(byte[] @sealed, string context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@sealed);
        if (@sealed.Length < NonceSize + TagSize)
        {
            throw new MsfpException(MsfpError.Sealed);
        }
        var length = @sealed.Length - NonceSize - TagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        try
        {
            aes.Decrypt(@sealed.AsSpan(0, NonceSize), @sealed.AsSpan(NonceSize, length), @sealed.AsSpan(NonceSize + length), plain, Encoding.UTF8.GetBytes(context));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new MsfpException(MsfpError.Sealed);
        }
        return ValueTask.FromResult(plain);
    }
}

/// <summary>
/// A <see cref="KeySealer"/> whose key is in its own file: 32 random bytes,
/// made on first use with mode 0600. Keep that file apart from the store's
/// data where you can (another disk, another backup): whoever holds both
/// holds the keys.
/// </summary>
public static class KeyFileSealer
{
    /// <summary>The sealer of the key in path, made when the file doesn't exist.</summary>
    /// <param name="path">The key file.</param>
    /// <exception cref="InvalidDataException">The file isn't 32 bytes.</exception>
    public static KeySealer Open(string path)
    {
        byte[] key;
        try
        {
            key = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            key = new byte[32];
            RandomNumberGenerator.Fill(key);
            // CreateNew: two processes starting at once never write two keys.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using var f = new FileStream(path, options);
            f.Write(key);
            f.Flush(flushToDisk: true);
        }
        if (key.Length != 32)
        {
            throw new InvalidDataException($"msfp: {path} is not a sealing key (32 bytes)");
        }
        return new KeySealer(key);
    }
}
