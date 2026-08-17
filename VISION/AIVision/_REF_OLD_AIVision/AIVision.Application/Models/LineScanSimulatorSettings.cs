namespace AIVision.Application.Models;

/// <summary>
/// Line Scan 模擬器設定
/// </summary>
public sealed class LineScanSimulatorSettings
{
    /// <summary>ROI 起始 X 座標 (Left-Edge)</summary>
    public int AnchorX { get; init; }

    /// <summary>固定掃描線 Y 座標</summary>
    public int AnchorY { get; init; }

    /// <summary>掃描寬度</summary>
    public int ScanWidth { get; init; }

    /// <summary>掃描行數（輸出圖像高度）</summary>
    public int ScanHeight { get; init; }

    /// <summary>模擬行頻 (Hz)</summary>
    public double LineRate { get; init; } = 1000;

    /// <summary>是否啟用時序模擬</summary>
    public bool EnableTiming { get; init; } = true;

    /// <summary>來源圖片路徑</summary>
    public string SourceImagePath { get; init; } = string.Empty;

    /// <summary>驗證設定是否有效</summary>
    public bool IsValid =>
        AnchorX >= 0 &&
        AnchorY >= 0 &&
        ScanWidth > 0 &&
        ScanHeight > 0 &&
        LineRate > 0 &&
        !string.IsNullOrEmpty(SourceImagePath);

    /// <summary>
    /// 驗證 ROI 是否在圖片範圍內
    /// </summary>
    public bool IsRoiInBounds(int imageWidth, int imageHeight) =>
        AnchorX >= 0 &&
        AnchorY >= 0 &&
        AnchorX + ScanWidth <= imageWidth &&
        AnchorY < imageHeight;
}

/// <summary>
/// 模擬器執行狀態
/// </summary>
public enum SimulatorState
{
    /// <summary>閒置</summary>
    Idle,

    /// <summary>執行中</summary>
    Running,

    /// <summary>暫停</summary>
    Paused,

    /// <summary>完成</summary>
    Completed,

    /// <summary>錯誤</summary>
    Error
}
