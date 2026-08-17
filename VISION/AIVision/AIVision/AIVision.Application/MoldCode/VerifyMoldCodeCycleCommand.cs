using MediatR;

namespace AIVision.Application.MoldCode;

/// <summary>
/// 執行一次模號核對週期:觸發取像 → 多幀投票辨識 → 三態決策 → 混料則氣吹。
/// </summary>
/// <param name="ExpectedCode">操作員預期模號(如 "M101/07")。</param>
public sealed record VerifyMoldCodeCycleCommand(string ExpectedCode)
    : IRequest<MoldCodeCycleResult>;
