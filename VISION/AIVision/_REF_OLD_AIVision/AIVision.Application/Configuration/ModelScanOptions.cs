namespace AIVision.Application.Configuration;

/// <summary>
/// 模型掃描配置選項
/// </summary>
public sealed class ModelScanOptions
{
    public const string SectionName = "Models";

    /// <summary>
    /// 模型掃描資料夾路徑
    /// </summary>
    public string ScanFolder { get; set; } = string.Empty;

    /// <summary>
    /// 是否啟用自動掃描
    /// </summary>
    public bool AutoScan { get; set; } = true;

    /// <summary>
    /// 是否在啟動時掃描
    /// </summary>
    public bool ScanOnStartup { get; set; } = true;

    /// <summary>
    /// 是否監控資料夾變化（未來功能）
    /// </summary>
    public bool WatchForChanges { get; set; } = false;
}
