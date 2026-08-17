namespace AIVision.Infrastructure.Devices.Plc.Communication;

/// <summary>
/// PLC Modbus TCP 連線設定
/// </summary>
public class PlcConnectionOptions
{
    public const string SectionName = "Devices:Plc:Connection";

    /// <summary>PLC IP 位址</summary>
    public string Ip { get; set; } = "127.0.0.1";

    /// <summary>Modbus TCP Port (預設 502)</summary>
    public int Port { get; set; } = 502;

    /// <summary>Modbus Unit ID / Slave ID</summary>
    public byte UnitId { get; set; } = 1;

    /// <summary>輪詢間隔 (毫秒)</summary>
    public int PollIntervalMs { get; set; } = 50;

    /// <summary>重連間隔 (毫秒)</summary>
    public int ReconnectIntervalMs { get; set; } = 5000;

    /// <summary>讀取逾時 (毫秒)。設為 3000 以應對網路不穩定情況</summary>
    public int ReadTimeoutMs { get; set; } = 3000;

    /// <summary>寫入逾時 (毫秒)。設為 3000 以應對網路不穩定情況</summary>
    public int WriteTimeoutMs { get; set; } = 3000;

    /// <summary>心跳檢測間隔 (毫秒)，0 表示停用。縮短到 3 秒避免 TCP 連線假死</summary>
    public int HeartbeatIntervalMs { get; set; } = 3000;

    /// <summary>斷線後首次重連等待時間 (毫秒)</summary>
    public int InitialReconnectDelayMs { get; set; } = 2000;

    /// <summary>重連最大等待時間 (毫秒)</summary>
    public int MaxReconnectDelayMs { get; set; } = 10000;

    /// <summary>連續通訊錯誤達此次數後觸發重連</summary>
    public int ErrorThresholdForReconnect { get; set; } = 3;
}
