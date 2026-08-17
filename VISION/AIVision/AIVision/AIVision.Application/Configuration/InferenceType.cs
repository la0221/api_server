namespace AIVision.Application.Configuration;

/// <summary>
/// AI 推論類型
/// </summary>
public enum InferenceType
{
    /// <summary>
    /// 單一模型推論 (使用 POST /inference API)
    /// </summary>
    SingleModel = 0,

    /// <summary>
    /// Workflow 多步驟推論 (使用 POST /workflow/run API)
    /// </summary>
    Workflow = 1
}
