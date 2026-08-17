using AIVision.Domain.MoldCode;
using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.MoldCode;

/// <summary>
/// 雙 head（模號 + 穴號）封閉集辨識器 port（可抽換）。本案實作 = 本地 ONNX yolov8s-cls ×2
/// + warpPolar/annulus 前處理（<c>WarpPolarTwoHeadRecognizer</c>）。
/// 失敗（無物件 / 辨識不出 / 例外）一律回 fail-closed 的 <see cref="PairObservation"/>
/// （HasReading=false），不可回「看似合法」的碼 —— 由 <see cref="MoldCodePairVerifier"/> 決定。
/// </summary>
public interface IMoldCodePairRecognizerPort
{
    /// <summary>
    /// 對一張影像同時辨識模號與穴號（內含多 pass 接縫修正）。
    /// </summary>
    /// <param name="image">影像（灰階 Mono8 或 Bgr24；應為已含整顆鏡片的判定區域）。</param>
    /// <returns>雙軸觀測（含各軸信心），餵給投票 / <see cref="MoldCodePairVerifier.Decide"/>。</returns>
    PairObservation Recognize(ImageData image);
}
