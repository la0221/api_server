namespace AIVision.Domain.AutoRun;

/// <summary>
/// Auto Run 統計資料
/// </summary>
public class AutoRunStatistics
{
    /// <summary>總檢測數</summary>
    public int TotalCount { get; set; }

    /// <summary>合格數</summary>
    public int OkCount { get; set; }

    /// <summary>不合格數</summary>
    public int NgCount { get; set; }

    /// <summary>錯誤數</summary>
    public int ErrorCount { get; set; }

    /// <summary>連續錯誤數</summary>
    public int ConsecutiveErrorCount { get; set; }

    /// <summary>良率 (%)</summary>
    public double YieldRate => TotalCount > 0 ? (double)OkCount / TotalCount * 100 : 0;

    /// <summary>平均取像時間 (ms)</summary>
    public double AverageCaptureTimeMs { get; set; }

    /// <summary>平均推論時間 (ms)</summary>
    public double AverageInferenceTimeMs { get; set; }

    /// <summary>平均總耗時 (ms)</summary>
    public double AverageTotalTimeMs { get; set; }

    /// <summary>開始時間</summary>
    public DateTime? StartTime { get; set; }

    /// <summary>最後更新時間</summary>
    public DateTime? LastUpdateTime { get; set; }

    /// <summary>運行時長</summary>
    public TimeSpan RunningDuration => StartTime.HasValue
        ? (LastUpdateTime ?? DateTime.Now) - StartTime.Value
        : TimeSpan.Zero;

    /// <summary>每小時產量 (UPH)</summary>
    public double UnitsPerHour => RunningDuration.TotalHours > 0
        ? TotalCount / RunningDuration.TotalHours
        : 0;

    /// <summary>重置統計</summary>
    public void Reset()
    {
        TotalCount = 0;
        OkCount = 0;
        NgCount = 0;
        ErrorCount = 0;
        ConsecutiveErrorCount = 0;
        AverageCaptureTimeMs = 0;
        AverageInferenceTimeMs = 0;
        AverageTotalTimeMs = 0;
        StartTime = null;
        LastUpdateTime = null;
    }

    /// <summary>
    /// 從資料庫載入的統計值初始化（用於載入工單時同步）
    /// </summary>
    /// <param name="totalCount">總檢測數</param>
    /// <param name="okCount">合格數</param>
    /// <param name="ngCount">不合格數</param>
    public void Initialize(int totalCount, int okCount, int ngCount)
    {
        TotalCount = totalCount;
        OkCount = okCount;
        NgCount = ngCount;
        ErrorCount = 0;
        ConsecutiveErrorCount = 0;
        AverageCaptureTimeMs = 0;
        AverageInferenceTimeMs = 0;
        AverageTotalTimeMs = 0;
        StartTime = null;
        LastUpdateTime = null;
    }

    /// <summary>更新統計 (檢測完成時)</summary>
    public void Update(bool isOk, long captureTimeMs, long inferenceTimeMs, long totalTimeMs)
    {
        if (!StartTime.HasValue)
            StartTime = DateTime.Now;

        TotalCount++;

        if (isOk)
        {
            OkCount++;
            ConsecutiveErrorCount = 0;
        }
        else
        {
            NgCount++;
        }

        // 更新移動平均
        AverageCaptureTimeMs = UpdateAverage(AverageCaptureTimeMs, captureTimeMs, TotalCount);
        AverageInferenceTimeMs = UpdateAverage(AverageInferenceTimeMs, inferenceTimeMs, TotalCount);
        AverageTotalTimeMs = UpdateAverage(AverageTotalTimeMs, totalTimeMs, TotalCount);

        LastUpdateTime = DateTime.Now;
    }

    /// <summary>記錄錯誤</summary>
    public void RecordError()
    {
        ErrorCount++;
        ConsecutiveErrorCount++;
        LastUpdateTime = DateTime.Now;
    }

    /// <summary>重置連續錯誤計數</summary>
    public void ResetConsecutiveErrors()
    {
        ConsecutiveErrorCount = 0;
    }

    private static double UpdateAverage(double currentAverage, double newValue, int count)
    {
        if (count <= 1) return newValue;
        return currentAverage + (newValue - currentAverage) / count;
    }
}
