namespace AIVision.Domain.MoldCode;

/// <summary>
/// 雙 head（模號 mohao + 穴號 xuehao）一次取像的觀測結果。
/// 與決策層解耦（不含影像型別）；餵給 <see cref="MoldCodePairVoter"/> / <see cref="MoldCodePairVerifier.Decide"/>。
/// fail-closed：辨識失敗一律 <see cref="Failed"/> / <see cref="NoObject"/>，不可回「看似合法」的碼。
/// </summary>
/// <param name="ObjectPresent">是否偵測到鏡片（Hough 命中）。false → 對應 log 的「NO OBJECT」。</param>
/// <param name="Mohao">模號 top-1（如 <c>M101</c>、可能為 <c>NG</c>）；無讀值時為 null。</param>
/// <param name="ConfMohao">模號 top-1 分類信心。</param>
/// <param name="Xuehao">穴號 top-1（如 <c>08</c>）；無讀值時為 null。</param>
/// <param name="ConfXuehao">穴號 top-1 分類信心。</param>
/// <param name="FailureReason">辨識失敗原因（有讀值時為 null）。</param>
public sealed record PairObservation(
    bool ObjectPresent,
    string? Mohao,
    double ConfMohao,
    string? Xuehao,
    double ConfXuehao,
    string? FailureReason = null)
{
    /// <summary>兩軸是否都讀到碼。</summary>
    public bool HasReading =>
        ObjectPresent && !string.IsNullOrWhiteSpace(Mohao) && !string.IsNullOrWhiteSpace(Xuehao);

    /// <summary>沒偵測到鏡片（NO OBJECT）。</summary>
    public static PairObservation NoObject(string? reason = null) =>
        new(false, null, 0, null, 0, reason ?? "no object detected");

    /// <summary>讀到雙軸碼 + 各自信心。</summary>
    public static PairObservation Read(string mohao, double confMohao, string xuehao, double confXuehao) =>
        new(true, mohao, confMohao, xuehao, confXuehao);

    /// <summary>辨識器啟用卻失敗（fail-closed：不可回「看似合法」的碼）。</summary>
    public static PairObservation Failed(string reason) =>
        new(true, null, 0, null, 0, reason);
}
