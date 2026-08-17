namespace AIVision.Infrastructure.Devices.Plc.Handshake;

/// <summary>
/// PLC 交握設定
/// </summary>
public class PlcHandshakeOptions
{
    public const string SectionName = "Devices:Plc:Handshake";

    /// <summary>觸發訊號名稱</summary>
    public string TriggerSignal { get; set; } = "TriggerCapture";

    /// <summary>取像狀態訊號名稱</summary>
    public string CaptureStatusSignal { get; set; } = "CaptureStatus";

    /// <summary>OK 結果訊號名稱</summary>
    public string OkSignal { get; set; } = "ResultOk";

    /// <summary>NG 結果訊號名稱</summary>
    public string NgSignal { get; set; } = "ResultNg";

    /// <summary>檢測完成訊號名稱</summary>
    public string DoneSignal { get; set; } = "InspectDone";

    /// <summary>取像超時時間 (毫秒) - Line Scan 模式需要較長時間</summary>
    public int CaptureTimeoutMs { get; set; } = 15000;

    /// <summary>推論超時時間 (毫秒)</summary>
    public int InferenceTimeoutMs { get; set; } = 10000;

    /// <summary>最小觸發間隔 (毫秒)</summary>
    public int MinTriggerIntervalMs { get; set; } = 200;

    /// <summary>PC 是否自動清除結果訊號</summary>
    public bool AutoClearByPc { get; set; } = true;

    /// <summary>錯誤處理策略: TreatAsNg, Ignore, BlockAndAlarm</summary>
    public string ErrorPolicy { get; set; } = "TreatAsNg";
}
