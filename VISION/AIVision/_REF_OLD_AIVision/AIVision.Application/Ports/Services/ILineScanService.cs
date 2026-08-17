using System;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Models;
using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.Services;

/// <summary>
/// Line Scan 服務介面
/// </summary>
public interface ILineScanService
{
    /// <summary>相機是否已連接</summary>
    bool IsCameraConnected { get; }

    /// <summary>當前 ROI 設定（可能被 EEPROM 覆蓋）</summary>
    LineScanRoiSettings? CurrentSettings { get; }

    /// <summary>UI 原始設定（不會被 EEPROM 覆蓋，Auto Run 應使用此設定）</summary>
    LineScanRoiSettings? OriginalSettings { get; }

    /// <summary>ROI 參數邊界</summary>
    LineScanRoiBounds? Bounds { get; }

    /// <summary>是否正在掃描</summary>
    bool IsScanning { get; }

    /// <summary>當前已掃描行數</summary>
    int CurrentLineIndex { get; }

    /// <summary>已完成的圖像數量</summary>
    int CompletedImageCount { get; }

    /// <summary>
    /// 連接相機（如果尚未連接）
    /// </summary>
    Task<bool> ConnectCameraAsync(CancellationToken ct = default);

    /// <summary>
    /// 取得 Area Scan 預覽影像 (用於設定 ROI)
    /// </summary>
    Task<ImageData?> CaptureAreaPreviewAsync(CancellationToken ct = default);

    /// <summary>
    /// 更新 ROI 邊界資訊 (從相機讀取)
    /// </summary>
    Task UpdateBoundsAsync(CancellationToken ct = default);

    /// <summary>
    /// 設定 ROI 參數並切換到 Line Scan 模式
    /// </summary>
    Task ConfigureAndStartAsync(LineScanRoiSettings settings, CancellationToken ct = default);

    /// <summary>
    /// 停止掃描並切換回 Area Scan 模式
    /// </summary>
    Task StopAndResetAsync(CancellationToken ct = default);

    /// <summary>
    /// 快速啟動取像（使用已載入的設定）
    /// 適用於 Auto Run：相機設定已在 Line Scan Panel 載入過，不需要重新設定
    /// </summary>
    Task StartCaptureAsync(CancellationToken ct = default);

    /// <summary>
    /// 快速停止取像
    /// </summary>
    Task StopCaptureAsync(CancellationToken ct = default);

    /// <summary>
    /// 執行單次完整取像 (等待累積完成後返回)
    /// 用於 Auto Run 模式，等待當前累積完成後返回影像
    /// 必須先呼叫 ConfigureAndStartAsync 啟動掃描
    /// </summary>
    /// <param name="ct">取消 Token</param>
    /// <returns>完成的影像資料</returns>
    Task<ImageData> CaptureOnceAsync(CancellationToken ct = default);

    /// <summary>
    /// 重置 ImageBuilder，準備下一次取像
    /// 在 Auto Run 模式中，每次取像前呼叫以清除前一次的資料
    /// </summary>
    void ResetForNextCapture();

    #region 被動等待模式 (用於 PLC 觸發場景)

    /// <summary>
    /// 最近一張完成的影像（可能為 null）
    /// </summary>
    ImageData? LatestImage { get; }

    /// <summary>
    /// 最近影像的完成時間
    /// </summary>
    DateTime LatestImageTime { get; }

    /// <summary>
    /// 取得最近的影像（如果在指定時間內有效）
    /// </summary>
    /// <param name="maxAgeMs">影像有效期（毫秒），預設 5000ms</param>
    /// <returns>影像資料，若無有效影像則回傳 null</returns>
    ImageData? GetLatestImageIfFresh(int maxAgeMs = 5000);

    /// <summary>
    /// 等待下一張影像完成（被動等待模式）
    /// 不主動觸發取像，而是等待 Encoder 驅動相機產生的下一張影像
    /// </summary>
    /// <param name="ct">取消 Token</param>
    /// <returns>完成的影像資料</returns>
    Task<ImageData> WaitForNextImageAsync(CancellationToken ct = default);

    /// <summary>
    /// 清除暫存的最近影像
    /// </summary>
    void ClearLatestImage();

    #endregion

    /// <summary>
    /// 收到一行數據時觸發
    /// </summary>
    event EventHandler<LineScanLineEventArgs>? LineReceived;

    /// <summary>
    /// 完成一張完整圖像時觸發
    /// </summary>
    event EventHandler<LineScanImageEventArgs>? ImageCompleted;

    /// <summary>
    /// 掃描錯誤時觸發
    /// </summary>
    event EventHandler<LineScanErrorEventArgs>? ScanError;
}

/// <summary>
/// 行接收事件參數
/// </summary>
public sealed class LineScanLineEventArgs : EventArgs
{
    /// <summary>當前行索引 (0-based)</summary>
    public int LineIndex { get; init; }

    /// <summary>目標總行數</summary>
    public int TotalLines { get; init; }

    /// <summary>進度百分比 (0-100)</summary>
    public double ProgressPercent => TotalLines > 0 ? (double)LineIndex / TotalLines * 100 : 0;
}

/// <summary>
/// 圖像完成事件參數
/// </summary>
public sealed class LineScanImageEventArgs : EventArgs
{
    /// <summary>完成的圖像</summary>
    public required ImageData Image { get; init; }

    /// <summary>圖像索引 (從 0 開始)</summary>
    public int ImageIndex { get; init; }

    /// <summary>掃描耗時</summary>
    public TimeSpan ElapsedTime { get; init; }

    /// <summary>實際行頻 (Hz)</summary>
    public double ActualLineRate { get; init; }
}

/// <summary>
/// 掃描錯誤事件參數
/// </summary>
public sealed class LineScanErrorEventArgs : EventArgs
{
    /// <summary>例外</summary>
    public required Exception Exception { get; init; }

    /// <summary>錯誤訊息</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>是否為致命錯誤 (需要停止掃描)</summary>
    public bool IsFatal { get; init; }
}
