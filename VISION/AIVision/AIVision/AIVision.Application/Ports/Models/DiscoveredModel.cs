namespace AIVision.Application.Ports.Models;

/// <summary>
/// 掃描發現的模型資訊
/// </summary>
public sealed class DiscoveredModel
{
    /// <summary>
    /// 模型資料夾完整路徑
    /// </summary>
    public required string FolderPath { get; init; }

    /// <summary>
    /// 資料夾名稱（作為唯一識別）
    /// </summary>
    public required string FolderName { get; init; }

    /// <summary>
    /// 模型名稱（從 info.json 讀取，若無則使用 FolderName）
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 模型描述（從 info.json 讀取）
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// AINAVI Plugin 類型：det_1s, seg_2, cls_1
    /// </summary>
    public required string Plugin { get; init; }

    /// <summary>
    /// 模型類型（由 Plugin 轉換）
    /// </summary>
    public AinaviModelType ModelType { get; init; }

    /// <summary>
    /// 輸入尺寸 [batch, channels, height, width]
    /// </summary>
    public int[] InputShape { get; init; } = [];

    /// <summary>
    /// 模型權重檔名
    /// </summary>
    public string ModelFileName { get; init; } = "final.mw";

    /// <summary>
    /// 類別對照表 {index: className}
    /// </summary>
    public IReadOnlyDictionary<int, string> ClassMap { get; init; } = new Dictionary<int, string>();

    /// <summary>
    /// 瑕疵類別清單（從 ClassMap 值提取）
    /// </summary>
    public IReadOnlyList<string> DefectClasses { get; init; } = [];

    /// <summary>
    /// 掃描發現時間
    /// </summary>
    public DateTime DiscoveredAt { get; init; } = DateTime.Now;
}

/// <summary>
/// AINAVI 模型類型
/// </summary>
public enum AinaviModelType
{
    Unknown = 0,
    Classification = 1,  // cls_1
    Detection = 2,       // det_1s
    Segmentation = 3     // seg_2
}
