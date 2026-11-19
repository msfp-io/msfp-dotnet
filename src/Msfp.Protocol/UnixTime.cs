namespace Msfp.Protocol;

/// <summary>Unix seconds against <see cref="DateTimeOffset"/>, for any integer MSFP allows (up to 2^53 − 1, past year 9999).</summary>
internal static class UnixTime
{
    /// <summary>now − when, in seconds (fractions dropped toward the past), without overflow.</summary>
    public static Int128 SecondsSince(DateTimeOffset now, long when) => (Int128)now.ToUnixTimeSeconds() - when;

    /// <summary>Whether a date in Unix seconds is within skew of now.</summary>
    public static bool Within(DateTimeOffset now, long when, TimeSpan skew)
    {
        // Compare in ticks: now's ticks against when's, both from the epoch.
        var nowTicks = (Int128)(now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks);
        var d = nowTicks - ((Int128)when * TimeSpan.TicksPerSecond);
        return d <= skew.Ticks && d >= -skew.Ticks;
    }

    /// <summary>Whether the second when begins before t.</summary>
    public static bool Before(long when, DateTimeOffset t) =>
        (Int128)when * TimeSpan.TicksPerSecond < (Int128)(t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks);
}
