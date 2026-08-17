namespace AIVision.Application.Models;

/// <summary>
/// Line Scan ROI 設定
/// </summary>
public sealed record LineScanRoiSettings
{
    /// <summary>ROI 起始 X 座標 (pixel)</summary>
    public long OffsetX { get; init; }

    /// <summary>ROI 起始 Y 座標 (pixel) - 決定掃描哪一行</summary>
    public long OffsetY { get; init; }

    /// <summary>ROI 寬度 (pixel)</summary>
    public long Width { get; init; }

    /// <summary>目標圖像高度 (要掃描的行數)</summary>
    public int TargetHeight { get; init; }

    /// <summary>線掃頻率 (Hz)</summary>
    public double LineRate { get; init; }

    /// <summary>曝光時間 (µs) - Line Scan 模式需要重新套用</summary>
    public double? ExposureTimeUs { get; init; }

    /// <summary>增益 - Line Scan 模式需要重新套用</summary>
    public double? Gain { get; init; }

    /// <summary>
    /// 要載入的相機 UserSet 名稱 (UserSet0, UserSet1, Linescan, 或 Default)
    /// </summary>
    public string UserSetName { get; init; } = "Linescan";

    /// <summary>
    /// 驗證設定是否有效
    /// </summary>
    public bool IsValid =>
        OffsetX >= 0 &&
        OffsetY >= 0 &&
        Width > 0 &&
        TargetHeight > 0 &&
        LineRate > 0;

    /// <summary>
    /// 計算單張完整圖像的 byte 大小 (Mono8 格式)
    /// </summary>
    public long GetImageSizeBytes(int bytesPerPixel = 1) =>
        Width * TargetHeight * bytesPerPixel;
}

/// <summary>
/// Line Scan ROI 參數邊界
/// </summary>
public sealed record LineScanRoiBounds
{
    public long OffsetXMin { get; init; }
    public long OffsetXMax { get; init; }
    public long OffsetXStep { get; init; } = 1;

    public long OffsetYMin { get; init; }
    public long OffsetYMax { get; init; }
    public long OffsetYStep { get; init; } = 1;

    public long WidthMin { get; init; }
    public long WidthMax { get; init; }
    public long WidthStep { get; init; } = 1;

    public double LineRateMin { get; init; }
    public double LineRateMax { get; init; }

    /// <summary>感測器最大寬度</summary>
    public long SensorWidth { get; init; }

    /// <summary>感測器最大高度</summary>
    public long SensorHeight { get; init; }

    /// <summary>
    /// 根據邊界修正設定值
    /// </summary>
    public LineScanRoiSettings Clamp(LineScanRoiSettings settings)
    {
        return settings with
        {
            OffsetX = Math.Clamp(settings.OffsetX, OffsetXMin, OffsetXMax),
            OffsetY = Math.Clamp(settings.OffsetY, OffsetYMin, OffsetYMax),
            Width = Math.Clamp(settings.Width, WidthMin, WidthMax),
            LineRate = Math.Clamp(settings.LineRate, LineRateMin, LineRateMax)
        };
    }
}
