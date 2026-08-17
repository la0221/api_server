using AIVision.Domain.MoldCode;

namespace AIVision.Application.MoldCode;

/// <summary>
/// 模號核對週期結果。
/// </summary>
/// <param name="Outcome">三態決策。</param>
/// <param name="ReadCode">投票後讀到的碼(可能 null)。</param>
/// <param name="ClassifiedAs">歸檔目標(預期碼 / _MISMATCH / null)。</param>
/// <param name="Confidence">勝出碼的平均分類信心。</param>
/// <param name="Frames">取了幾幀。</param>
/// <param name="WinnerVotes">勝出碼票數。</param>
/// <param name="AirBlown">是否觸發氣吹剔除(MixedAlarm)。</param>
/// <param name="ElapsedMs">辨識端到端耗時。</param>
/// <param name="Reason">決策理由(稽核)。</param>
public sealed record MoldCodeCycleResult(
    MarkingVerifyOutcome Outcome,
    string? ReadCode,
    string? ClassifiedAs,
    double Confidence,
    int Frames,
    int WinnerVotes,
    bool AirBlown,
    long ElapsedMs,
    string Reason);
