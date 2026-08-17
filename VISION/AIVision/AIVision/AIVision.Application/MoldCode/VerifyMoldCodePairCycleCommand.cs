using MediatR;

namespace AIVision.Application.MoldCode;

/// <summary>
/// 執行一次雙軸模號/穴號核對週期：觸發取像 → 多幀配對投票辨識 → 分軸三態決策 → 混料/NG 則氣吹。
/// </summary>
/// <param name="ExpectedMohao">操作員預期模號（如 "M101"）。</param>
/// <param name="ExpectedXuehao">操作員預期穴號（如 "08"）。</param>
public sealed record VerifyMoldCodePairCycleCommand(string ExpectedMohao, string ExpectedXuehao)
    : IRequest<MoldCodePairCycleResult>;
