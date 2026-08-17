// 重新導出 Application 層的 InferenceType，便於 Presentation 層內部使用
// 這樣可以避免在每個使用的地方都需要加 using alias

namespace AIVision.Presentation.Wpf.Models;

/// <summary>
/// 推論類型（重新導出自 Application.Configuration.InferenceType）
/// </summary>
public enum InferenceType
{
    /// <summary>
    /// 傳統單一模型 (POST /inference)
    /// </summary>
    SingleModel = 0,

    /// <summary>
    /// Workflow 多步驟流程 (POST /workflow/run)
    /// </summary>
    Workflow = 1
}
