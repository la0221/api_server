using System.Collections.Generic;

namespace AIVision.Application.Configuration;

public sealed class HikCameraOptions
{
    public const string SectionName = "Devices:Camera";

    /// <summary>
    /// 相機驅動類型，目前僅支援 Hik。
    /// </summary>
    public string Type { get; set; } = "Hik";

    /// <summary>
    /// 優先開啟的裝置序號或 IP（依序嘗試）。
    /// </summary>
    public List<string> Preferred { get; set; } = new();

    public HikGigEOptions GigE { get; set; } = new();

    public HikCameraFeatureOptions Options { get; set; } = new();
}

public sealed class HikGigEOptions
{
    /// <summary>
    /// 建議的 GigE 封包大小（0 代表維持預設）。
    /// </summary>
    public int PacketSize { get; set; } = 1500;

    /// <summary>
    /// 使用的串流通道索引。
    /// </summary>
    public int StreamChannel { get; set; } = 0;
}

public sealed class HikCameraFeatureOptions
{
    /// <summary>
    /// 觸發模式：Off / On。
    /// </summary>
    public string TriggerMode { get; set; } = "Off";

    /// <summary>
    /// 曝光時間（微秒）。
    /// </summary>
    public double ExposureTimeUs { get; set; } = 8000;

    /// <summary>
    /// 額外增益。
    /// </summary>
    public double Gain { get; set; } = 8.0;

    /// <summary>
    /// 目標像素格式，例如 PixelFormat_BGR8。
    /// </summary>
    public string PixelFormat { get; set; } = "PixelFormat_BGR8";
}
