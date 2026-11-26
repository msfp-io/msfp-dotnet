namespace Msfp.Describe.Tests;

// msfp-go's describe/sps_internal_test.go, the same SPS units (as
// FFmpeg's trace_headers reads them too): High 10 with scaling matrices
// (a 4x4 list read whole, an 8x8 one ending early; 4:4:4's 12 lists),
// picture order count type 1 with its cycle, and 4:2:2.
public class SpsTests
{
    [Theory]
    [InlineData("676e0029a6da49249249249211108a872113145a", 1)]
    [InlineData("676e0029b6d00d9680", 2)]
    [InlineData("676e002990db0882110846872113145a", 3)]
    public void ScalingMatricesAndPicOrderType1(string sps, int chroma)
    {
        var annexB = Convert.FromHexString("00000001" + sps);
        Assert.True(Records.TryParseAvc(annexB, out var r));
        Assert.Equal(new AvcRecord(110, 0, 41, 4, 10, chroma), r);
    }
}
