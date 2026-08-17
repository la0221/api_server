namespace AIVision.Domain.AutoRun;

/// <summary>
/// Auto Run 設定選項
/// </summary>
public class AutoRunOptions
{
    /// <summary>相機模式</summary>
    public CameraMode CameraMode { get; set; } = CameraMode.LineScan;

    /// <summary>Line Scan 設定</summary>
    public LineScanSettings? LineScanSettings { get; set; }

    /// <summary>工單代碼（用於圖片儲存路徑）</summary>
    public string? WorkOrderCode { get; set; }

    /// <summary>是否儲存圖片</summary>
    public bool SaveImages { get; set; } = true;

    /// <summary>
    /// 是否跳過推論（訓練蒐圖模式）
    /// 設為 true 時只取像存圖，不執行 AI 推論
    /// </summary>
    public bool SkipInference { get; set; } = false;

    /// <summary>
    /// 跳過推論時的模擬結果: "Ok", "Ng", "Random"
    /// 只在 SkipInference = true 時生效
    /// </summary>
    public string SkipInferenceResult { get; set; } = "Ok";

    /// <summary>單次檢測超時 (ms)</summary>
    public int InspectionTimeoutMs { get; set; } = 30000;

    /// <summary>取像超時 (ms)</summary>
    public int CaptureTimeoutMs { get; set; } = 10000;

    /// <summary>推論超時 (ms)</summary>
    public int InferenceTimeoutMs { get; set; } = 5000;

    /// <summary>錯誤時自動重試</summary>
    public bool AutoRetryOnError { get; set; } = true;

    /// <summary>最大重試次數</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>結果信號保持時間 (ms)</summary>
    public int ResultHoldTimeMs { get; set; } = 100;

    /// <summary>連續錯誤達此數量後停止</summary>
    public int MaxConsecutiveErrors { get; set; } = 5;
}

/// <summary>
/// 相機模式
/// </summary>
public enum CameraMode
{
    /// <summary>面陣相機</summary>
    AreaScan,

    /// <summary>線掃相機</summary>
    LineScan
}

/// <summary>
/// Line Scan 設定
/// </summary>
public class LineScanSettings
{
    /// <summary>ROI X 偏移</summary>
    public int OffsetX { get; set; }

    /// <summary>ROI Y 偏移</summary>
    public int OffsetY { get; set; }

    /// <summary>ROI 寬度</summary>
    public int Width { get; set; }

    /// <summary>目標行數 (影像高度)</summary>
    public int TargetHeight { get; set; }

    /// <summary>行速率 (Hz)</summary>
    public double LineRate { get; set; } = 10000;

    /// <summary>曝光時間 (us)</summary>
    public double ExposureTimeUs { get; set; } = 50;
}
