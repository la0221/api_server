using AIVision.Domain.AutoRun;

namespace AIVision.Application.Services;

/// <summary>
/// Auto Run 服務介面
/// </summary>
public interface IAutoRunService
{
    #region 狀態屬性

    /// <summary>當前狀態</summary>
    AutoRunState State { get; }

    /// <summary>是否正在運行</summary>
    bool IsRunning { get; }

    /// <summary>統計資料</summary>
    AutoRunStatistics Statistics { get; }

    /// <summary>當前設定</summary>
    AutoRunOptions? CurrentOptions { get; }

    #endregion

    #region 事件

    /// <summary>狀態變更事件</summary>
    event EventHandler<AutoRunStateChangedEventArgs>? StateChanged;

    /// <summary>檢測完成事件</summary>
    event EventHandler<InspectionCompletedEventArgs>? InspectionCompleted;

    /// <summary>錯誤發生事件</summary>
    event EventHandler<AutoRunErrorEventArgs>? ErrorOccurred;

    /// <summary>觸發接收事件</summary>
    event EventHandler<TriggerReceivedEventArgs>? TriggerReceived;

    /// <summary>取像完成事件</summary>
    event EventHandler<CaptureCompletedEventArgs>? CaptureCompleted;

    #endregion

    #region 控制方法

    /// <summary>
    /// 啟動 Auto Run
    /// </summary>
    /// <param name="options">設定選項</param>
    /// <param name="ct">取消 Token</param>
    Task StartAsync(AutoRunOptions options, CancellationToken ct = default);

    /// <summary>
    /// 停止 Auto Run
    /// </summary>
    /// <param name="ct">取消 Token</param>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// 暫停 Auto Run
    /// </summary>
    Task PauseAsync();

    /// <summary>
    /// 恢復 Auto Run
    /// </summary>
    Task ResumeAsync();

    /// <summary>
    /// 重置統計資料
    /// </summary>
    void ResetStatistics();

    #endregion
}
