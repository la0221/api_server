namespace AIVision.Domain.AutoRun;

/// <summary>
/// Auto Run 流程狀態
/// </summary>
public enum AutoRunState
{
    /// <summary>閒置，等待啟動</summary>
    Idle,

    /// <summary>初始化中 (連接相機、PLC)</summary>
    Initializing,

    /// <summary>等待 PLC 觸發 (10001)</summary>
    WaitingTrigger,

    /// <summary>Line Scan 取像中</summary>
    Capturing,

    /// <summary>AI 推論中</summary>
    Inferring,

    /// <summary>回報 PLC 結果中</summary>
    Reporting,

    /// <summary>暫停</summary>
    Paused,

    /// <summary>停止中</summary>
    Stopping,

    /// <summary>已停止</summary>
    Stopped,

    /// <summary>錯誤狀態</summary>
    Error
}
