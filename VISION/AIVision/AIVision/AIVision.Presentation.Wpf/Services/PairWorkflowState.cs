namespace AIVision.Presentation.Wpf.Services;

/// <summary>
/// 雙 head 離線工作流程的跨頁共用狀態（Singleton）。
/// 讓「模號穴號模型管理」與「批量推論」共享上下文，避免使用者在頁間重複選擇。
/// 目前載入的模型版本本身由 <see cref="AIVision.Application.Ports.MoldCode.IMoldCodePairModelSwitch"/>
/// （同一個 Singleton 辨識器）保存，這裡只補「上次選的影像資料夾」。
/// </summary>
public sealed class PairWorkflowState
{
    /// <summary>上次在任一頁選用的測試/推論影像資料夾（供下一頁自動帶入）。</summary>
    public string? LastImageFolder { get; set; }
}
