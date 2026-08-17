namespace AIVision.Domain.MoldCode;

/// <summary>
/// <see cref="MarkingVerifier.Decide"/> 的三態核對決策。
/// </summary>
/// <param name="Outcome">三態(+Skip)結果。</param>
/// <param name="ClassifiedAs">
/// 該存到哪:Match / TrustInput → 操作員預期碼;MixedAlarm → <see cref="MismatchBin"/>;Skip → null(不存)。
/// </param>
/// <param name="Reason">決策理由(稽核用,永遠非 null)。</param>
public sealed record MarkingDecision(
    MarkingVerifyOutcome Outcome,
    string? ClassifiedAs,
    string Reason)
{
    /// <summary>混料件的歸檔目標(對應 log 的 <c>_MISMATCH</c> 夾;產線對應氣吹剔除)。</summary>
    public const string MismatchBin = "_MISMATCH";

    /// <summary>不分類、不存圖。</summary>
    public static MarkingDecision Skip(string reason) =>
        new(MarkingVerifyOutcome.Skip, null, reason);
}
