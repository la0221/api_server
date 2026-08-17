using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Models;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.Services;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Devices.Camera.Ids;

/// <summary>
/// IDS 相機 Line Scan 服務實作
///
/// 重要：Line Scan 使用相機硬體累積模式
/// - 設定 Height = TargetHeight，讓相機自動累積行數據
/// - 每次 WaitForFinishedBuffer 返回的就是完整的 Width x Height 影像
/// - 不需要軟體層面的拼接 (LineScanImageBuilder)
/// </summary>
public sealed class LineScanService : ILineScanService, IDisposable
{
    private readonly IdsCameraPort _cameraPort;
    private readonly ICameraDiscoveryPort _discoveryPort;
    private readonly ILogger<LineScanService> _logger;

    private LineScanRoiSettings? _currentSettings;
    private LineScanRoiSettings? _originalSettings;  // UI 原始設定（不會被 EEPROM 覆蓋）
    private LineScanRoiBounds? _bounds;
    private bool _isScanning;
    private int _completedImageCount;
    private Stopwatch? _scanStopwatch;
    private bool _disposed;
    private TaskCompletionSource<ImageData>? _captureCompletionSource;
    private bool _singleShotMode = false; // 改為 false：允許連續接收影像（被動等待模式需要）
    private bool _waitingForCapture; // 是否正在等待取像

    // 被動等待模式：影像緩衝
    private ImageData? _latestImage;
    private DateTime _latestImageTime = DateTime.MinValue;
    private TaskCompletionSource<ImageData>? _nextImageWaiter;
    private readonly object _latestImageLock = new();

    public LineScanService(
        ICameraPort cameraPort,
        ICameraDiscoveryPort discoveryPort,
        ILogger<LineScanService> logger)
    {
        if (cameraPort is not IdsCameraPort idsCameraPort)
        {
            throw new ArgumentException(
                $"LineScanService 需要 IdsCameraPort，但收到 {cameraPort.GetType().Name}",
                nameof(cameraPort));
        }
        _cameraPort = idsCameraPort;
        _discoveryPort = discoveryPort;
        _logger = logger;
    }

    public bool IsCameraConnected => _cameraPort.IsOpen;
    public LineScanRoiSettings? CurrentSettings => _currentSettings;
    public LineScanRoiSettings? OriginalSettings => _originalSettings;  // UI 原始設定（不會被 EEPROM 覆蓋）
    public LineScanRoiBounds? Bounds => _bounds;
    public bool IsScanning => _isScanning;
    public int CurrentLineIndex => 0; // 相機硬體累積，不追蹤單行
    public int CompletedImageCount => _completedImageCount;

    // 被動等待模式屬性
    public ImageData? LatestImage
    {
        get { lock (_latestImageLock) return _latestImage; }
    }

    public DateTime LatestImageTime
    {
        get { lock (_latestImageLock) return _latestImageTime; }
    }

    // 硬體累積模式下不使用 LineReceived 事件，但介面需要實作
#pragma warning disable CS0067
    public event EventHandler<LineScanLineEventArgs>? LineReceived;
#pragma warning restore CS0067
    public event EventHandler<LineScanImageEventArgs>? ImageCompleted;
    public event EventHandler<LineScanErrorEventArgs>? ScanError;

