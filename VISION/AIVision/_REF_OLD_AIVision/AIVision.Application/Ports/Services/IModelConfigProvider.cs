namespace AIVision.Application.Ports.Services;

/// <summary>
/// 模型配置提供者介面
/// 用於從模型管理取得模型配置（跨層抽象）
/// </summary>
public interface IModelConfigProvider
{
    /// <summary>
    /// 根據模型名稱取得模型配置
    /// </summary>
    /// <param name="modelName">模型名稱</param>
    /// <returns>模型配置，如果找不到則返回 null</returns>
    Task<ModelConfigInfo?> GetModelByNameAsync(string modelName);

    /// <summary>
    /// 取得所有可用的模型配置
    /// </summary>
    /// <returns>所有模型配置列表</returns>
    Task<IReadOnlyList<ModelConfigInfo>> GetAllModelsAsync();
}

/// <summary>
/// 模型配置資訊（跨層 DTO）
/// </summary>
public sealed class ModelConfigInfo
{
    /// <summary>
    /// 模型名稱
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 模型描述
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// 推論類型：SingleModel 或 Workflow
    /// </summary>
    public string InferenceType { get; init; } = "SingleModel";

    /// <summary>
    /// EdgeHub 主機 IP
    /// </summary>
    public string EdgeHubHost { get; init; } = "192.168.1.95";

    /// <summary>
    /// EdgeHub 服務埠號
    /// </summary>
    public int EdgeHubPort { get; init; } = 5001;

    /// <summary>
    /// 推論服務埠號（SingleModel 類型使用）
    /// </summary>
    public int InferencePort { get; init; } = 8001;

    /// <summary>
    /// 模型在伺服器上的完整路徑（SingleModel 類型使用）
    /// </summary>
    public string? ModelPath { get; init; }

    /// <summary>
    /// Workflow 推論服務埠號（Workflow 類型使用）
    /// </summary>
    public int WorkflowPort { get; init; } = 8100;

    /// <summary>
    /// Workflow 設定檔路徑（Workflow 類型使用）
    /// </summary>
    public string? WorkflowSettingPath { get; init; }

    /// <summary>
    /// 是否為 Workflow 模式
    /// </summary>
    public bool IsWorkflow => string.Equals(InferenceType, "Workflow", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否為單一模型模式
    /// </summary>
    public bool IsSingleModel => string.Equals(InferenceType, "SingleModel", StringComparison.OrdinalIgnoreCase);

    // ===== 瑕疵過濾設定 =====

    /// <summary>
    /// 瑕疵過濾是否啟用
    /// </summary>
    public bool DefectFilteringEnabled { get; init; } = false;

    /// <summary>
    /// 單一像素面積 (mm²)
    /// </summary>
    public double PixelAreaMm2 { get; init; } = 0.00000576;

    /// <summary>
    /// 單一像素邊長 (mm)
    /// </summary>
    public double PixelSizeMm { get; init; } = 0.0024;

    /// <summary>
    /// 最小檢出面積閾值 (mm²)
    /// </summary>
    public double MinimumAreaMm2 { get; init; } = 0.02;

    /// <summary>
    /// 中等/大瑕疵分界閾值 (mm²)
    /// </summary>
    public double MediumAreaMm2 { get; init; } = 0.05;

    /// <summary>
    /// 群聚距離閾值 (mm)
    /// </summary>
    public double CloseDistanceMm { get; init; } = 50.0;

    /// <summary>
    /// 關鍵瑕疵類別列表
    /// </summary>
    public IReadOnlyList<string> CriticalClasses { get; init; } = Array.Empty<string>();
}
