using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.MoldCode;
using AIVision.Domain.MoldCode;
using AIVision.Domain.Shared;
using MediatR;
using Microsoft.Extensions.Options;

namespace AIVision.Application.MoldCode;

/// <summary>
/// 模號核對週期 handler。本地熱迴圈:PLC 觸發 → 多幀投票辨識 → 三態決策 → 混料氣吹。
/// 辨識器(ONNX)/相機(IDS)/IO(PLC)全走 port 注入,本 handler 無 DL/影像相依。
/// </summary>
public sealed class VerifyMoldCodeCycleCommandHandler
    : IRequestHandler<VerifyMoldCodeCycleCommand, MoldCodeCycleResult>
{
    private readonly IPlcPort _plc;
    private readonly ICameraPort _camera;
    private readonly IMoldCodeRecognizerPort _recognizer;
    private readonly MoldCodeCycleOptions _options;

    public VerifyMoldCodeCycleCommandHandler(
        IPlcPort plc,
        ICameraPort camera,
        IMoldCodeRecognizerPort recognizer,
        IOptions<MoldCodeCycleOptions> options)
    {
        _plc = plc;
        _camera = camera;
        _recognizer = recognizer;
        _options = options.Value;
    }

    public async Task<MoldCodeCycleResult> Handle(
        VerifyMoldCodeCycleCommand request,
        CancellationToken cancellationToken)
    {
        await _plc.WriteAsync(IoCommand.CaptureStartCommand(), cancellationToken);

        var sw = Stopwatch.StartNew();
        var frames = new List<MarkingObservation>();
        VoteResult vote = MultiFrameVoter.Vote(frames);

        // 自適應多幀投票:達共識或時間/幀數預算用盡即停。
        for (int i = 0; i < _options.MaxFrames; i++)
        {
            var image = await _camera.CaptureOnceAsync(cancellationToken);
            frames.Add(_recognizer.Recognize(image, _options.ClassSet));
            vote = MultiFrameVoter.Vote(frames);

            if (MultiFrameVoter.HasConsensus(vote, _options.MinConsensusVotes, _options.MinConsensusMargin))
                break;
            if (sw.ElapsedMilliseconds >= _options.TimeBudgetMs)
                break;
        }

        var decision = MarkingVerifier.Decide(
            request.ExpectedCode, vote.Observation, _options.MixedAlarmConfThreshold);

        bool blow = decision.Outcome == MarkingVerifyOutcome.MixedAlarm;
        await _plc.WriteAsync(blow ? IoCommand.Blow() : IoCommand.Result(true), cancellationToken);
        sw.Stop();

        return new MoldCodeCycleResult(
            decision.Outcome,
            vote.Observation.Code,
            decision.ClassifiedAs,
            vote.Observation.ConfClassify,
            vote.Frames,
            vote.WinnerVotes,
            blow,
            sw.ElapsedMilliseconds,
            decision.Reason);
    }
}