    public async Task<bool> ConnectCameraAsync(CancellationToken ct = default)
    {
        if (_cameraPort.IsOpen)
        {
            _logger.LogDebug("相機已連接");
            return true;
        }

        _logger.LogInformation("正在搜尋並連接相機...");

        try
        {
            // 搜尋可用相機
            var devices = await _discoveryPort.ListAsync(ct).ConfigureAwait(false);
            if (devices.Count == 0)
            {
                _logger.LogWarning("未找到可用的相機");
                return false;
            }

            // 取得第一台相機的 ID
            var deviceId = devices[0].Id;
            _logger.LogInformation("找到相機: {DeviceId} ({Name}), 正在連接...", deviceId, devices[0].Name);

            // 開啟相機
            await _cameraPort.OpenAsync(deviceId, ct).ConfigureAwait(false);

            _logger.LogInformation("✓ 相機連接成功");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "連接相機失敗");
            return false;
        }
    }

    public async Task<ImageData?> CaptureAreaPreviewAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("══════ CaptureAreaPreviewAsync 開始 ══════");

        try
        {
            // 確保相機已開啟，如果沒有則嘗試連接
            _logger.LogInformation("  檢查相機狀態: IsOpen={IsOpen}", _cameraPort.IsOpen);
            if (!_cameraPort.IsOpen)
            {
                _logger.LogInformation("  相機尚未開啟，嘗試自動連接...");
                var connected = await ConnectCameraAsync(ct).ConfigureAwait(false);
                if (!connected)
                {
                    _logger.LogWarning("  無法連接相機");
                    return null;
                }
                _logger.LogInformation("  相機連接成功");
            }

            // 使用預覽模式取像 (解決單次取像超時問題)
            // 參考 CameraTestViewModel: 啟動預覽 → 等待第一幀 → 停止預覽
            _logger.LogInformation("  啟動預覽模式...");

            ImageData? capturedImage = null;
            var imageReceived = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnFrameReceived(object? sender, ImageData image)
            {
                _logger.LogInformation("  收到影像幀: {Width}x{Height}", image.Width, image.Height);
                imageReceived.TrySetResult(image);
            }

            _cameraPort.FrameReceived += OnFrameReceived;
            try
            {
                // 啟動預覽
                await _cameraPort.StartPreviewAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("  預覽已啟動，等待影像...");

                // 等待第一幀影像 (最多 10 秒)
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                using var registration = linkedCts.Token.Register(() =>
                    imageReceived.TrySetCanceled(linkedCts.Token));

                try
                {
                    capturedImage = await imageReceived.Task.ConfigureAwait(false);
                    _logger.LogInformation("  ✓ 成功收到影像: {Width}x{Height}", capturedImage?.Width ?? 0, capturedImage?.Height ?? 0);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    _logger.LogWarning("  等待影像超時 (10秒)");
                    return null;
                }
            }
            finally
            {
                _cameraPort.FrameReceived -= OnFrameReceived;

                // 停止預覽
                _logger.LogInformation("  停止預覽...");
                await _cameraPort.StopPreviewAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("  預覽已停止");
            }

            _logger.LogInformation("✓ Area Scan 預覽完成: {Width}x{Height}",
                capturedImage?.Width ?? 0, capturedImage?.Height ?? 0);

            // 同時更新邊界
            _logger.LogInformation("  呼叫 UpdateBoundsAsync()...");
            await UpdateBoundsAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("  UpdateBoundsAsync 完成");

            _logger.LogInformation("══════ CaptureAreaPreviewAsync 結束 ══════");
            return capturedImage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Area Scan 預覽失敗");
            _logger.LogInformation("══════ CaptureAreaPreviewAsync 異常結束 ══════");
            return null;
        }
    }

    public Task UpdateBoundsAsync(CancellationToken ct = default)
    {
        try
        {
            var roiBounds = _cameraPort.GetRoiBounds();
            var sensorSize = _cameraPort.GetSensorSize();

            if (roiBounds.HasValue && sensorSize.HasValue)
            {
                _bounds = new LineScanRoiBounds
                {
                    OffsetXMin = roiBounds.Value.minX,
                    OffsetXMax = roiBounds.Value.maxX,
                    OffsetYMin = roiBounds.Value.minY,
                    OffsetYMax = roiBounds.Value.maxY,
                    WidthMin = roiBounds.Value.minW,
                    WidthMax = roiBounds.Value.maxW,
                    LineRateMin = 100,
                    LineRateMax = 250000,
                    SensorWidth = sensorSize.Value.width,
                    SensorHeight = sensorSize.Value.height
                };

                _logger.LogInformation("ROI 邊界已更新: Sensor={SensorW}x{SensorH}, OffsetX=[{MinX},{MaxX}], Width=[{MinW},{MaxW}]",
                    sensorSize.Value.width, sensorSize.Value.height,
                    roiBounds.Value.minX, roiBounds.Value.maxX,
                    roiBounds.Value.minW, roiBounds.Value.maxW);
            }
            else
            {
                _logger.LogWarning("無法讀取 ROI 邊界");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新 ROI 邊界失敗");
        }

        return Task.CompletedTask;
    }

    public async Task ConfigureAndStartAsync(LineScanRoiSettings settings, CancellationToken ct = default)
    {
        if (!settings.IsValid)
        {
            throw new ArgumentException("ROI 設定無效", nameof(settings));
        }

        if (_isScanning)
        {
            throw new InvalidOperationException("已經在掃描中");
        }

        _logger.LogInformation("══════ ConfigureAndStartAsync 開始 ══════");
        _logger.LogInformation("配置 Line Scan (硬體累積模式): OffsetX={OffsetX}, OffsetY={OffsetY}, Width={Width}, Height={Height}, LineRate={LineRate}Hz",
            settings.OffsetX, settings.OffsetY, settings.Width, settings.TargetHeight, settings.LineRate);

        // 保存 UI 原始設定（不會被 EEPROM 覆蓋）
        _originalSettings = settings;

        try
        {
            // 停止目前的預覽
            _logger.LogInformation("  停止目前預覽...");
            await _cameraPort.StopPreviewAsync(ct).ConfigureAwait(false);

            // 設定 ROI - 關鍵修改：Height = TargetHeight
            // 讓相機硬體自動累積 TargetHeight 行後輸出完整影像
            _logger.LogInformation("  設定 ROI: Height={Height} (相機硬體累積模式)", settings.TargetHeight);
            _logger.LogInformation("  曝光時間: {Exposure} µs, 增益: {Gain}",
                settings.ExposureTimeUs?.ToString("F0") ?? "未指定",
                settings.Gain?.ToString("F2") ?? "未指定");

            // 當使用 UserSet0 或 UserSet1 時，LineRate 設為 0 表示使用 EEPROM 的設定
            // 這樣可以讓使用者在 IDS Cockpit 中設定的 LineRate 生效
            var isUsingEepromUserSet = settings.UserSetName is "UserSet0" or "UserSet1";
            var lineRateToApply = isUsingEepromUserSet ? 0 : settings.LineRate;

            if (isUsingEepromUserSet)
            {
                _logger.LogInformation("  使用 {UserSet} - LineRate 將從 EEPROM 載入，不覆蓋", settings.UserSetName);
            }

            var (actualWidth, actualHeight, actualLineRate) = _cameraPort.ApplyLineScanRoi(
                settings.OffsetX,
                settings.OffsetY,
                settings.Width,
                height: settings.TargetHeight,  // 關鍵：使用 TargetHeight 而非 1
                exposureTimeUs: isUsingEepromUserSet ? null : settings.ExposureTimeUs,  // EEPROM 模式不覆蓋曝光
                gain: isUsingEepromUserSet ? null : settings.Gain,  // EEPROM 模式不覆蓋增益
                lineRate: lineRateToApply,
                userSetName: settings.UserSetName);  // 傳遞 UserSet 設定

            // 使用相機實際的 ROI 設定 (UserSet0/UserSet1 時會與傳入參數不同)
            _currentSettings = settings with
            {
                Width = actualWidth,
                TargetHeight = actualHeight,
                LineRate = actualLineRate
            };
            _completedImageCount = 0;

            // 訂閱幀接收事件
            _cameraPort.FrameReceived += OnFrameReceived;

            // 啟動掃描
            _isScanning = true;
            _scanStopwatch = Stopwatch.StartNew();

            _logger.LogInformation("  啟動預覽...");
            await _cameraPort.StartPreviewAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("✓ Line Scan 已啟動 (實際設定: {Width}x{Height}, 行頻: {LineRate}Hz)",
                _currentSettings.Width, _currentSettings.TargetHeight, _currentSettings.LineRate);
            _logger.LogInformation("══════ ConfigureAndStartAsync 結束 ══════");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置 Line Scan 失敗");
            _isScanning = false;
            _cameraPort.FrameReceived -= OnFrameReceived;
            throw;
        }
    }

    public async Task StopAndResetAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("停止 Line Scan...");

        try
        {
            _isScanning = false;
            _cameraPort.FrameReceived -= OnFrameReceived;

            await _cameraPort.StopPreviewAsync(ct).ConfigureAwait(false);

            _scanStopwatch?.Stop();

            // 取消等待中的取像請求
            _captureCompletionSource?.TrySetCanceled();
            _captureCompletionSource = null;

            _logger.LogInformation("✓ Line Scan 已停止，共完成 {Count} 張圖像", _completedImageCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止 Line Scan 失敗");
            throw;
        }
    }

    /// <summary>
    /// 快速啟動取像（使用已載入的設定）
    /// 適用於 Auto Run：相機設定已在 Line Scan Panel 載入過
    /// </summary>
    public async Task StartCaptureAsync(CancellationToken ct = default)
    {
        if (!_cameraPort.IsOpen)
        {
            throw new InvalidOperationException("相機未連接");
        }

        if (_currentSettings == null)
        {
            throw new InvalidOperationException("相機尚未設定，請先在 Line Scan Panel 載入設定並測試取像");
        }

        if (_isScanning)
        {
            _logger.LogWarning("相機已在運行中，跳過啟動");
            return;
        }

        _logger.LogInformation("快速啟動取像 (使用已載入設定: {Width}x{Height}, LineRate={LineRate}Hz)",
            _currentSettings.Width, _currentSettings.TargetHeight, _currentSettings.LineRate);

        // 訂閱事件
        _cameraPort.FrameReceived += OnFrameReceived;
        _isScanning = true;
        _scanStopwatch = Stopwatch.StartNew();

        // 只啟動預覽，不重新設定
        await _cameraPort.StartPreviewAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("✓ 快速啟動完成");
    }

    /// <summary>
    /// 快速停止取像
    /// </summary>
    public async Task StopCaptureAsync(CancellationToken ct = default)
    {
        if (!_isScanning)
        {
            return;
        }

        _logger.LogInformation("快速停止取像...");

        _isScanning = false;
        _cameraPort.FrameReceived -= OnFrameReceived;

        await _cameraPort.StopPreviewAsync(ct).ConfigureAwait(false);

        _scanStopwatch?.Stop();

        // 取消等待中的請求
        _captureCompletionSource?.TrySetCanceled();
        _captureCompletionSource = null;
        _nextImageWaiter?.TrySetCanceled();
        _nextImageWaiter = null;

        _logger.LogInformation("✓ 快速停止完成");
    }

    /// <inheritdoc />
    public async Task<ImageData> CaptureOnceAsync(CancellationToken ct = default)
    {
        if (!_isScanning)
        {
            throw new InvalidOperationException("Line Scan 尚未啟動，請先呼叫 ConfigureAndStartAsync");
        }

        // 避免重複調用
        if (_waitingForCapture)
        {
            _logger.LogWarning("已有取像請求在等待中，忽略此次調用");
            throw new InvalidOperationException("已有取像請求在等待中");
        }

        // 建立 TaskCompletionSource 等待完成
        _captureCompletionSource = new TaskCompletionSource<ImageData>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // 標記正在等待取像
        _waitingForCapture = true;

        // 重置計時器
        _scanStopwatch = Stopwatch.StartNew();

        // 註冊取消 token
        using var registration = ct.Register(() =>
        {
            _waitingForCapture = false;
            _captureCompletionSource?.TrySetCanceled(ct);
        });

        _logger.LogInformation("等待 Line Scan 取像完成 (單張模式)... 設定: {Width}x{Height}, 行頻: {LineRate}Hz",
            _currentSettings?.Width ?? 0, _currentSettings?.TargetHeight ?? 0, _currentSettings?.LineRate ?? 0);
        _logger.LogDebug("  請確保相機正在接收觸發信號（Encoder 或 Line Trigger）");

        try
        {
            // 等待 ImageCompleted 事件觸發 (由 OnFrameReceived 設定結果)
            return await _captureCompletionSource.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var elapsed = _scanStopwatch?.Elapsed.TotalSeconds ?? 0;
            _logger.LogWarning("Line Scan 取像被取消/超時，等待時間: {Elapsed:F1}s", elapsed);
            _logger.LogWarning("  可能原因: 相機未收到觸發信號或行頻設定不正確");
            throw;
        }
        finally
        {
            _waitingForCapture = false;
        }
    }

    /// <inheritdoc />
    public void ResetForNextCapture()
    {
        // 相機硬體累積模式下，只需重置計時器
        _scanStopwatch = Stopwatch.StartNew();
        _logger.LogDebug("計時器已重置，準備下一次取像");
    }

    #region 被動等待模式實作

    /// <inheritdoc />
    public ImageData? GetLatestImageIfFresh(int maxAgeMs = 5000)
    {
        lock (_latestImageLock)
        {
            if (_latestImage == null)
                return null;

            var age = (DateTime.Now - _latestImageTime).TotalMilliseconds;
            if (age > maxAgeMs)
            {
                _logger.LogDebug("最近影像已過期 ({Age:F0}ms > {Max}ms)", age, maxAgeMs);
                return null;
            }

            _logger.LogDebug("取用最近影像，age={Age:F0}ms", age);
            return _latestImage;
        }
    }

    /// <inheritdoc />
    public async Task<ImageData> WaitForNextImageAsync(CancellationToken ct = default)
    {
        if (!_isScanning)
        {
            throw new InvalidOperationException("Line Scan 尚未啟動，請先呼叫 ConfigureAndStartAsync");
        }

        // 建立等待器
        _nextImageWaiter = new TaskCompletionSource<ImageData>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // 註冊取消
        using var registration = ct.Register(() =>
        {
            _nextImageWaiter?.TrySetCanceled(ct);
        });

        _logger.LogInformation("等待下一張 Line Scan 影像 (被動等待模式)...");

        try
        {
            return await _nextImageWaiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("等待影像被取消/超時");
            throw;
        }
        finally
        {
            _nextImageWaiter = null;
        }
    }

    /// <inheritdoc />
    public void ClearLatestImage()
    {
        lock (_latestImageLock)
        {
            _latestImage = null;
            _latestImageTime = DateTime.MinValue;
        }
        _logger.LogDebug("已清除暫存影像");
    }

    #endregion

    /// <summary>
    /// 處理相機輸出的完整影像
    ///
    /// 重要：在硬體累積模式下，每次 FrameReceived 收到的就是完整的 Width x Height 影像
    /// 不需要軟體拼接
    ///
    /// 被動等待模式：每張影像都會儲存到 _latestImage，供 PLC 觸發時取用
    /// </summary>
    private void OnFrameReceived(object? sender, ImageData image)
    {
        if (!_isScanning || _currentSettings is null)
        {
            return;
        }

        try
        {
            // 硬體累積模式：每次收到的就是完整的 Width x Height 影像
            var expectedWidth = (int)_currentSettings.Width;
            var expectedHeight = _currentSettings.TargetHeight;

            // 計算實際行頻
            var elapsed = _scanStopwatch?.Elapsed ?? TimeSpan.Zero;
            var actualLineRate = elapsed.TotalSeconds > 0 ? expectedHeight / elapsed.TotalSeconds : 0;

            _completedImageCount++;

            // 簡化 log 輸出
            var sizeMatch = (image.Width == expectedWidth && image.Height == expectedHeight);
            _logger.LogInformation("收到影像 #{Index}: {Width}x{Height}, 耗時 {Elapsed:F2}s, 行頻 {LineRate:F0}Hz {Status}",
                _completedImageCount, image.Width, image.Height, elapsed.TotalSeconds, actualLineRate,
                sizeMatch ? "✓" : "⚠尺寸不符");

            if (!sizeMatch)
            {
                _logger.LogWarning("  預期 {ExpWidth}x{ExpHeight}, 實際 {Width}x{Height}",
                    expectedWidth, expectedHeight, image.Width, image.Height);
            }

            // 被動等待模式：儲存最近影像
            lock (_latestImageLock)
            {
                _latestImage = image;
                _latestImageTime = DateTime.Now;
            }

            // 單張模式 (CaptureOnceAsync)：處理完一張後標記完成
            if (_singleShotMode && _waitingForCapture)
            {
                _waitingForCapture = false;
            }

            // 觸發 ImageCompleted 事件
            ImageCompleted?.Invoke(this, new LineScanImageEventArgs
            {
                Image = image,  // 直接使用相機輸出的完整影像
                ImageIndex = _completedImageCount - 1,
                ElapsedTime = elapsed,
                ActualLineRate = actualLineRate
            });

            // 設定 CaptureOnceAsync 的結果 (如果有等待中的請求)
            _captureCompletionSource?.TrySetResult(image);

            // 設定 WaitForNextImageAsync 的結果 (被動等待模式)
            _nextImageWaiter?.TrySetResult(image);

            // 重置計時器，準備下一張影像
            _scanStopwatch = Stopwatch.StartNew();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "處理 Line Scan 影像失敗");

            // 設定錯誤到等待中的請求
            _captureCompletionSource?.TrySetException(ex);
            _nextImageWaiter?.TrySetException(ex);

            ScanError?.Invoke(this, new LineScanErrorEventArgs
            {
                Exception = ex,
                Message = $"處理影像失敗: {ex.Message}",
                IsFatal = false
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _isScanning = false;
        _cameraPort.FrameReceived -= OnFrameReceived;
        _disposed = true;
    }
}
