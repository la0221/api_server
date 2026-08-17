namespace AIVision.Presentation.Wpf.Models;

/// <summary>
/// AI 模型配置。
/// </summary>
public sealed class ModelConfig
{
    /// <summary>
    /// 模型顯示名稱
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 取得帶有類型標記的顯示名稱（用於 ComboBox 等 UI 元素）
    /// </summary>
    public string DisplayNameWithType => InferenceType switch
    {
        InferenceType.Workflow => $"[Workflow] {Name}",
        _ => $"[模型] {Name}"
    };

    /// <summary>
    /// 模型原始名稱（資料夾名稱，用於識別）
    /// </summary>
    public string? OriginalName { get; set; }

    /// <summary>
    /// 模型描述
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 模型資料夾路徑（自動掃描時設定）
    /// </summary>
    public string? FolderPath { get; set; }

    /// <summary>
    /// 是否為自動掃描發現的模型
    /// </summary>
    public bool IsAutoDiscovered { get; set; }

    // ===== 雙 head 配對模型（warpPolar/annulus；由 *.pair.json 描述）=====

    /// <summary>
    /// 推論管線：null/空 = 單 head 本地 ONNX（blackhat）；<c>"warpPolar"</c> = 雙 head（模號+穴號）。
    /// </summary>
    public string? Pipeline { get; set; }

    /// <summary>是否為雙 head warpPolar 配對模型（XAML 綁定/判斷用）。</summary>
    public bool IsPairModel => string.Equals(Pipeline, "warpPolar", StringComparison.OrdinalIgnoreCase);

    /// <summary>雙 head：模號 head ONNX 路徑。</summary>
    public string? MohaoModelPath { get; set; }

    /// <summary>雙 head：穴號 head ONNX 路徑。</summary>
    public string? XuehaoModelPath { get; set; }

    /// <summary>雙 head：接縫修正 pass 數（0 = 用辨識器預設 2）。</summary>
    public int Passes { get; set; }

    /// <summary>
    /// AINAVI Plugin 類型（det_1s, seg_2, cls_1）
    /// </summary>
    public string? Plugin { get; set; }

    /// <summary>
    /// 輸入尺寸 [batch, channels, height, width]
    /// </summary>
    public int[]? InputShape { get; set; }

    /// <summary>
    /// EdgeHub 主機 IP（例如 192.168.1.95）
    /// </summary>
    public string EdgeHubHost { get; set; } = "192.168.1.95";

    /// <summary>
    /// EdgeHub 服務埠號（例如 5001）
    /// </summary>
    public int EdgeHubPort { get; set; } = 5001;

    /// <summary>
    /// 模型 UUID（通常固定為 "model"）
    /// </summary>
    public string Uuid { get; set; } = "model";

    /// <summary>
    /// 模型在伺服器上的完整路徑
    /// 例如：/home/smasoft/Public/ainavi_edgehub/models/Ecoco_cls_251103
    /// </summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>
    /// 推論服務埠號（例如 8001）
    /// </summary>
    public int InferencePort { get; set; } = 8001;

    /// <summary>
    /// 取得實際使用的推論埠號（根據 InferenceType 自動選擇）
    /// - SingleModel: 返回 InferencePort
    /// - Workflow: 返回 WorkflowPort
    /// </summary>
    public int EffectiveInferencePort => InferenceType switch
    {
        InferenceType.Workflow => WorkflowPort,
        _ => InferencePort
    };

    /// <summary>
    /// 模型類型（分類或檢測）
    /// </summary>
    public ModelType ModelType { get; set; } = ModelType.Classification;

    /// <summary>
    /// 推論類型：SingleModel（單一模型）或 Workflow（多步驟流程）
    /// </summary>
    public InferenceType InferenceType { get; set; } = InferenceType.SingleModel;

    /// <summary>
    /// Workflow 設定檔路徑（僅 Workflow 類型使用）
    /// 例如：C:\Users\Public\Documents\Spingence\AINavi\workflows\myflow\workflow_setting.json
    /// </summary>
    public string? WorkflowSettingPath { get; set; }

    /// <summary>
    /// Workflow 推論服務埠號（僅 Workflow 類型使用，預設 8100）
    /// </summary>
    public int WorkflowPort { get; set; } = 8100;

