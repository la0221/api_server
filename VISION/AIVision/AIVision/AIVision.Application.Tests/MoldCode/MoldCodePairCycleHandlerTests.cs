using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.MoldCode;
using AIVision.Application.Ports.MoldCode;
using AIVision.Domain.MoldCode;
using AIVision.Domain.Shared;
using AIVision.Infrastructure.Devices;
using Microsoft.Extensions.Options;
using Xunit;

namespace AIVision.Application.Tests.MoldCode;

/// <summary>
/// 雙軸核對週期 handler 的 IO 映射測試（fail-closed）：
/// 相符→放行、混料→氣吹、辨識失敗(Skip)→不可放行(NG)。
/// </summary>
public class MoldCodePairCycleHandlerTests
{
    private sealed class StubPairRecognizer : IMoldCodePairRecognizerPort
    {
        private readonly PairObservation _obs;
        public StubPairRecognizer(PairObservation obs) => _obs = obs;
        public PairObservation Recognize(ImageData image) => _obs;
    }

    private static IOptions<MoldCodePairCycleOptions> Opts() =>
        Options.Create(new MoldCodePairCycleOptions { MaxFrames = 1, TimeBudgetMs = 50 });

    private static async Task<(MoldCodePairCycleResult Result, FakePlcPort Plc)> Run(PairObservation obs)
    {
        var plc = new FakePlcPort();
        var handler = new VerifyMoldCodePairCycleCommandHandler(
            plc, new FakeCameraPort(), new StubPairRecognizer(obs), Opts());
        var r = await handler.Handle(new VerifyMoldCodePairCycleCommand("M101", "08"), CancellationToken.None);
        return (r, plc);
    }

    [Fact]
    public async Task Match_SignalsOk_NoBlow()
    {
        var (r, plc) = await Run(PairObservation.Read("M101", 0.99, "08", 0.99));
        Assert.Equal(PairVerifyOutcome.Match, r.Outcome);
        Assert.False(r.AirBlown);
        Assert.True(plc.LastCommand.ResultOn);
        Assert.False(plc.LastCommand.AirBlow);
    }

    [Fact]
    public async Task ConfidentMismatch_Blows()
    {
        var (r, plc) = await Run(PairObservation.Read("M60", 0.99, "08", 0.99));
        Assert.Equal(PairVerifyOutcome.MixedAlarm, r.Outcome);
        Assert.True(r.AirBlown);
        Assert.True(plc.LastCommand.AirBlow);
    }

    [Fact]
    public async Task RecognizerFailed_DoesNotSignalOk_FailClosed()
    {
        // 辨識失敗(Skip) 絕不可送出 ResultOn=true（否則失敗被當良品放行）。
        var (r, plc) = await Run(PairObservation.Failed("onnx error"));
        Assert.Equal(PairVerifyOutcome.Skip, r.Outcome);
        Assert.False(r.AirBlown);
        Assert.False(plc.LastCommand.ResultOn);   // 關鍵 fail-closed 斷言
    }

    [Fact]
    public async Task NoObject_DoesNotSignalOk_FailClosed()
    {
        var (r, plc) = await Run(PairObservation.NoObject());
        Assert.Equal(PairVerifyOutcome.Skip, r.Outcome);
        Assert.False(plc.LastCommand.ResultOn);
    }
}
