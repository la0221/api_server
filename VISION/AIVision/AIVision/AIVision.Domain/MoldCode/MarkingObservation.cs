namespace AIVision.Domain.MoldCode;

/// <summary>
/// 一次取像後、辨識器（<c>IMoldCodeRecognizerPort</c> 的實作）對 ROI 內標記的觀測結果。
/// 與決策層解耦:不含影像型別,<see cref="MarkingVerifier.Decide"/> 只吃這些純量。
/// 移植自 OpenCV_Vision.Core.MarkingVerify(反哺後預計統一為共用庫)。
/// </summary>
/// <param name="ObjectPresent">物件存在閘(鏡片是否在 ROI)。false → 對應 log 的「NO OBJECT」。</param>
/// <param name="HasCode">辨識器是否產出可用碼。false 時 <see cref="Code"/> 無意義,看 <see cref="FailureReason"/>。</param>
/// <param name="Code">辨識出的模號(如 <c>"M101/04"</c>;分隔符不拘,比對前正規化)。</param>
/// <param name="ConfDetect">偵測信心(log 的第一個 conf,分類模型恆 ~1.0;保留供記錄/未來閘)。</param>
/// <param name="ConfClassify">分類信心(log 的第二個 conf,決策依據)。</param>
/// <param name="FailureReason">辨識失敗原因(HasCode=true 時為 null)。</param>
public sealed record MarkingObservation(
    bool ObjectPresent,
    bool HasCode,
    string? Code,
    double ConfDetect,
    double ConfClassify,
    string? FailureReason = null)
{
    /// <summary>沒偵測到物件(NO OBJECT)。</summary>
    public static MarkingObservation NoObject(string? reason = null) =>
        new(false, false, null, 0, 0, reason ?? "no object detected");

    /// <summary>辨識器讀到一個碼 + 雙信心度。</summary>
    public static MarkingObservation Read(string code, double confDetect, double confClassify) =>
        new(true, true, code, confDetect, confClassify);

    /// <summary>辨識器啟用卻失敗(fail-closed:不可回傳「看似合法」的碼)。</summary>
    public static MarkingObservation Failed(string reason) =>
        new(true, false, null, 0, 0, reason);
}
