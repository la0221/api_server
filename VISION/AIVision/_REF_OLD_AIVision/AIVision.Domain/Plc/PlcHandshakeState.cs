namespace AIVision.Domain.Plc;

/// <summary>
/// PLC 交握狀態
/// </summary>
public enum PlcHandshakeState
{
    /// <summary>未連線</summary>
    Disconnected,

    /// <summary>等待 PLC 觸發</summary>
    WaitForTrigger,

    /// <summary>開始取像</summary>
    StartCapture,

    /// <summary>執行 AI 推論中</summary>
    RunningInspect,

    /// <summary>送出結果</summary>
    SendResult,

    /// <summary>等待 PLC 重置觸發訊號</summary>
    WaitForPlcReset,

    /// <summary>錯誤狀態</summary>
    Error
}

/// <summary>
/// 檢測結果
/// </summary>
public enum PlcInspectionResult
{
    /// <summary>檢測通過</summary>
    Ok,

    /// <summary>檢測不通過</summary>
    Ng
}
