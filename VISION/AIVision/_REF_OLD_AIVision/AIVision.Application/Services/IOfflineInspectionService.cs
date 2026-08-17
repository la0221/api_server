namespace AIVision.Application.Services;

/// <summary>
/// 離線檢測服務 - 用於從資料夾讀取影像並進行 AI 推論
/// </summary>
public interface IOfflineInspectionService
{
    /// <summary>
    /// 載入資料夾中的所有影像檔案
    /// </summary>
    /// <param name="folderPath">資料夾路徑</param>
    /// <returns>成功載入的影像數量</returns>
    Task<int> LoadImagesFromFolderAsync(string folderPath);

    /// <summary>
    /// 取得當前影像檔案路徑
    /// </summary>
    string? CurrentImagePath { get; }

    /// <summary>
    /// 取得當前影像索引 (1-based)
    /// </summary>
    int CurrentImageIndex { get; }

    /// <summary>
    /// 取得總影像數量
    /// </summary>
    int TotalImageCount { get; }

    /// <summary>
    /// 是否有上一張影像
    /// </summary>
    bool HasPrevious { get; }

    /// <summary>
    /// 是否有下一張影像
    /// </summary>
    bool HasNext { get; }

    /// <summary>
    /// 切換到上一張影像
    /// </summary>
    void MoveToPrevious();

    /// <summary>
    /// 切換到下一張影像
    /// </summary>
    void MoveToNext();

    /// <summary>
    /// 執行當前影像的檢測
    /// </summary>
    /// <param name="workOrderId">工單 ID (可選)</param>
    /// <param name="modelVersion">模型版本 (可選)</param>
    /// <returns>檢測結果</returns>
    Task<OfflineInspectionResult> InspectCurrentImageAsync(Guid? workOrderId = null, string? modelVersion = null);

    /// <summary>
    /// 重設統計數據
    /// </summary>
    void ResetStatistics();

    /// <summary>
    /// 統計資料
    /// </summary>
    OfflineInspectionStatistics Statistics { get; }
}

/// <summary>
/// 離線檢測統計資料
/// </summary>
public sealed record OfflineInspectionStatistics
{
    public int TotalInspected { get; init; }
    public int OkCount { get; init; }
    public int NgCount { get; init; }
    public double YieldRate => TotalInspected > 0 ? (double)OkCount / TotalInspected * 100.0 : 0.0;
}

/// <summary>
/// 離線檢測結果
/// </summary>
public sealed record OfflineInspectionResult
{
    public bool IsPass { get; init; }
    public string? DefectType { get; init; }
    public double Confidence { get; init; }
    public string? ImagePath { get; init; }
    public DateTime Timestamp { get; init; }
}
