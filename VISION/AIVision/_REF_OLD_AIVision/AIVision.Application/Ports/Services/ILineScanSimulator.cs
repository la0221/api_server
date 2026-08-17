using System;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Models;
using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.Services;

/// <summary>
/// Line Scan 模擬器介面
/// </summary>
public interface ILineScanSimulator
{
    /// <summary>模擬器狀態</summary>
    SimulatorState State { get; }

    /// <summary>當前已掃描行數</summary>
    int CurrentLine { get; }

    /// <summary>目標總行數</summary>
    int TotalLines { get; }

    /// <summary>已完成的圖像數</summary>
    int CompletedImageCount { get; }

    /// <summary>來源圖片資訊（寬、高）</summary>
    (int Width, int Height)? SourceImageSize { get; }

    /// <summary>
    /// 設定來源圖片數據（灰階 Mono8 格式）
    /// </summary>
    /// <param name="imageData">圖片數據</param>
    void SetSourceImage(ImageData imageData);

    /// <summary>
    /// 取得來源圖片預覽（用於 ROI 設定）
    /// </summary>
    /// <returns>來源圖片資料，若尚未載入則回傳 null</returns>
    ImageData? GetSourcePreview();

    /// <summary>
    /// 開始模擬掃描
    /// </summary>
    /// <param name="settings">模擬器設定</param>
    /// <param name="ct">取消權杖</param>
    Task StartAsync(LineScanSimulatorSettings settings, CancellationToken ct = default);

    /// <summary>
    /// 停止模擬
    /// </summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// 暫停模擬
    /// </summary>
    void Pause();

    /// <summary>
    /// 繼續模擬
    /// </summary>
    void Resume();

    /// <summary>
    /// 行掃描事件
    /// </summary>
    event EventHandler<LineScanLineEventArgs>? LineReceived;

    /// <summary>
    /// 圖像完成事件
    /// </summary>
    event EventHandler<LineScanImageEventArgs>? ImageCompleted;

    /// <summary>
    /// 錯誤事件
    /// </summary>
    event EventHandler<LineScanErrorEventArgs>? SimulatorError;
}
