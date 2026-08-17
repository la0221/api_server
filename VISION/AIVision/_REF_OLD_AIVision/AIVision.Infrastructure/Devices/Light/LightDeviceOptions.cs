namespace AIVision.Infrastructure.Devices.Light;

/// <summary>
/// 光源設備配置選項
/// </summary>
public class LightDeviceOptions
{
    public const string SectionName = "Devices:Light";

    /// <summary>監聽 IP (預設 0.0.0.0)</summary>
    public string ListenIp { get; set; } = "0.0.0.0";

    /// <summary>監聽埠號 (預設 8000)</summary>
    public int ListenPort { get; set; } = 8000;

    /// <summary>光源通道數 (預設 2，可空以支援 ViewModel 的 ?? 運算子)</summary>
    public int? ChannelCount { get; set; } = 2;

    /// <summary>通訊超時 (預設 1000ms)</summary>
    public int? TimeoutMs { get; set; } = 1000;

    /// <summary>輪詢間隔 (預設 500ms)</summary>
    public int? PollIntervalMs { get; set; } = 500;

    // 以下為設備端設定（可選）
    public string? DeviceIp { get; set; }
    public int? DevicePort { get; set; }
    public string? GatewayIp { get; set; }
    public string? ServerIp { get; set; }
    public int? ServerPort { get; set; }
    public byte? UnitId { get; set; }
}
