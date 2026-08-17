using AIVision.Domain.MoldCode;

namespace AIVision.Application.MoldCode;

/// <summary>
/// 雙軸模號/穴號核對週期結果。
/// </summary>
/// <param name="Outcome">分軸三態（+Reject/Skip）決策。</param>
/// <param name="ReadMohao">投票後讀到的模號（可能 null）。</param>
/// <param name="ReadXuehao">投票後讀到的穴號（可能 null）。</param>
/// <param name="ConfMohao">勝出配對的模號平均信心。</param>
/// <param name="ConfXuehao">勝出配對的穴號平均信心。</param>
/// <param name="ClassifiedAs">歸檔目標（預期完整碼 / _MISMATCH / _NG / null）。</param>
/// <param name="Frames">取了幾幀。</param>
/// <param name="WinnerVotes">勝出配對票數。</param>
/// <param name="AirBlown">是否觸發氣吹剔除（MixedAlarm 或 Reject）。</param>
/// <param name="ElapsedMs">辨識端到端耗時。</param>
/// <param name="Reason">決策理由（稽核）。</param>
public sealed record MoldCodePairCycleResult(
    PairVerifyOutcome Outcome,
    string? ReadMohao,
    string? ReadXuehao,
    double ConfMohao,
    double ConfXuehao,
    string? ClassifiedAs,
    int Frames,
    int WinnerVotes,
    bool AirBlown,
    long ElapsedMs,
    string Reason);
