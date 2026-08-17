namespace AIVision.Application.Ports.MoldCode;

/// <summary>
/// 雙 head（模號 + 穴號）模型「版本抽換」port：讓 UI（雙軸模型管理頁）在執行期切換目前使用的
/// 雙 head 模型版本，而辨識端仍透過 <see cref="IMoldCodePairRecognizerPort"/> 呼叫不變。
/// 實作須 thread-safe（切換與辨識可能跨執行緒）。切換失敗須保留前一個模型，不可使辨識器失能。
/// </summary>
public interface IMoldCodePairModelSwitch
{
    /// <summary>
    /// 載入（抽換）一組雙 head 模型版本。
    /// </summary>
    /// <param name="mohaoOnnxPath">模號 head .onnx 完整路徑。</param>
    /// <param name="xuehaoOnnxPath">穴號 head .onnx 完整路徑。</param>
    /// <param name="versionName">版本顯示名稱（如 <c>v6.7.2</c>）。</param>
    void LoadVersion(string mohaoOnnxPath, string xuehaoOnnxPath, string versionName);

    /// <summary>目前已載入的版本名稱（尚未明確載入則為 baseline 名稱或 null）。</summary>
    string? CurrentVersionName { get; }

    /// <summary>目前模型的模號類別清單（依索引；如 M101…NG）。未載入則為空。供工單「預期模號」下拉。</summary>
    System.Collections.Generic.IReadOnlyList<string> CurrentMohaoNames { get; }

    /// <summary>目前模型的穴號類別清單（依索引；如 01…18）。未載入則為空。供工單「預期穴號」下拉。</summary>
    System.Collections.Generic.IReadOnlyList<string> CurrentXuehaoNames { get; }
}
