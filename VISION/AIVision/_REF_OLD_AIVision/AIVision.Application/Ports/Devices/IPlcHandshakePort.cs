using AIVision.Domain.Plc;

namespace AIVision.Application.Ports.Devices;

/// <summary>
/// PLC 交握服務介面
/// </summary>
public interface IPlcHandshakePort
{
    /// <summary>當前狀態</summary>
    PlcHandshakeState CurrentState { get; }

    /// <summary>是否正在運行</summary>
    bool IsRunning { get; }

    /// <summary>狀態變更事件</summary>
    event EventHandler<PlcHandshakeStateChangedEventArgs>? StateChanged;

    /// <summary>收到觸發請求事件</summary>
    event EventHandler<PlcTriggerReceivedEventArgs>? TriggerReceived;

    /// <summary>請求取像事件 (當 PLC 觸發後，通知外部進行取像)</summary>
    event EventHandler<PlcCaptureRequestedEventArgs>? CaptureRequested;

    /// <summary>啟動交握服務</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>停止交握服務</summary>
    Task StopAsync();

    /// <summary>回報檢測結果</summary>
    Task ReportResultAsync(PlcInspectionResult result, CancellationToken cancellationToken = default);

    /// <summary>通知取像完成 (外部呼叫，用於設定 CaptureStatus 訊號)</summary>
    Task NotifyCaptureCompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>強制重置狀態機</summary>
    Task ResetAsync();
}

/// <summary>
/// 狀態變更事件參數
/// </summary>
public class PlcHandshakeStateChangedEventArgs : EventArgs
{
    public PlcHandshakeState OldState { get; }
    public PlcHandshakeState NewState { get; }
    public DateTime Timestamp { get; }

    public PlcHandshakeStateChangedEventArgs(PlcHandshakeState oldState, PlcHandshakeState newState)
    {
        OldState = oldState;
        NewState = newState;
        Timestamp = DateTime.Now;
    }
}

/// <summary>
/// 觸發接收事件參數
/// </summary>
public class PlcTriggerReceivedEventArgs : EventArgs
{
    public DateTime Timestamp { get; }
    public int TriggerCount { get; }

    public PlcTriggerReceivedEventArgs(int triggerCount)
    {
        Timestamp = DateTime.Now;
        TriggerCount = triggerCount;
    }
}

/// <summary>
/// 取像請求事件參數
/// </summary>
public class PlcCaptureRequestedEventArgs : EventArgs
{
    public DateTime Timestamp { get; }
    public int TriggerCount { get; }

    public PlcCaptureRequestedEventArgs(int triggerCount)
    {
        Timestamp = DateTime.Now;
        TriggerCount = triggerCount;
    }
}
