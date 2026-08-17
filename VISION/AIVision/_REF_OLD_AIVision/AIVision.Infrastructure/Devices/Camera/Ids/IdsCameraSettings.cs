using System.Collections.Generic;

namespace AIVision.Infrastructure.Devices.Camera.Ids;

internal sealed class IdsCameraSettings
{
    public List<string>? Preferred { get; set; }

    public double ExposureTimeUs { get; set; } = 15000;

    public string GainSelector { get; set; } = "AnalogAll";

    public double Gain { get; set; } = 2.0;

    public long Height { get; set; } = 1024;

    public double? AcquisitionLineRate { get; set; }

    // Line Scan ROI 參數
    /// <summary>ROI 水平偏移 (pixel)</summary>
    public long OffsetX { get; set; } = 0;

    /// <summary>ROI 垂直偏移 (pixel)</summary>
    public long OffsetY { get; set; } = 0;

    /// <summary>ROI 寬度 (pixel)，0 表示使用感測器最大寬度</summary>
    public long Width { get; set; } = 0;
}
