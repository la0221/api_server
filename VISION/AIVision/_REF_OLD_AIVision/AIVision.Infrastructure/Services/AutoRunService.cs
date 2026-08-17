using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIVision.Application.Models;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.Services;
using AIVision.Application.Services;
using AIVision.Domain.AutoRun;
using AIVision.Domain.Entities;
using AIVision.Domain.Plc;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Services;

/// <summary>
/// Auto Run 服務實作
/// </summary>
public class AutoRunService : IAutoRunService, IDisposable
{
    private readonly IPlcHandshakePort _plcHandshake;
    private readonly ILineScanService _lineScan;
    private readonly IAiInferencePort _aiInference;
    private readonly IInspectionImageService _imageService;
    private readonly IContourOverlayRenderer? _overlayRenderer;
    private readonly ILightPort? _lightPort;  // 光源控制（可選）
    private readonly IDefectFilteringService? _defectFilteringService;  // 瑕疵過濾服務（可選）
    private readonly ILogger<AutoRunService> _logger;

    private AutoRunState _state = AutoRunState.Idle;
    private CancellationTokenSource? _cts;
    private AutoRunOptions? _options;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _pauseSemaphore = new(1, 1);
    private bool _isPaused;
    private bool _disposed;

    // 保存初始化時的 Line Scan 設定，避免被 EEPROM 覆蓋
    private LineScanRoiSettings? _initialLineScanSettings;

    // 空閒檢測相關
    private DateTime _lastActivityTime = DateTime.Now;
    private const int IdleWarningSeconds = 60; // 空閒超過 60 秒發出警告
    private const int IdleCheckIntervalMs = 5000; // 每 5 秒檢查一次

    public AutoRunState State
    {
        get { lock (_stateLock) return _state; }
        private set
        {
            AutoRunState oldState;
            lock (_stateLock)
            {
                if (_state == value) return;
                oldState = _state;
                _state = value;
            }
            _logger.LogInformation("Auto Run 狀態變更: {OldState} → {NewState}", oldState, value);
            OnStateChanged(oldState, value);
        }
    }

    public bool IsRunning => State != AutoRunState.Idle &&
                             State != AutoRunState.Stopped &&
                             State != AutoRunState.Error;

    public AutoRunStatistics Statistics { get; } = new();
    public AutoRunOptions? CurrentOptions => _options;

    #region Events

    public event EventHandler<AutoRunStateChangedEventArgs>? StateChanged;
    public event EventHandler<InspectionCompletedEventArgs>? InspectionCompleted;
    public event EventHandler<AutoRunErrorEventArgs>? ErrorOccurred;
    public event EventHandler<TriggerReceivedEventArgs>? TriggerReceived;
    public event EventHandler<CaptureCompletedEventArgs>? CaptureCompleted;

    #endregion

    public AutoRunService(
        IPlcHandshakePort plcHandshake,
        ILineScanService lineScan,
        IAiInferencePort aiInference,
        IInspectionImageService imageService,
        ILogger<AutoRunService> logger,
        IContourOverlayRenderer? overlayRenderer = null,
        ILightPort? lightPort = null,
        IDefectFilteringService? defectFilteringService = null)
    {
        _plcHandshake = plcHandshake ?? throw new ArgumentNullException(nameof(plcHandshake));
        _lineScan = lineScan ?? throw new ArgumentNullException(nameof(lineScan));
        _aiInference = aiInference ?? throw new ArgumentNullException(nameof(aiInference));
        _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _overlayRenderer = overlayRenderer; // 可選，若無則不繪製 Overlay
        _lightPort = lightPort; // 可選，若無則不控制光源亮度
        _defectFilteringService = defectFilteringService; // 可選，若無則不進行瑕疵過濾
    }

