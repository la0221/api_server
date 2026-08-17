namespace AIVision.Infrastructure.Devices.Light;

/// <summary>
/// RS232 光源設備配置選項
/// </summary>
public class LightSerialDeviceOptions
{
    public const string SectionName = "Devices:LightSerial";

    /// <summary>串口名稱 (如 COM1, COM3)</summary>
    public string PortName { get; set; } = "COM1";

    /// <summary>串列傳輸速率 (預設 19200)</summary>
    public int BaudRate { get; set; } = 19200;

    /// <summary>光源通道數 (預設 2)</summary>
    public int ChannelCount { get; set; } = 2;

    /// <summary>通訊超時 (預設 1000ms)</summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>輪詢間隔 (預設 500ms)</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>命令間隔 (預設 20ms)</summary>
    public int CommandIntervalMs { get; set; } = 20;

    /// <summary>
    /// Auto Run 光源亮度控制設定
    /// </summary>
    public LightBrightnessControlOptions BrightnessControl { get; set; } = new();
}

/// <summary>
/// 光源亮度自動控制設定（用於 Auto Run）
/// </summary>
public class LightBrightnessControlOptions
{
    /// <summary>
    /// 是否啟用亮度控制（取像時設定工作亮度，待機時設定待機亮度）
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 待機時的亮度值（0-255）
    /// 預設 0，完全關閉光源
    /// </summary>
    public int IdleBrightness { get; set; } = 0;

    /// <summary>
    /// 工作時的亮度值（0-255）
    /// 預設 38（約 15% 的 255）
    /// 當 ChannelBrightnessMap 為空時使用此值
    /// </summary>
    public int WorkingBrightness { get; set; } = 38;

    /// <summary>
    /// 需要控制的通道列表（預設 [1, 2]）
    /// </summary>
    public int[] Channels { get; set; } = [1, 2];

    /// <summary>
    /// 每個通道的獨立工作亮度設定
    /// key: 通道編號, value: 亮度值 (0-255)
    /// 當設定時，會覆蓋 WorkingBrightness 的全域值
    /// </summary>
    public Dictionary<int, int> ChannelBrightnessMap { get; set; } = new();

    /// <summary>
    /// 取得指定通道的工作亮度
    /// 優先使用 ChannelBrightnessMap，若無則使用全域 WorkingBrightness
    /// </summary>
    public int GetChannelBrightness(int channel)
    {
        return ChannelBrightnessMap.TryGetValue(channel, out var brightness)
            ? brightness
            : WorkingBrightness;
    }
}
