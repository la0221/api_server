using AIVision.Application.Configuration;
using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.Services;

/// <summary>
/// 瑕疵過濾服務介面
/// 提供瑕疵尺寸與距離過濾功能
/// </summary>
public interface IDefectFilteringService
{
    /// <summary>
    /// 過濾瑕疵並判定結果
    /// </summary>
    /// <param name="defects">AI 偵測到的原始瑕疵列表</param>
    /// <returns>過濾結果，包含有效瑕疵、被過濾瑕疵及判定結果</returns>
    DefectFilteringResult FilterDefects(IReadOnlyList<WorkflowDefect> defects);

    /// <summary>
    /// 更新過濾配置選項
    /// 用於專案載入時動態更新配置
    /// </summary>
    /// <param name="options">新的配置選項</param>
    void UpdateOptions(DefectFilteringOptions options);

    /// <summary>
    /// 取得目前的配置選項
    /// </summary>
    DefectFilteringOptions GetCurrentOptions();

    /// <summary>
    /// 計算瑕疵面積 (mm²)
    /// </summary>
    /// <param name="pixelArea">像素面積</param>
    /// <returns>面積 (mm²)</returns>
    double CalculateAreaMm2(int pixelArea);

    /// <summary>
    /// 計算兩個瑕疵之間的距離 (mm)
    /// </summary>
    /// <param name="defect1">瑕疵 1</param>
    /// <param name="defect2">瑕疵 2</param>
    /// <returns>距離 (mm)</returns>
    double CalculateDistanceMm(WorkflowDefect defect1, WorkflowDefect defect2);
}

/// <summary>
/// 瑕疵過濾結果
/// </summary>
public sealed record DefectFilteringResult
{
    /// <summary>
    /// 最終判定結果（true = OK, false = NG）
    /// </summary>
    public bool IsOk { get; init; }

    /// <summary>
    /// 有效瑕疵列表（會被判定為 NG 的瑕疵）
    /// </summary>
    public IReadOnlyList<WorkflowDefect> ValidDefects { get; init; } = Array.Empty<WorkflowDefect>();

    /// <summary>
    /// 被過濾瑕疵列表（忽略的小瑕疵）
    /// </summary>
    public IReadOnlyList<WorkflowDefect> FilteredDefects { get; init; } = Array.Empty<WorkflowDefect>();

    /// <summary>
    /// 過濾原因說明
    /// </summary>
    public string FilteringReason { get; init; } = string.Empty;

    /// <summary>
    /// 大瑕疵數量
    /// </summary>
    public int LargeDefectCount { get; init; }

    /// <summary>
    /// 中等瑕疵數量
    /// </summary>
    public int MediumDefectCount { get; init; }

    /// <summary>
    /// 小瑕疵數量（被過濾）
    /// </summary>
    public int SmallDefectCount { get; init; }

    /// <summary>
    /// 關鍵瑕疵數量
    /// </summary>
    public int CriticalDefectCount { get; init; }

    /// <summary>
    /// 供 Overlay 使用的瑕疵列表
    /// Phase 1: 只回傳有效瑕疵
    /// Phase 2: 可回傳全部瑕疵（有效 + 過濾）
    /// </summary>
    public IReadOnlyList<WorkflowDefect> DefectsForOverlay => ValidDefects;
}

/// <summary>
/// 瑕疵尺寸分類
/// </summary>
public enum DefectSizeCategory
{
    /// <summary>
    /// 小瑕疵（≤ MinimumAreaMm2，忽略）
    /// </summary>
    Small,

    /// <summary>
    /// 中等瑕疵（MinimumAreaMm2 ~ MediumAreaMm2，條件判定）
    /// </summary>
    Medium,

    /// <summary>
    /// 大瑕疵（≥ MediumAreaMm2，直接 NG）
    /// </summary>
    Large,

    /// <summary>
    /// 關鍵瑕疵（屬於 CriticalClasses，直接 NG）
    /// </summary>
    Critical
}
