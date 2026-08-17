using System.Collections.Generic;
using AIVision.Domain.MoldCode;
using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.MoldCode;

/// <summary>
/// 封閉字集模號辨識器 port(可抽換)。本案實作 = 本地 ONNX YOLO + blackhat 前處理
/// (<c>OnnxMoldCodeRecognizer</c>,不放 Core/Infra,獨立專案以避相機 DLL 相依)。
/// 失敗(無物件 / 辨識不出 / 例外)一律回 fail-closed 的 <see cref="MarkingObservation"/>
/// (HasCode=false),不可回傳「看似合法」的碼 —— 由 <see cref="MarkingVerifier"/> 決定三態。
/// </summary>
public interface IMoldCodeRecognizerPort
{
    /// <summary>
    /// 對一張(已是 ROI 的)影像辨識模號碼。
    /// </summary>
    /// <param name="image">ROI 影像(灰階 Mono8 或 Bgr24)。</param>
    /// <param name="classSet">封閉字集(如 M101/01..18);空 = 不限制。</param>
    /// <returns>觀測結果(含雙信心度),餵給投票 / <see cref="MarkingVerifier.Decide"/>。</returns>
    MarkingObservation Recognize(ImageData image, IReadOnlyList<string> classSet);
}
