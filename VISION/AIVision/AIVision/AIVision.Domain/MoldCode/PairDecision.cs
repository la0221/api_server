namespace AIVision.Domain.MoldCode;

/// <summary>
/// <see cref="MoldCodePairVerifier.Decide"/> 的雙軸核對決策。
/// </summary>
/// <param name="Outcome">綜合結果（Match / TrustInput / MixedAlarm / Reject / Skip）。</param>
/// <param name="ClassifiedAs">
/// 歸檔/處置目標：Match / TrustInput → 操作員預期完整碼（如 "M101/08"）；
/// MixedAlarm → <see cref="MismatchBin"/>；Reject → <see cref="RejectBin"/>；Skip → null。
/// </param>
/// <param name="MohaoMismatch">模號軸是否「高信心不符」（稽核用）。</param>
/// <param name="XuehaoMismatch">穴號軸是否「高信心不符」（稽核用）。</param>
/// <param name="Reason">決策理由（稽核用，永遠非 null）。</param>
public sealed record PairDecision(
    PairVerifyOutcome Outcome,
    string? ClassifiedAs,
    bool MohaoMismatch,
    bool XuehaoMismatch,
    string Reason)
{
    /// <summary>混料件歸檔目標（對應 log 的 <c>_MISMATCH</c>；產線對應氣吹剔除）。</summary>
    public const string MismatchBin = "_MISMATCH";

    /// <summary>不良品歸檔目標（NG；產線對應氣吹剔除）。</summary>
    public const string RejectBin = "_NG";

    /// <summary>是否應觸發氣吹剔除（MixedAlarm 或 Reject）。</summary>
    public bool ShouldReject => Outcome is PairVerifyOutcome.MixedAlarm or PairVerifyOutcome.Reject;

    /// <summary>不分類、不處置。</summary>
    public static PairDecision Skip(string reason) =>
        new(PairVerifyOutcome.Skip, null, false, false, reason);
}