    public async Task StartAsync(AutoRunOptions options, CancellationToken ct = default)
    {
        if (IsRunning)
        {
            _logger.LogWarning("Auto Run 已在運行中，忽略啟動請求");
            return;
        }

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isPaused = false;
        Statistics.Reset();

        _logger.LogInformation("===== Auto Run 啟動 =====");
        _logger.LogInformation("相機模式: {Mode}", options.CameraMode);

        try
        {
            await RunLoopAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Auto Run 被取消");
            State = AutoRunState.Stopped;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto Run 發生錯誤");
            State = AutoRunState.Error;
            OnError(AutoRunErrorType.Unknown, "Auto Run 發生未預期錯誤", ex, false);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!IsRunning)
        {
            _logger.LogDebug("Auto Run 未在運行中，忽略停止請求");
            return;
        }

        _logger.LogInformation("===== Auto Run 停止中 =====");
        State = AutoRunState.Stopping;

        try
        {
            _cts?.Cancel();

            // 釋放暫停
            if (_isPaused)
            {
                _isPaused = false;
                _pauseSemaphore.Release();
            }

            // 停止 PLC 握手
            await _plcHandshake.StopAsync();

            // 停止 Line Scan
            if (_lineScan.IsScanning)
            {
                await _lineScan.StopAndResetAsync(ct);
            }

            // 停止時恢復待機亮度（0%）
            if (_lightPort != null)
            {
                try
                {
                    await _lightPort.SetIdleBrightnessAsync(CancellationToken.None);
                    _logger.LogInformation("光源已恢復待機亮度");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "停止時恢復光源待機亮度失敗");
                }
            }

            State = AutoRunState.Stopped;
            _logger.LogInformation("===== Auto Run 已停止 =====");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止 Auto Run 時發生錯誤");
            State = AutoRunState.Error;
        }
    }

    public Task PauseAsync()
    {
        if (!IsRunning || _isPaused)
            return Task.CompletedTask;

        _logger.LogInformation("Auto Run 暫停");
        _isPaused = true;
        _pauseSemaphore.Wait();
        State = AutoRunState.Paused;
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        if (!_isPaused)
            return Task.CompletedTask;

        _logger.LogInformation("Auto Run 恢復");
        _isPaused = false;
        _pauseSemaphore.Release();
        State = AutoRunState.WaitingTrigger;
        return Task.CompletedTask;
    }

    public void ResetStatistics()
    {
        Statistics.Reset();
        _logger.LogInformation("統計資料已重置");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // 1. 初始化
        State = AutoRunState.Initializing;
        await InitializeAsync(ct);

        // 2. 訂閱事件
        SubscribeEvents();

        try
        {
            // 3. 啟動 PLC 握手服務
            await _plcHandshake.StartAsync(ct);

            // 4. 主循環 - 由 PLC 事件驅動，帶空閒檢測
            State = AutoRunState.WaitingTrigger;
            _logger.LogInformation("進入等待觸發狀態，等待 PLC 10001 信號...");
            _lastActivityTime = DateTime.Now;

            // 主循環：定期檢查空閒狀態，而不是無限等待
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(IdleCheckIntervalMs, ct);

                // 檢查是否空閒過久
                if (State == AutoRunState.WaitingTrigger)
                {
                    var idleSeconds = (DateTime.Now - _lastActivityTime).TotalSeconds;
                    if (idleSeconds >= IdleWarningSeconds)
                    {
                        _logger.LogDebug("Auto Run 空閒中 ({Seconds:F0} 秒)，等待 PLC 觸發...", idleSeconds);
                    }
                }

                // 檢查 PLC 連線狀態
                if (!_plcHandshake.IsRunning)
                {
                    _logger.LogWarning("PLC 握手服務已停止，嘗試重新啟動...");
                    try
                    {
                        await _plcHandshake.StartAsync(ct);
                        _logger.LogInformation("PLC 握手服務已重新啟動");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "PLC 握手服務重新啟動失敗");
                    }
                }
            }
        }
        finally
        {
            UnsubscribeEvents();
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        _logger.LogInformation("初始化 Auto Run...");

        // 驗證相機狀態（不連接，因為 Line Scan Panel 應該已經設定好）
        if (!_lineScan.IsCameraConnected)
        {
            throw new InvalidOperationException("相機未連接，請先在 Line Scan Panel 連接相機");
        }

        // 驗證相機已設定（Line Scan Panel 應該已經載入 UserSet 並測試過）
        if (_lineScan.OriginalSettings == null)
        {
            throw new InvalidOperationException("相機尚未設定，請先在 Line Scan Panel 載入設定並測試取像");
        }

        // 使用 OriginalSettings（UI 原始設定，不會被 EEPROM 覆蓋）
        _initialLineScanSettings = _lineScan.OriginalSettings;

        _logger.LogInformation("相機設定已記錄: {Width}x{Height}, LineRate={LineRate}Hz",
            _initialLineScanSettings.Width,
            _initialLineScanSettings.TargetHeight,
            _initialLineScanSettings.LineRate);

        // ===== 相機熱機流程 =====
        // 在 Auto Run 啟動前先預熱相機，避免第一輪取像時相機還在初始化
        // 這模擬手動操作時「先拍一張測試照」的行為
        await WarmupCameraAsync(ct);

        _logger.LogInformation("初始化完成，相機已熱機，等待 PLC 觸發");
    }

    /// <summary>
    /// 相機熱機流程
    /// 啟動相機並短暫取像（不等待完整影像），讓相機進入工作狀態
    /// </summary>
    private async Task WarmupCameraAsync(CancellationToken ct)
    {
        if (_initialLineScanSettings == null)
        {
            _logger.LogWarning("無 Line Scan 設定，跳過相機熱機");
            return;
        }

        _logger.LogInformation("===== 相機熱機開始 =====");

        try
        {
            // 使用與正式取像相同的設定啟動相機
            var lineRateOverride = _options?.LineScanSettings?.LineRate > 0
                ? _options.LineScanSettings.LineRate
                : 0;

            var settings = _initialLineScanSettings with
            {
                LineRate = lineRateOverride
            };

            _logger.LogInformation("熱機設定: {UserSet}, {Width}x{Height}",
                settings.UserSetName, settings.Width, settings.TargetHeight);

            // 如果正在掃描，先停止
            if (_lineScan.IsScanning)
            {
                await _lineScan.StopCaptureAsync(ct);
            }

            // 啟動相機
            await _lineScan.ConfigureAndStartAsync(settings, ct);
            _logger.LogInformation("✓ 相機已啟動，進行熱機中...");

            // 等待一小段時間讓相機穩定（不需要等待完整影像）
            // 這個延遲讓相機完成內部初始化
            await Task.Delay(500, ct);

            // 停止相機，但現在它已經「熱」了
            await _lineScan.StopCaptureAsync(ct);

            _logger.LogInformation("===== 相機熱機完成 =====");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("相機熱機被取消");
            throw;
        }
        catch (Exception ex)
        {
            // 熱機失敗不是致命錯誤，記錄警告但繼續
            _logger.LogWarning(ex, "相機熱機失敗，第一輪取像可能會有延遲");

            // 確保相機停止
            if (_lineScan.IsScanning)
            {
                try { await _lineScan.StopCaptureAsync(CancellationToken.None); }
                catch { /* 忽略 */ }
            }
        }
    }

    private void SubscribeEvents()
    {
        _plcHandshake.CaptureRequested += OnPlcCaptureRequested;
        _plcHandshake.TriggerReceived += OnPlcTriggerReceived;
        _lineScan.ImageCompleted += OnLineScanImageCompleted;
        _lineScan.ScanError += OnLineScanError;
    }

    private void UnsubscribeEvents()
    {
        _plcHandshake.CaptureRequested -= OnPlcCaptureRequested;
        _plcHandshake.TriggerReceived -= OnPlcTriggerReceived;
        _lineScan.ImageCompleted -= OnLineScanImageCompleted;
        _lineScan.ScanError -= OnLineScanError;
    }

    private void OnPlcTriggerReceived(object? sender, PlcTriggerReceivedEventArgs e)
    {
        _logger.LogDebug("收到 PLC 觸發 #{Count}", e.TriggerCount);
        _lastActivityTime = DateTime.Now; // 更新活動時間
        TriggerReceived?.Invoke(this, new TriggerReceivedEventArgs(e.TriggerCount));
    }

    private async void OnPlcCaptureRequested(object? sender, PlcCaptureRequestedEventArgs e)
    {
        if (_cts?.IsCancellationRequested ?? true)
            return;

        // 檢查暫停
        await _pauseSemaphore.WaitAsync();
        _pauseSemaphore.Release();

        try
        {
            var totalStopwatch = Stopwatch.StartNew();

            _logger.LogInformation("===== 檢測循環 #{Index} 開始 =====", Statistics.TotalCount + 1);

            // ======================================================================
            // ⚡ 關鍵優化：光源調整並行執行，不阻塞 PLC 訊號
            // ======================================================================

            // 0. 調高光源亮度到工作亮度（15%）（非阻塞，Fire-and-Forget）
            if (_lightPort != null)
            {
                // 並行執行，不等待完成
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _lightPort.SetWorkingBrightnessAsync(_cts!.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "調整光源到工作亮度失敗（不影響取像）");
                    }
                });
            }

            // 1. 取像
            State = AutoRunState.Capturing;
            var captureStopwatch = Stopwatch.StartNew();

            ImageData image;
            if (_options?.CameraMode == CameraMode.LineScan)
            {
                image = await CaptureLineScanImageAsync(_cts!.Token);
            }
            else
            {
                // Area Scan 模式 (待實作)
                throw new NotSupportedException("Area Scan 模式尚未在 AutoRunService 中實作");
            }

            captureStopwatch.Stop();
            var captureTimeMs = captureStopwatch.ElapsedMilliseconds;
            _logger.LogInformation("取像完成，耗時 {Ms}ms，尺寸 {W}x{H}",
                captureTimeMs, image.Width, image.Height);

            CaptureCompleted?.Invoke(this, new CaptureCompletedEventArgs(image, captureTimeMs));

            // 通知 PLC 取像完成
            await _plcHandshake.NotifyCaptureCompleteAsync(_cts.Token);

            // 2. 推論（或模擬結果）
            Prediction prediction;
            List<Defect>? defectsForStats = null;  // 用於統計的瑕疵列表
            long inferenceTimeMs = 0;

            if (_options?.SkipInference == true)
            {
                // 訓練蒐圖模式：跳過推論，根據設定模擬結果
                var simulatedResult = _options.SkipInferenceResult?.ToUpperInvariant() switch
                {
                    "NG" => false,
                    "RANDOM" => Random.Shared.Next(2) == 0,  // 50% OK, 50% NG
                    _ => true  // 預設 OK
                };
                // 使用「良品」「不良品」作為 Label，讓缺陷統計能正確顯示
                var label = simulatedResult ? "良品" : "不良品";

                // 建立模擬瑕疵（用於統計）
                defectsForStats = new List<Defect>
                {
                    new Defect(
                        type: label,  // "良品" 或 "不良品"
                        confidence: 1.0f,
                        boundingBox: null,
                        severity: null)
                };

                _logger.LogInformation("訓練蒐圖模式：跳過 AI 推論，模擬結果={Result} (設定={Setting})",
                    label, _options.SkipInferenceResult ?? "Ok");

                // 模擬推論 API 的處理時間，給 PLC 足夠時間處理
                _logger.LogDebug("SkipInference 模式：等待 700ms 模擬推論時間...");
                await Task.Delay(700, _cts!.Token);

                prediction = new Prediction(
                    label: label,
                    confidence: 1.0f,
                    isOk: simulatedResult,
                    modelVersion: "N/A",
                    imagePath: null);  // 稍後填入
            }
            else
            {
                // 正常模式：執行推論
                State = AutoRunState.Inferring;
                var inferenceStopwatch = Stopwatch.StartNew();

                prediction = await InferWithTimeoutAsync(image, _cts!.Token);

                inferenceStopwatch.Stop();
                inferenceTimeMs = inferenceStopwatch.ElapsedMilliseconds;
                _logger.LogInformation("推論完成，耗時 {Ms}ms，結果: {Label} ({Confidence:P1})",
                    inferenceTimeMs, prediction.Label, prediction.Confidence);

                // 從推論結果建立瑕疵統計列表
                // 主要瑕疵 = prediction.Label（最大面積的瑕疵類別）
                defectsForStats = new List<Defect>
                {
                    new Defect(
                        type: prediction.Label,
                        confidence: prediction.Confidence,
                        boundingBox: null,
                        severity: null)
                };

                // 若有 WorkflowDefects，記錄詳細瑕疵資訊
                if (prediction.WorkflowDefects != null && prediction.WorkflowDefects.Count > 0)
                {
                    _logger.LogDebug("Workflow 瑕疵詳細: {Count} 個瑕疵", prediction.WorkflowDefects.Count);
                }
            }

            // 2.5 【新增】瑕疵過濾處理
            DefectFilteringResult? filterResult = null;
            IReadOnlyList<WorkflowDefect> defectsForOverlay = prediction.WorkflowDefects ?? Array.Empty<WorkflowDefect>();
            bool filteredIsOk = prediction.IsOk;

            if (_defectFilteringService != null && prediction.WorkflowDefects != null && prediction.WorkflowDefects.Count > 0)
            {
                filterResult = _defectFilteringService.FilterDefects(prediction.WorkflowDefects);
                filteredIsOk = filterResult.IsOk;
                defectsForOverlay = filterResult.DefectsForOverlay;

                // 更新 defectsForStats，只計入有效瑕疵
                if (filterResult.ValidDefects.Count > 0)
                {
                    var primaryDefect = filterResult.ValidDefects.First();
                    defectsForStats = new List<Defect>
                    {
                        new Defect(
                            type: primaryDefect.ClassName,
                            confidence: prediction.Confidence,
                            boundingBox: null,
                            severity: null)
                    };
                }
                else if (!filterResult.IsOk)
                {
                    // NG 但沒有 ValidDefects（理論上不應發生）
                    defectsForStats = new List<Defect>
                    {
                        new Defect(
                            type: "過濾後NG",
                            confidence: prediction.Confidence,
                            boundingBox: null,
                            severity: null)
                    };
                }
                else
                {
                    // OK：無有效瑕疵，不建立瑕疵記錄
                    // 避免在資料庫中建立 type="OK" 的假瑕疵，導致 IsNg 判斷錯誤
                    defectsForStats = null;
                }

                _logger.LogInformation("瑕疵過濾結果: {Result}, 有效瑕疵: {Valid}, 過濾瑕疵: {Filtered}",
                    filteredIsOk ? "OK" : "NG", filterResult.ValidDefects.Count, filterResult.FilteredDefects.Count);
            }

            // 3. 繪製瑕疵輪廓 Overlay（若有 WorkflowDefects）或顯示原圖
            ImageData? annotatedImage = null;
            BitmapSource? annotatedBitmap = null;

            if (_overlayRenderer != null && prediction.WorkflowDefects != null)
            {
                if (defectsForOverlay.Count > 0)
                {
                    // 有瑕疵：繪製 Overlay（使用過濾後的瑕疵列表）
                    try
                    {
                        _logger.LogDebug("開始繪製瑕疵輪廓 Overlay，瑕疵數: {Count}", defectsForOverlay.Count);
                        annotatedImage = _overlayRenderer.DrawDefectContours(image, defectsForOverlay);
                        _logger.LogDebug("瑕疵輪廓 Overlay 繪製完成");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "繪製瑕疵輪廓失敗，將使用原始圖片");
                        annotatedImage = image;  // Fallback 到原圖
                    }
                }
                else
                {
                    // 無瑕疵 (OK 結果)：顯示原圖
                    _logger.LogDebug("檢測結果為 OK (無瑕疵)，將顯示原圖");
                    annotatedImage = image;
                }

                // 統一轉換為 BitmapSource（無論有無瑕疵）
                if (annotatedImage.HasValue)
                {
                    try
                    {
                        // 內聯轉換，避免依賴 Presentation 層
                        var imgData = annotatedImage.Value;
                        PixelFormat pixelFormat = imgData.PixelFormat switch
                        {
                            "Bgr24" => PixelFormats.Bgr24,
                            "Mono8" or "Gray8" => PixelFormats.Gray8,
                            "Bgra32" => PixelFormats.Bgra32,
                            _ => PixelFormats.Bgr24
                        };

                        var bytesPerPixel = (pixelFormat.BitsPerPixel + 7) / 8;
                        int stride = imgData.Stride > 0
                            ? imgData.Stride
                            : imgData.Width * bytesPerPixel;

                        // WPF 要求 stride 必須對齊到 4 bytes
                        stride = ((stride + 3) / 4) * 4;

                        // 驗證緩衝區大小
                        int requiredSize = stride * imgData.Height;
                        byte[] pixelData = imgData.Bytes;

                        if (imgData.Bytes.Length < requiredSize)
                        {
                            _logger.LogWarning("緩衝區大小不足: 需要 {Required} bytes, 實際 {Actual} bytes，正在重新分配",
                                requiredSize, imgData.Bytes.Length);

                            // 重新分配緩衝區並填充
                            pixelData = new byte[requiredSize];
                            int srcStride = imgData.Width * bytesPerPixel;

                            for (int y = 0; y < imgData.Height; y++)
                            {
                                int srcOffset = y * srcStride;
                                int dstOffset = y * stride;
                                int copyLength = Math.Min(srcStride, imgData.Bytes.Length - srcOffset);

                                if (copyLength > 0 && srcOffset + copyLength <= imgData.Bytes.Length)
                                {
                                    Array.Copy(imgData.Bytes, srcOffset, pixelData, dstOffset, copyLength);
                                }
                            }

                            _logger.LogDebug("緩衝區重新分配完成: {Size} bytes", requiredSize);
                        }

                        annotatedBitmap = BitmapSource.Create(
                            imgData.Width,
                            imgData.Height,
                            96, 96,
                            pixelFormat,
                            null,
                            pixelData,
                            stride);
                        annotatedBitmap.Freeze();  // 凍結以便跨執行緒使用
                        _logger.LogDebug("圖片已轉換為 BitmapSource 供 UI 顯示");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "圖片轉換失敗");
                        annotatedBitmap = null;
                    }
                }
            }

            // 4. 儲存圖片（在得到結果後，根據 OK/NG 存到對應資料夾）
            string? imagePath = null;
            string? annotatedPath = null;
            if (_options?.SaveImages == true)
            {
                try
                {
                    var workOrderCode = _options?.WorkOrderCode ?? "Default";
                    // 根據過濾後結果決定存到 OK 或 NG
                    var saveResult = filteredIsOk ? "OK" : "NG";
                    _logger.LogDebug("開始儲存圖片，工單: {WorkOrder}, 結果: {Result}", workOrderCode, saveResult);

                    var paths = await _imageService.SaveInspectionImageAsync(
                        image,
                        workOrderCode,
                        saveResult,
                        annotatedImage,  // 傳入標註圖片（若有）
                        _cts!.Token);

                    imagePath = paths.originalPath;
                    annotatedPath = paths.annotatedPath;
                    _logger.LogInformation("圖片已儲存: {Path} (結果: {Result})", imagePath, saveResult);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "儲存圖片失敗，但不影響主流程");
                }
            }

            // 5. 回報結果給 PLC（使用過濾後結果）
            State = AutoRunState.Reporting;
            var result = filteredIsOk ? PlcInspectionResult.Ok : PlcInspectionResult.Ng;
            await _plcHandshake.ReportResultAsync(result, _cts.Token);

            // ======================================================================
            // ⚡ 送出結果後，降低光源亮度到待機亮度（0%）（非阻塞）
            // ======================================================================
            if (_lightPort != null)
            {
                // 並行執行，不等待完成
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _lightPort.SetIdleBrightnessAsync(_cts!.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "調整光源到待機亮度失敗（不影響流程）");
                    }
                });
            }

            totalStopwatch.Stop();
            var totalTimeMs = totalStopwatch.ElapsedMilliseconds;

            // 6. 更新統計（使用過濾後結果）
            Statistics.Update(filteredIsOk, captureTimeMs, inferenceTimeMs, totalTimeMs);

            // 7. 發送完成事件（傳入瑕疵用於統計，並傳入 BitmapSource 供 UI 顯示）
            // 使用過濾後結果決定顯示標籤
            // - OK 時顯示 "OK"
            // - NG 時顯示瑕疵類別名稱（優先用過濾後的有效瑕疵，否則用原始 prediction.Label）
            var displayLabel = filteredIsOk
                ? "OK"
                : (filterResult?.ValidDefects.FirstOrDefault()?.ClassName ?? prediction.Label);
            InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(
                Statistics.TotalCount,
                filteredIsOk,
                displayLabel,
                prediction.Confidence,
                captureTimeMs,
                inferenceTimeMs,
                totalTimeMs,
                imagePath,
                annotatedPath,
                annotatedBitmap,  // 新增：傳入標註圖的 BitmapSource
                defectsForStats));

            _logger.LogInformation("===== 檢測循環 #{Index} 完成，總耗時 {Ms}ms =====",
                Statistics.TotalCount, totalTimeMs);

            // 回到等待狀態
            State = AutoRunState.WaitingTrigger;
            _lastActivityTime = DateTime.Now; // 完成後更新活動時間
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("檢測循環被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "檢測循環發生錯誤");
            Statistics.RecordError();

            // 發生錯誤時也降低光源亮度
            if (_lightPort != null)
            {
                try
                {
                    await _lightPort.SetIdleBrightnessAsync(CancellationToken.None);
                }
                catch
                {
                    // 忽略光源調整錯誤
                }
            }

            var errorType = DetermineErrorType(ex);
            var isRecoverable = Statistics.ConsecutiveErrorCount < (_options?.MaxConsecutiveErrors ?? 5);

            OnError(errorType, ex.Message, ex, isRecoverable, Statistics.ConsecutiveErrorCount);

            if (!isRecoverable)
            {
                _logger.LogError("連續錯誤達上限 ({Count})，停止 Auto Run",
                    Statistics.ConsecutiveErrorCount);
                await StopAsync();
            }
            else
            {
                // 嘗試回報 NG 並繼續（使用獨立的 CancellationToken，避免連鎖取消）
                try
                {
                    using var recoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _plcHandshake.NotifyCaptureCompleteAsync(recoveryCts.Token);
                    await _plcHandshake.ReportResultAsync(PlcInspectionResult.Ng, recoveryCts.Token);
                    State = AutoRunState.WaitingTrigger;
                }
                catch (Exception recoveryEx)
                {
                    _logger.LogWarning(recoveryEx, "錯誤恢復時 PLC 通訊失敗，將忽略並繼續");
                    // 即使恢復失敗也嘗試回到等待狀態
                    State = AutoRunState.WaitingTrigger;
                }
            }
        }
    }

    /// <summary>
    /// 取得 Line Scan 影像（PLC 觸發同步模式）
    ///
    /// 流程：
    /// 1. PLC 10001=1 觸發時，PC 設定 00001=1（表示取像中）
    /// 2. 此方法啟動相機（使用 Line Scan Panel 選擇的 UserSet，可能是 UserSet0 或 UserSet1）
    /// 3. 等待影像完成（相機硬體累積指定行數後輸出）
    /// 4. 停止相機（避免 Free Run 模式持續取像）
    /// 5. 取像完成後由呼叫端通知 NotifyCaptureCompleteAsync() → 00001=0
    ///
    /// 重要：相機每次取像後停止，避免 Free Run 模式持續產生影像
    /// </summary>
    private async Task<ImageData> CaptureLineScanImageAsync(CancellationToken ct)
    {
        var timeoutMs = _options?.CaptureTimeoutMs ?? 10000;

        _logger.LogInformation("===== Line Scan 取像開始 =====");

        try
        {
            // 1. 每次觸發時啟動相機（使用 Line Scan Panel 選擇的 UserSet）
            if (_initialLineScanSettings != null)
            {
                // 只有當 appsettings 有明確指定 LineRate 時才覆蓋 EEPROM
                // 否則設為 0，讓 IdsCameraPort 使用 EEPROM 的值
                var lineRateOverride = _options?.LineScanSettings?.LineRate > 0
                    ? _options.LineScanSettings.LineRate
                    : 0;  // 0 表示不覆蓋，使用 EEPROM 值

                // 保留 Line Scan Panel 選擇的 UserSet（可能是 UserSet0 或 UserSet1）
                var settings = _initialLineScanSettings with
                {
                    LineRate = lineRateOverride
                };

                if (lineRateOverride > 0)
                {
                    _logger.LogInformation("載入 {UserSet} 並啟動相機: {Width}x{Height}, LineRate={LineRate}Hz (來源: appsettings，覆蓋 EEPROM)",
                        settings.UserSetName, settings.Width, settings.TargetHeight, settings.LineRate);
                }
                else
                {
                    _logger.LogInformation("載入 {UserSet} 並啟動相機: {Width}x{Height}, LineRate=使用 EEPROM 設定",
                        settings.UserSetName, settings.Width, settings.TargetHeight);
                }

                // ✅ 修復：如果 Line Scan 正在運行（預覽模式），先停止
                if (_lineScan.IsScanning)
                {
                    _logger.LogInformation("Line Scan 預覽正在運行，先停止...");
                    await _lineScan.StopCaptureAsync(ct);
                    _logger.LogDebug("✓ Line Scan 預覽已停止");
                }

                await _lineScan.ConfigureAndStartAsync(settings, ct);
            }
            else
            {
                _logger.LogWarning("無 Line Scan 設定，使用快速啟動模式");
                await _lineScan.StartCaptureAsync(ct);
            }

            // 2. 使用 CaptureOnceAsync 等待影像
            _logger.LogInformation("等待 Line Scan 影像... (Encoder 驅動中)，超時: {TimeoutMs}ms", timeoutMs);

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            ImageData image;
            try
            {
                image = await _lineScan.CaptureOnceAsync(linkedCts.Token);
                _logger.LogInformation("收到影像 ({Width}x{Height})", image.Width, image.Height);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                var msg = $"等待 Line Scan 影像超時 ({timeoutMs}ms)。\n" +
                          "請確認：\n" +
                          "1. PLC 觸發時 Encoder 是否同步啟動\n" +
                          "2. Line Scan 相機是否正在接收 Encoder 脈衝\n" +
                          "3. 物料是否正在經過相機視野\n" +
                          "4. 行頻和目標行數設定是否正確";
                _logger.LogWarning(msg);
                throw new TimeoutException(msg);
            }

            // 3. 取像完成後停止相機（避免 Free Run 模式持續取像）
            _logger.LogInformation("取像完成，停止相機");
            await _lineScan.StopCaptureAsync(ct);

            _logger.LogInformation("===== Line Scan 取像結束 =====");
            return image;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("取像因外部請求而取消");
            if (_lineScan.IsScanning)
            {
                await _lineScan.StopCaptureAsync(CancellationToken.None);
            }
            throw;
        }
        catch (Exception)
        {
            // 發生錯誤時確保相機停止
            if (_lineScan.IsScanning)
            {
                try { await _lineScan.StopCaptureAsync(CancellationToken.None); }
                catch { /* 忽略停止時的錯誤 */ }
            }
            throw;
        }
    }

    private void OnLineScanImageCompleted(object? sender, LineScanImageEventArgs e)
    {
        // 記錄事件用於除錯
        _logger.LogDebug("Line Scan 影像完成事件，索引 {Index}，耗時 {Time}",
            e.ImageIndex, e.ElapsedTime);
    }

    private void OnLineScanError(object? sender, LineScanErrorEventArgs e)
    {
        _logger.LogError(e.Exception, "Line Scan 錯誤: {Message}", e.Message);
    }

    private async Task<Prediction> InferWithTimeoutAsync(ImageData image, CancellationToken ct)
    {
        var maxRetries = _options?.AutoRetryOnError == true ? (_options?.MaxRetryCount ?? 3) : 1;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            using var timeoutCts = new CancellationTokenSource(_options?.InferenceTimeoutMs ?? 5000);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                return await _aiInference.PredictAsync(image, linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning("推論超時 (嘗試 {Attempt}/{Max})", attempt, maxRetries);

                if (attempt >= maxRetries)
                {
                    _logger.LogWarning("推論超時 ({Ms}ms)，回報 NG", _options?.InferenceTimeoutMs);
                    return new Prediction(
                        label: "Timeout",
                        confidence: 0f,
                        isOk: false,
                        modelVersion: "unknown",
                        imagePath: null);
                }
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "推論失敗 (嘗試 {Attempt}/{Max}): {Message}", attempt, maxRetries, ex.Message);
            }

            // 等待一小段時間再重試
            if (attempt < maxRetries)
            {
                await Task.Delay(200 * attempt, ct); // 遞增延遲
            }
        }

        // 若所有重試都失敗，回報為服務錯誤
        _logger.LogError("推論服務多次失敗，回報 NG");
        return new Prediction(
            label: "ServiceError",
            confidence: 0f,
            isOk: false,
            modelVersion: "unknown",
            imagePath: null);
    }

    private static LineScanRoiSettings ConvertToLineScanRoiSettings(Domain.AutoRun.LineScanSettings settings)
    {
        return new LineScanRoiSettings
        {
            OffsetX = settings.OffsetX,
            OffsetY = settings.OffsetY,
            Width = settings.Width,
            TargetHeight = settings.TargetHeight,
            LineRate = settings.LineRate
        };
    }

    private static AutoRunErrorType DetermineErrorType(Exception ex)
    {
        // 先檢查內部例外
        var innerEx = ex.InnerException;
        while (innerEx != null)
        {
            var innerType = ClassifyException(innerEx);
            if (innerType != AutoRunErrorType.Unknown)
                return innerType;
            innerEx = innerEx.InnerException;
        }

        return ClassifyException(ex);
    }

    private static AutoRunErrorType ClassifyException(Exception ex)
    {
        // 依照例外類型分類
        return ex switch
        {
            TimeoutException => AutoRunErrorType.CaptureTimeout,
            TaskCanceledException => AutoRunErrorType.CaptureTimeout,
            SocketException => AutoRunErrorType.PlcConnection,
            HttpRequestException => AutoRunErrorType.InferenceService,
            InvalidOperationException when ContainsAny(ex.Message, "相機", "camera", "Camera") => AutoRunErrorType.CameraConnection,
            InvalidOperationException when ContainsAny(ex.Message, "PLC", "plc", "Plc", "Modbus") => AutoRunErrorType.PlcConnection,
            InvalidOperationException when ContainsAny(ex.Message, "推論", "inference", "AI", "模型") => AutoRunErrorType.InferenceService,
            InvalidOperationException when ContainsAny(ex.Message, "取像", "capture", "Capture") => AutoRunErrorType.CaptureError,
            IOException => AutoRunErrorType.SaveError,
            ArgumentException => AutoRunErrorType.ConfigurationError,
            _ => AutoRunErrorType.Unknown
        };
    }

    private static bool ContainsAny(string message, params string[] keywords)
    {
        return keywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    #region Event Helpers

    private void OnStateChanged(AutoRunState oldState, AutoRunState newState)
    {
        StateChanged?.Invoke(this, new AutoRunStateChangedEventArgs(oldState, newState));
    }

    private void OnError(AutoRunErrorType errorType, string message, Exception? ex,
        bool isRecoverable, int retryCount = 0)
    {
        ErrorOccurred?.Invoke(this, new AutoRunErrorEventArgs(
            errorType, message, ex, State, isRecoverable, retryCount));
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _pauseSemaphore.Dispose();
        }

        _disposed = true;
    }

    #endregion
}
