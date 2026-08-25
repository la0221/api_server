using System;
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
    /// <summary>額外的吹氣觸發通道（TCP → 現場 IO 監聽程式）。null＝未配置，只走 PLC。</summary>
    private readonly IBlowDispatcherPort? _blow;
    /// <summary>混料圖歸檔（自帶正解，供自我強化訓練）。null＝未配置。</summary>
    private readonly IMismatchArchivePort? _mismatchArchive;

    /// <summary>單片流水號，給吹氣去重用（同一片只吹一次）。</summary>
    private static int _itemSeq;

    public VerifyMoldCodePairCycleCommandHandler(
        IPlcPort plc,
        ICameraPort camera,
        IMoldCodePairRecognizerPort recognizer,
        IOptions<MoldCodePairCycleOptions> options,
        IBlowDispatcherPort? blow = null,
        IMismatchArchivePort? mismatchArchive = null)
    {
        _plc = plc;
        _camera = camera;
        _recognizer = recognizer;
        _options = options.Value;
        _blow = blow;
        _mismatchArchive = mismatchArchive;
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
        // 留最後一張供混料歸檔用（存的是實際判定當下看到的那張）。
        AIVision.Domain.Shared.ImageData? lastImage = null;
        for (int i = 0; i < _options.MaxFrames; i++)
        {
            var image = await _camera.CaptureOnceAsync(cancellationToken);
            lastImage = image;
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

        // ── 額外的吹氣觸發（TCP → 現場 IO 監聽程式）──────────────────────
        // 現場的 IO 卡不在這台電腦上，PLC 那條吹不到它，所以多送一份訊號出去。
        // ⚠ 這裡**只排隊、不等待**：延遲（等工件走到吹嘴）與實際送出都在背景，
        //   熱迴圈不能為了吹氣多花任何時間；送不出去也只記 log，不影響本次判定結果。
        if (blow && _blow is { Enabled: true })
        {
            var reason = decision.Outcome == PairVerifyOutcome.MixedAlarm
                ? BlowRequest.ReasonMismatch
                : BlowRequest.ReasonNg;
            _blow.Enqueue(new BlowRequest(
                Id: $"T{System.Threading.Interlocked.Increment(ref _itemSeq):000000}",
                CreatedAt: DateTime.Now,
                Reason: reason,
                ExpectedMohao: request.ExpectedMohao,
                ExpectedXuehao: request.ExpectedXuehao,
                DetectedMohao: vote.Observation.Mohao ?? "",
                DetectedXuehao: vote.Observation.Xuehao ?? "",
                ConfMohao: vote.Observation.ConfMohao,
                ConfXuehao: vote.Observation.ConfXuehao,
                DelayMs: 0));   // 0 = 用設定檔的 Devices:Blow:DelayMs（見 BlowDispatcher）
        }

        // ── 混料圖歸檔（自我強化訓練的輸入）──────────────────────────
        // 混料被抓到的這一刻，**正解（工單預期值）和模型答錯的內容同時都在手上**，
        // 存成 exp_預期_got_偵測_*.jpg 就是自帶標註的訓練資料，不必再找人標。
        // 只存 MixedAlarm：NG 沒有「正解」可寫，存了也不能拿來訓練。
        // ⚠ 存檔失敗不影響判定與吹氣（已在實作內吞例外）。
        if (decision.Outcome == PairVerifyOutcome.MixedAlarm
            && _mismatchArchive is { Enabled: true } && lastImage is { } img)
        {
            await _mismatchArchive.SaveMismatchAsync(
                img,
                request.ExpectedMohao, request.ExpectedXuehao,
                vote.Observation.Mohao ?? "", vote.Observation.Xuehao ?? "",
                null, cancellationToken).ConfigureAwait(false);
        }

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
