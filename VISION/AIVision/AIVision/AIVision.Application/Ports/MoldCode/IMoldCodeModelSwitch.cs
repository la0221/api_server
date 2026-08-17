using System.Collections.Generic;

namespace AIVision.Application.Ports.MoldCode;

/// <summary>
/// 模號辨識「模型抽換」port:讓 UI(模型管理 / 批量頁 / 工單)在執行期切換當前 ONNX 模型,
/// 而辨識週期(<c>AreaRunService</c> / 命令處理器)仍透過 <see cref="IMoldCodeRecognizerPort"/> 呼叫不變。
/// 實作須 thread-safe(切換與辨識可能跨執行緒)。
/// </summary>
public interface IMoldCodeModelSwitch
{
    /// <summary>
    /// 載入(抽換)模號模型。失敗時須保留前一個模型,不可使辨識器進入不可用狀態。
    /// </summary>
    /// <param name="modelPath">.onnx 模型完整路徑。</param>
    /// <param name="classNames">類別(穴號)清單,依模型輸出索引順序;空 = 沿用 baseline。</param>
    /// <param name="codePrefix">模號前綴(如 "M101");空 = 沿用 baseline。</param>
    void LoadModel(string modelPath, IReadOnlyList<string> classNames, string codePrefix);

    /// <summary>目前已載入模型的 .onnx 路徑(尚未載入則為 null)。</summary>
    string? CurrentModelPath { get; }

    /// <summary>目前封閉字集(由 classNames 組成 "{prefix}/{name}")。</summary>
    IReadOnlyList<string> CurrentClassSet { get; }
}