    /// <summary>
    /// 模型可能產生的結果類型清單。
    /// - 對於分類模型：例如 ["A1", "A2", "A3", "A4", "OTHERS"]
    /// - 對於檢測模型：例如 ["scratch", "stain", "dirty"]
    /// </summary>
    public List<string> ResultTypes { get; set; } = new();

    /// <summary>
    /// 結果類別的彙整顯示字串(供模型管理 DataGrid 顯示;空則顯示「—」)。
    /// </summary>
    public string ResultTypesSummary =>
        ResultTypes is { Count: > 0 } ? $"{ResultTypes.Count} 類: {string.Join(", ", ResultTypes)}" : "—";

    /// <summary>
    /// 結果類型的顯示名稱映射（可選，用於 UI 顯示）。
    /// 例如：{ "scratch": "刮痕", "stain": "髒汙" }
    /// </summary>
    public Dictionary<string, string>? ResultTypeDisplayNames { get; set; }

    /// <summary>
    /// 取得結果類型的顯示名稱。
    /// </summary>
    /// <param name="resultType">結果類型</param>
    /// <returns>顯示名稱，如果沒有映射則返回原始類型名稱</returns>
    public string GetDisplayName(string resultType)
    {
        if (ResultTypeDisplayNames != null &&
            ResultTypeDisplayNames.TryGetValue(resultType, out var displayName))
        {
            return displayName;
        }
        return resultType;
    }

    /// <summary>
    /// 取得有效的結果類型列表（如果未配置則返回空列表）。
    /// </summary>
    /// <returns>結果類型列表</returns>
    public IReadOnlyList<string> GetEffectiveResultTypes()
    {
        // 直接返回模型設定的 ResultTypes，不再提供預設值
        return ResultTypes;
    }

    /// <summary>
    /// 取得 EdgeHub 完整 URL
    /// </summary>
    public string GetEdgeHubUrl() => $"http://{EdgeHubHost}:{EdgeHubPort}";

    /// <summary>
    /// 取得推論 URL（根據推論類型自動選擇）
    /// </summary>
    public string GetInferenceUrl() => InferenceType switch
    {
        InferenceType.Workflow => $"http://{EdgeHubHost}:{WorkflowPort}/workflow/run",
        _ => $"http://{EdgeHubHost}:{InferencePort}/inference"
    };

    /// <summary>
    /// 取得 Workflow 服務啟動 URL
    /// </summary>
    public string GetWorkflowServiceUrl() => $"http://{EdgeHubHost}:{EdgeHubPort}/service/workflow";

    // ===== 瑕疵過濾設定 =====

    /// <summary>
    /// 瑕疵過濾設定
    /// </summary>
    public DefectFilteringConfig? DefectFiltering { get; set; }

    /// <summary>
    /// 瑕疵過濾是否啟用（安全屬性，用於 XAML 綁定）
    /// </summary>
    public bool IsDefectFilteringEnabled => DefectFiltering?.Enabled ?? false;
}

/// <summary>
/// 瑕疵過濾配置（用於模型設定）
/// </summary>
public sealed class DefectFilteringConfig
{
    /// <summary>
    /// 是否啟用瑕疵過濾
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 單一像素面積 (mm²)
    /// 預設: 2.4×2.4 µm² = 0.00000576 mm²
    /// </summary>
    public double PixelAreaMm2 { get; set; } = 0.00000576;

    /// <summary>
    /// 單一像素邊長 (mm)
    /// 預設: 2.4 µm = 0.0024 mm
    /// </summary>
    public double PixelSizeMm { get; set; } = 0.0024;

    /// <summary>
    /// 最小檢出面積閾值 (mm²)
    /// 小於此值的瑕疵將被忽略
    /// </summary>
    public double MinimumAreaMm2 { get; set; } = 0.02;

    /// <summary>
    /// 中等/大瑕疵分界閾值 (mm²)
    /// 大於或等於此值的瑕疵直接判定為 NG
    /// </summary>
    public double MediumAreaMm2 { get; set; } = 0.05;

    /// <summary>
    /// 群聚距離閾值 (mm)
    /// 中等瑕疵之間距離小於此值時判定為 NG
    /// </summary>
    public double CloseDistanceMm { get; set; } = 50.0;

    /// <summary>
    /// 關鍵瑕疵類別列表
    /// 這些類別的瑕疵無論大小都直接判定為 NG
    /// </summary>
    public List<string> CriticalClasses { get; set; } = new();
}

