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
/// 雙軸模號/穴號核對週期 handler。本地熱迴圈：PLC 觸發 → 多幀配對投票辨識 → 分軸三態決策 →
/// 混料(MixedAlarm)/不良品(NG) 氣吹剔除。辨識器(ONNX 雙 head)/相機(IDS)/IO(PLC) 全走 port 注入。
/// </summary>
public sealed class VerifyMoldCodePairCycleCommandHandler
    : IRequestHandler<VerifyMoldCodePairCycleCommand, MoldCodePairCycleResult>
{
    private readonly IPlcPort _plc;
    private readonly ICameraPort _camera;
    private readonly IMoldCodePairRecognizerPort _recognizer;
    private readonly MoldCodePairCycleOptions _options;

    public VerifyMoldCodePairCycleCommandHandler(
        IPlcPort plc,
        ICameraPort camera,
        IMoldCodePairRecognizerPort recognizer,
        IOptions<MoldCodePairCycleOptions> options)
    {
        _plc = plc;
        _camera = camera;
        _recognizer = recognizer;
        _options = options.Value;
    }

    public async Task<MoldCodePairCycleResult> Handle(
        VerifyMoldCodePairCycleCommand request,
        CancellationToken cancellationToken)
    {
        await _plc.WriteAsync(IoCommand.CaptureStartCommand(), cancellationToken);

        var sw = Stopwatch.StartNew();
        var frames = new List<PairObservation>();
        PairVoteResult vote = MoldCodePairVoter.Vote(frames);

        // 自適應多幀投票：達共識或時間/幀數預算用盡即停。
        for (int i = 0; i < _options.MaxFrames; i++)
        {
            var image = await _camera.CaptureOnceAsync(cancellationToken);
            frames.Add(_recognizer.Recognize(image));
            vote = MoldCodePairVoter.Vote(frames);

            if (MoldCodePairVoter.HasConsensus(vote, _options.MinConsensusVotes, _options.MinConsensusMargin))
                break;
            if (sw.ElapsedMilliseconds >= _options.TimeBudgetMs)
                break;
        }

        var decision = MoldCodePairVerifier.Decide(
            request.ExpectedMohao, request.ExpectedXuehao, vote.Observation,
            _options.MoldThreshold, _options.CavityThreshold, _options.NgClassName);

        // fail-closed IO 映射：
        //   Match / TrustInput → 放行(Result OK)
        //   MixedAlarm / Reject → 氣吹剔除(Blow)
        //   Skip(無物件 / 辨識失敗 / 信心非有限)→ 不可當良品放行 → 回 NG(Result false)，
        //     避免「啟用卻失敗」被當成功(fail-mode-output Q1)。
        bool blow = decision.ShouldReject;
        bool accept = decision.Outcome is PairVerifyOutcome.Match or PairVerifyOutcome.TrustInput;
        IoCommand io = blow ? IoCommand.Blow()
            : accept ? IoCommand.Result(true)
            : IoCommand.Result(false);
        await _plc.WriteAsync(io, cancellationToken);
        sw.Stop();

        return new MoldCodePairCycleResult(
            decision.Outcome,
            vote.Observation.Mohao,
            vote.Observation.Xuehao,
            vote.Observation.ConfMohao,
            vote.Observation.ConfXuehao,
            decision.ClassifiedAs,
            vote.Frames,
            vote.WinnerVotes,
            blow,
            sw.ElapsedMilliseconds,
            decision.Reason);
    }
}
