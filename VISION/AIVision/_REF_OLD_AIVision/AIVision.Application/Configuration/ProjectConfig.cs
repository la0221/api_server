using System.Text.Json.Serialization;

namespace AIVision.Application.Configuration;

/// <summary>
/// 專案配置 - 一鍵載入專案功能的設定檔結構
/// 對應 project.json 檔案
/// </summary>
public sealed class ProjectConfig
{
    /// <summary>
    /// 專案名稱
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 專案描述
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 關聯的模型名稱（對應 models.json 中的 ModelConfig.Name）
    /// 優先使用此欄位從模型管理載入設定
    /// </summary>
    public string? ModelName { get; set; }

    /// <summary>
    /// 專案資料夾路徑（runtime 設定，不序列化到 JSON）
    /// </summary>
    [JsonIgnore]
    public string? FolderPath { get; set; }

    /// <summary>
    /// 相機設定
    /// </summary>
    public ProjectCameraConfig? Camera { get; set; }

    /// <summary>
    /// 光源設定
    /// </summary>
    public ProjectLightConfig? Light { get; set; }

    /// <summary>
    /// PLC 設定
    /// </summary>
    public ProjectPlcConfig? Plc { get; set; }

    /// <summary>
    /// AI 模型設定（當 ModelName 為空時作為 Fallback）
    /// 建議改用 ModelName 引用模型管理中的設定
    /// </summary>
    public ProjectAiModelConfig? AiModel { get; set; }

    /// <summary>
    /// 瑕疵過濾設定
    /// </summary>
    public ProjectDefectFilteringConfig? DefectFiltering { get; set; }

    /// <summary>
    /// 取得顯示名稱（用於 UI）
    /// </summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(Description)
        ? Name
        : $"{Name} - {Description}";
}

/// <summary>
/// 專案相機設定
/// </summary>
public sealed class ProjectCameraConfig
{
    /// <summary>
    /// 相機設定檔路徑（.cset 檔案，相對於專案資料夾）
    /// </summary>
    public string? CamFilePath { get; set; }

    /// <summary>
    /// 要載入的 UserSet 名稱（UserSet0, UserSet1, UserSet2）
    /// </summary>
    public string UserSet { get; set; } = "UserSet0";
}

/// <summary>
/// 專案光源設定
/// </summary>
public sealed class ProjectLightConfig
{
    /// <summary>
    /// 介面類型：Serial 或 TCP
    /// </summary>
    public string Interface { get; set; } = "Serial";

    /// <summary>
    /// Serial 介面的 COM Port 名稱（例如 COM3）
    /// </summary>
    public string? PortName { get; set; }

    /// <summary>
    /// TCP 介面的 IP 位址
    /// </summary>
    public string? TcpHost { get; set; }

    /// <summary>
    /// TCP 介面的埠號
    /// </summary>
    public int? TcpPort { get; set; }

    /// <summary>
    /// 各通道亮度設定（Key: 通道編號, Value: 亮度值 0-255）
    /// </summary>
    public Dictionary<string, int>? ChannelBrightness { get; set; }

    /// <summary>
    /// 是否為 Serial 介面
    /// </summary>
    [JsonIgnore]
    public bool IsSerial => string.Equals(Interface, "Serial", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否為 TCP 介面
    /// </summary>
    [JsonIgnore]
    public bool IsTcp => string.Equals(Interface, "TCP", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 專案 PLC 設定
/// </summary>
public sealed class ProjectPlcConfig
{
    /// <summary>
    /// PLC IP 位址
    /// </summary>
    public string Host { get; set; } = "192.168.0.20";

    /// <summary>
    /// PLC Modbus TCP 埠號
    /// </summary>
    public int Port { get; set; } = 502;
}

/// <summary>
/// 專案 AI 模型設定
/// </summary>
public sealed class ProjectAiModelConfig
{
    /// <summary>
    /// 推論類型：SingleModel 或 Workflow
    /// </summary>
    public string InferenceType { get; set; } = "SingleModel";

    /// <summary>
    /// EdgeHub 主機 IP
    /// </summary>
    public string EdgeHubHost { get; set; } = "192.168.1.95";

    /// <summary>
    /// EdgeHub 服務埠號
    /// </summary>
    public int EdgeHubPort { get; set; } = 5001;

    /// <summary>
    /// 單一模型推論埠號（僅 SingleModel 類型使用）
    /// </summary>
    public int InferencePort { get; set; } = 8001;

    /// <summary>
    /// 模型在伺服器上的路徑（僅 SingleModel 類型使用）
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// Workflow 推論埠號（僅 Workflow 類型使用）
    /// </summary>
    public int WorkflowPort { get; set; } = 8100;

    /// <summary>
    /// Workflow 設定檔路徑（僅 Workflow 類型使用）
    /// </summary>
    public string? WorkflowSettingPath { get; set; }

    /// <summary>
    /// 是否為 Workflow 模式
    /// </summary>
    [JsonIgnore]
    public bool IsWorkflow => string.Equals(InferenceType, "Workflow", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否為單一模型模式
    /// </summary>
    [JsonIgnore]
    public bool IsSingleModel => string.Equals(InferenceType, "SingleModel", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 取得 EdgeHub 管理 API URL
    /// </summary>
    public string GetEdgeHubUrl() => $"http://{EdgeHubHost}:{EdgeHubPort}";

    /// <summary>
    /// 取得推論 URL（根據類型自動選擇）
    /// </summary>
    public string GetInferenceUrl() => IsWorkflow
        ? $"http://{EdgeHubHost}:{WorkflowPort}/workflow/run"
        : $"http://{EdgeHubHost}:{InferencePort}/inference";
}

/// <summary>
/// 專案瑕疵過濾設定
/// </summary>
public sealed class ProjectDefectFilteringConfig
{
    /// <summary>
    /// 是否啟用瑕疵過濾規則
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 單一像素面積 (mm²)
    /// 預設值對應 2.4×2.4 µm² = 0.00000576 mm²
    /// </summary>
    public double PixelAreaMm2 { get; set; } = 0.00000576;

    /// <summary>
    /// 單一像素邊長 (mm)
    /// 預設值對應 2.4 µm = 0.0024 mm
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

    /// <summary>
    /// 轉換為 DefectFilteringOptions
    /// </summary>
    public DefectFilteringOptions ToOptions()
    {
        return new DefectFilteringOptions
        {
            Enabled = Enabled,
            PixelAreaMm2 = PixelAreaMm2,
            PixelSizeMm = PixelSizeMm,
            MinimumAreaMm2 = MinimumAreaMm2,
            MediumAreaMm2 = MediumAreaMm2,
            CloseDistanceMm = CloseDistanceMm,
            CriticalClasses = CriticalClasses
        };
    }
}
