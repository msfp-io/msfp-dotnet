using Msfp.Engine;

namespace Msfp.Tests;

// msfp-go's refusals_internal_test.go: a peer's signed refusal read back
// as the engine's error, for each problem the engine sends; a type it
// doesn't know by its status alone; any other status unreachable.
public class RefusalTests
{
    private static readonly Dictionary<string, MsfpError> Want = new()
    {
        [ProblemType.NotFound] = MsfpError.NotFound,
        [ProblemType.NotSupported] = MsfpError.NotSupported,
        [ProblemType.Invalid] = MsfpError.InvalidRequest,
        [ProblemType.Rotation] = MsfpError.RotationRefused,
        [ProblemType.StreamLimit] = MsfpError.StreamLimitReached,
        [ProblemType.NotPlayable] = MsfpError.NotPlayable,
        [ProblemType.Delivery] = MsfpError.DeliveryRefused,
        [ProblemType.Stopped] = MsfpError.PlaybackStopped,
        [ProblemType.RateLimited] = MsfpError.RateLimited,
        [ProblemType.Internal] = MsfpError.PeerFailed,
        [ProblemType.ServerBusy] = MsfpError.ServerBusy,
    };

    [Fact]
    public void EachProblemReadsBackAsItsError()
    {
        Assert.Equal(Want.Count, Problems.ByType.Count);
        foreach (var (type, (status, _)) in Problems.ByType)
        {
            Assert.Equal(Want[type], Problems.RefusalError(status, type).Code);
        }
    }

    [Theory]
    [InlineData(404, MsfpError.NotFound)]
    [InlineData(400, MsfpError.InvalidRequest)]
    [InlineData(409, MsfpError.StreamLimitReached)]
    [InlineData(410, MsfpError.PlaybackStopped)]
    [InlineData(429, MsfpError.RateLimited)]
    [InlineData(503, MsfpError.ServerBusy)]
    [InlineData(500, MsfpError.PeerFailed)]
    [InlineData(200, MsfpError.Unreachable)]
    [InlineData(401, MsfpError.Unreachable)]
    [InlineData(403, MsfpError.Unreachable)]
    [InlineData(418, MsfpError.Unreachable)]
    [InlineData(502, MsfpError.Unreachable)]
    public void AnUnknownTypeByItsStatus(int status, MsfpError want) =>
        Assert.Equal(want, Problems.RefusalError(status, "something-new").Code);
}
