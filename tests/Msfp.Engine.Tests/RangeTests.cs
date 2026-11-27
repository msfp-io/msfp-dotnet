namespace Msfp.Tests;

// Byte ranges, as a Range header gives them (MSFP Media fetching 2.1).
public sealed class RangeTests
{
    [Theory]
    [InlineData("bytes=0-99", 0L, 99L, 0L)]
    [InlineData("bytes=5-", 5L, -1L, 0L)]
    [InlineData("bytes=-10", 0L, 0L, 10L)]
    [InlineData("bytes=000-009", 0L, 9L, 0L)]
    public void ParseRange(string header, long first, long last, long suffix)
    {
        Assert.True(ByteRange.TryParse(header, out var r));
        Assert.Equal(new ByteRange(first, last, suffix), r);
    }

    [Theory]
    [InlineData("bytes=0-1,5-9")]
    [InlineData("bytes=-")]
    [InlineData("bytes=5-1")]
    [InlineData("items=0-9")]
    [InlineData("bytes = 0-9")]
    [InlineData("bytes=0-1234567890123456")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=a-b")]
    [InlineData("bytes=1-2 ")]
    public void ParseRangeRefused(string header) => Assert.False(ByteRange.TryParse(header, out _));
}
