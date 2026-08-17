using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIVision.Application.Models;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.Services;
using AIVision.Domain.Shared;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class LineScanViewModel : ObservableObject, IDisposable
{
    private readonly ILineScanService _lineScanService;
    private readonly ILineScanSimulator _simulator;
    private readonly ICameraControlPort _cameraControl;
    private readonly ILogger<LineScanViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private DateTime _lastUiUpdate = DateTime.MinValue;
    private const int UI_UPDATE_INTERVAL_MS = 50;
    private bool _disposed;

    public LineScanViewModel(
        ILineScanService lineScanService,
        ILineScanSimulator simulator,
        ICameraControlPort cameraControl,
        ILogger<LineScanViewModel> logger)
    {
        _lineScanService = lineScanService;
        _simulator = simulator;
        _cameraControl = cameraControl;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _logger.LogInformation("═══════ LineScanViewModel 初始化開始 ═══════");

        // 訂閱 LineScanService 事件
        _lineScanService.LineReceived += OnLineReceived;
        _lineScanService.ImageCompleted += OnImageCompleted;
        _lineScanService.ScanError += OnScanError;

        // 訂閱模擬器事件
        _simulator.LineReceived += OnSimulatorLineReceived;
        _simulator.ImageCompleted += OnSimulatorImageCompleted;
        _simulator.SimulatorError += OnSimulatorError;

        // 嘗試載入儲存的設定，如果沒有則使用預設值
        var savedSettings = LineScanUserSettings.Load();
        if (savedSettings != null)
        {
            _logger.LogInformation("載入已儲存的 Line Scan 設定: {FilePath}", LineScanUserSettings.DefaultFilePath);
            ApplySettings(savedSettings);
        }
        else
        {
            _logger.LogInformation("使用預設 Line Scan 設定");
            // 設定預設值
            OffsetX = 0;
            OffsetY = 0;
            Width = 1200;
            TargetHeight = 2000;
            LineRate = 1000;

            // 相機參數預設值
            ExposureTime = 10000;
            Gain = 1.0;
        }

        // 模擬器預設值
        SimulatorAnchorX = 0;
        SimulatorAnchorY = 0;
        SimulatorScanWidth = 1200;
        SimulatorScanHeight = 2000;
        SimulatorLineRate = 1000;
        SimulatorEnableTiming = true;

        _logger.LogInformation("═══════ LineScanViewModel 初始化完成 ═══════");
        _logger.LogInformation("  ILineScanService: {ServiceType}", _lineScanService.GetType().FullName);
        _logger.LogInformation("  ICameraControlPort: {ControlType}", _cameraControl.GetType().FullName);
        _logger.LogInformation("  ILineScanSimulator: {SimulatorType}", _simulator.GetType().FullName);
    }

    // === 影像顯示 ===
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    private BitmapSource? _previewBitmap;

    [ObservableProperty]
    private BitmapSource? _resultBitmap;

    // === 滑鼠座標 ===
    [ObservableProperty]
    private int _mouseX;

    [ObservableProperty]
    private int _mouseY;

    [ObservableProperty]
    private string _mouseCoordinateText = "X: -, Y: -";

    // === ROI 參數 ===
    [ObservableProperty]
    private long _offsetX;

    [ObservableProperty]
    private long _offsetY;

    [ObservableProperty]
    private long _width = 1200;

    [ObservableProperty]
    private int _targetHeight = 2000;

    [ObservableProperty]
    private double _lineRate = 1000;

    // === ROI 邊界 (用於 UI 驗證) ===
    [ObservableProperty]
    private long _offsetXMax = 4096;

    [ObservableProperty]
    private long _offsetYMax = 4096;

    [ObservableProperty]
    private long _widthMin = 64;

    [ObservableProperty]
    private long _widthMax = 4096;

    [ObservableProperty]
    private double _lineRateMin = 100;

    [ObservableProperty]
    private double _lineRateMax = 250000;

    [ObservableProperty]
    private long _sensorWidth = 4096;

    [ObservableProperty]
    private long _sensorHeight = 4096;

    // === 相機參數 ===
    [ObservableProperty]
    private double _exposureTime = 10000;

    [ObservableProperty]
    private double _exposureTimeMin = 10;

    // === 相機 UserSet 設定 ===
    [ObservableProperty]
    private string _selectedUserSet = "Linescan";

    /// <summary>
    /// 可用的 UserSet 選項
    /// </summary>
    public string[] AvailableUserSets { get; } = ["Linescan", "UserSet0", "UserSet1", "Default"];

    [ObservableProperty]
    private double _exposureTimeMax = 200000;

    [ObservableProperty]
    private double _gain = 1.0;

    [ObservableProperty]
    private double _gainMin = 0;

    [ObservableProperty]
    private double _gainMax = 24;

    [ObservableProperty]
    private long _heightMax = 8192;

    // === 狀態 ===
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCameraParametersCommand))]
    private bool _isCameraConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CapturePreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCameraParametersCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CapturePreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCameraParametersCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadSimulatorImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    private bool _isBusy;

    // === 進度 ===
    [ObservableProperty]
    private int _currentLine;

    [ObservableProperty]
    private int _totalLines;

    [ObservableProperty]
    private double _scanProgress; // 0-100

    [ObservableProperty]
    private string _progressText = "0 / 0";

    // === 統計 ===
    [ObservableProperty]
    private int _completedImages;

    [ObservableProperty]
    private string _statusMessage = "就緒 - 點擊「擷取預覽」連接相機並取得影像";

    // === 模擬模式 ===
    [ObservableProperty]
    private bool _isSimulationMode;

    [ObservableProperty]
    private string? _simulatorSourceImagePath;

    [ObservableProperty]
    private BitmapSource? _simulatorSourcePreview;

    [ObservableProperty]
    private int _simulatorAnchorX;

    [ObservableProperty]
    private int _simulatorAnchorY;

    [ObservableProperty]
    private int _simulatorScanWidth = 1200;

    [ObservableProperty]
    private int _simulatorScanHeight = 2000;

    [ObservableProperty]
    private double _simulatorLineRate = 1000;

    [ObservableProperty]
    private bool _simulatorEnableTiming = true;

    [ObservableProperty]
    private bool _isSimulating;

    [ObservableProperty]
    private bool _isSimulatorPaused;

    [ObservableProperty]
    private string _simulatorSourceInfo = "尚未載入圖片";

    [ObservableProperty]
    private int _simulatorSourceWidth;

    [ObservableProperty]
    private int _simulatorSourceHeight;

    // === 儲存設定 ===
    [ObservableProperty]
    private bool _autoSaveImages = true;

    [ObservableProperty]
    private string _saveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LineScan");

    // === 連續掃描設定 ===
    /// <summary>
    /// 是否連續掃描（收到一張影像後自動觸發下一張）
    /// 預設為 false，避免記憶體爆滿
    /// </summary>
    [ObservableProperty]
    private bool _continuousScan = false;

    // === Commands ===

    [RelayCommand(CanExecute = nameof(CanCapturePreview))]
    private async Task CapturePreviewAsync()
    {
        _logger.LogInformation("──────── CapturePreviewAsync 開始 ────────");
        IsBusy = true;

        // 檢查相機狀態
        IsCameraConnected = _lineScanService.IsCameraConnected;
        _logger.LogInformation("  IsCameraConnected (初始): {Connected}", IsCameraConnected);

        if (!IsCameraConnected)
        {
            StatusMessage = "正在連接相機...";
            _logger.LogInformation("  相機未連接，將嘗試自動連接");
        }
        else
        {
            StatusMessage = "正在擷取預覽影像...";
        }

        try
        {
            _logger.LogInformation("  呼叫 _lineScanService.CaptureAreaPreviewAsync()...");
            var image = await _lineScanService.CaptureAreaPreviewAsync();
            _logger.LogInformation("  CaptureAreaPreviewAsync 返回, HasValue: {HasValue}", image.HasValue);

            // 更新連線狀態
            IsCameraConnected = _lineScanService.IsCameraConnected;
            _logger.LogInformation("  IsCameraConnected (擷取後): {Connected}", IsCameraConnected);

            if (image.HasValue)
            {
                _logger.LogInformation("  影像資訊: {Width}x{Height}, PixelFormat: {PixelFormat}",
                    image.Value.Width, image.Value.Height, image.Value.PixelFormat);
                PreviewBitmap = BitmapSourceFactory.FromImageData(image.Value);
                StatusMessage = $"✓ 預覽完成 ({image.Value.Width} x {image.Value.Height})";

                // 更新邊界
                _logger.LogInformation("  呼叫 UpdateBoundsFromService()...");
                UpdateBoundsFromService();

                // 通知 Command 狀態更新（讓「開始掃描」和「套用參數」按鈕可用）
                _logger.LogInformation("  通知 Command 狀態更新...");
                StartScanCommand.NotifyCanExecuteChanged();
                ApplyCameraParametersCommand.NotifyCanExecuteChanged();
            }
            else
            {
                _logger.LogWarning("  CaptureAreaPreviewAsync 返回 null");
                StatusMessage = "無法擷取預覽影像 - 請確認相機已連接";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "  CapturePreviewAsync 發生例外");
            StatusMessage = $"預覽失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _logger.LogInformation("──────── CapturePreviewAsync 結束 ────────");
        }
    }

    private bool CanCapturePreview() => !IsBusy && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        _logger.LogInformation("──────── StartScanAsync 開始 ────────");
        _logger.LogInformation("  ROI 參數: OffsetX={OffsetX}, OffsetY={OffsetY}, Width={Width}, TargetHeight={TargetHeight}, LineRate={LineRate}",
            OffsetX, OffsetY, Width, TargetHeight, LineRate);

        if (!ValidateSettings(out var errorMessage))
        {
            _logger.LogWarning("  參數驗證失敗: {Error}", errorMessage);
            StatusMessage = errorMessage;
            return;
        }

        IsBusy = true;
        StatusMessage = "正在啟動 Line Scan...";

        try
        {
            var settings = new LineScanRoiSettings
            {
                OffsetX = OffsetX,
                OffsetY = OffsetY,
                Width = Width,
                TargetHeight = TargetHeight,
                LineRate = LineRate,
                ExposureTimeUs = ExposureTime,  // 傳遞曝光時間到 Line Scan 模式
                Gain = Gain,                     // 傳遞增益到 Line Scan 模式
                UserSetName = SelectedUserSet    // 傳遞 UserSet 名稱
            };

            _logger.LogInformation("  呼叫 _lineScanService.ConfigureAndStartAsync()...");
            await _lineScanService.ConfigureAndStartAsync(settings);
            _logger.LogInformation("  ConfigureAndStartAsync 完成");

            IsScanning = true;
            TotalLines = TargetHeight;
            CurrentLine = 0;
            ScanProgress = 0;
            ProgressText = $"等待相機累積 {TargetHeight} 行...";
            CompletedImages = 0;
            StatusMessage = $"掃描中... (單張模式: {Width}x{TargetHeight})";

            // 更新 Command 狀態
            CapturePreviewCommand.NotifyCanExecuteChanged();
            StartScanCommand.NotifyCanExecuteChanged();
            StopScanCommand.NotifyCanExecuteChanged();
            CaptureNextCommand.NotifyCanExecuteChanged();

            // 自動觸發第一次取像（單張模式）
            _logger.LogInformation("  觸發第一次取像...");
            _ = TriggerCaptureAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"啟動失敗: {ex.Message}";
            IsScanning = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 觸發單次取像（在背景執行）
    /// </summary>
    private async Task TriggerCaptureAsync()
    {
        try
        {
            _logger.LogInformation("TriggerCaptureAsync: 開始等待取像...");
            var image = await _lineScanService.CaptureOnceAsync();
            _logger.LogInformation("TriggerCaptureAsync: 取像完成，影像 {Width}x{Height}", image.Width, image.Height);
            // 影像會透過 ImageCompleted 事件處理
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("TriggerCaptureAsync: 取像被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TriggerCaptureAsync: 取像失敗");
            _dispatcher.BeginInvoke(() =>
            {
                StatusMessage = $"取像失敗: {ex.Message}";
            });
        }
    }

    private bool CanStartScan() => !IsBusy && !IsScanning && PreviewBitmap is not null;

    /// <summary>
    /// 手動觸發下一次取像（單張模式下使用）
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCaptureNext))]
    private void CaptureNext()
    {
        _logger.LogInformation("手動觸發下一次取像");
        StatusMessage = "正在取像...";
        _ = TriggerCaptureAsync();
    }

    private bool CanCaptureNext() => IsScanning && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStopScan))]
    private async Task StopScanAsync()
    {
        // 防止重複呼叫（單張模式自動停止 + 用戶手動停止可能同時發生）
        if (!IsScanning)
        {
            _logger.LogDebug("StopScanAsync: 已經停止，跳過重複呼叫");
            return;
        }

        // 先標記為停止狀態，防止並發問題
        IsScanning = false;
        IsBusy = true;
        StatusMessage = "正在停止掃描...";

        try
        {
            await _lineScanService.StopAndResetAsync();
            StatusMessage = $"掃描已停止，共完成 {CompletedImages} 張圖像";

            // 更新 Command 狀態
            CapturePreviewCommand.NotifyCanExecuteChanged();
            StartScanCommand.NotifyCanExecuteChanged();
            StopScanCommand.NotifyCanExecuteChanged();
            CaptureNextCommand.NotifyCanExecuteChanged();
        }
        catch (ObjectDisposedException)
        {
            // CTS 已被釋放，這在快速連續停止時是正常的
            _logger.LogDebug("StopScanAsync: CancellationTokenSource 已被釋放");
            StatusMessage = $"掃描已停止，共完成 {CompletedImages} 張圖像";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StopScanAsync: 停止時發生例外");
            StatusMessage = $"停止失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStopScan() => IsScanning;

    // === 模擬器 Commands ===

    [RelayCommand(CanExecute = nameof(CanLoadSimulatorImage))]
    private void LoadSimulatorImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "選擇來源圖片",
            Filter = "圖片檔案|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有檔案|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                SimulatorSourceImagePath = dialog.FileName;

                // 載入圖片並轉換為灰階
                var bitmap = new BitmapImage(new Uri(SimulatorSourceImagePath));
                var grayBitmap = ConvertToGrayscale(bitmap);

                // 提取像素數據
                var width = grayBitmap.PixelWidth;
                var height = grayBitmap.PixelHeight;
                var stride = width; // Mono8 每像素 1 byte
                var pixels = new byte[height * stride];
                grayBitmap.CopyPixels(pixels, stride, 0);

                // 設定到模擬器
                var imageData = new ImageData(pixels, width, height, "Mono8");
                _simulator.SetSourceImage(imageData);

                // 更新 UI
                SimulatorSourcePreview = grayBitmap;
                SimulatorSourceWidth = width;
                SimulatorSourceHeight = height;
                SimulatorSourceInfo = $"{width} x {height} px";

                // 自動設定 ROI 預設值
                if (SimulatorAnchorX + SimulatorScanWidth > width)
                    SimulatorScanWidth = Math.Max(64, width - SimulatorAnchorX);
                if (SimulatorAnchorY >= height)
                    SimulatorAnchorY = 0;

                StatusMessage = $"✓ 來源圖片已載入: {Path.GetFileName(SimulatorSourceImagePath)}";

                // 更新 Command 狀態
                NotifySimulatorCommandsChanged();
            }
            catch (Exception ex)
            {
                StatusMessage = $"載入圖片失敗: {ex.Message}";
                SimulatorSourceImagePath = null;
                SimulatorSourcePreview = null;
            }
        }
    }

    private bool CanLoadSimulatorImage() => !IsSimulating && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStartSimulation))]
    private async Task StartSimulationAsync()
    {
        if (!ValidateSimulatorSettings(out var errorMessage))
        {
            StatusMessage = errorMessage;
            return;
        }

        IsBusy = true;
        StatusMessage = "正在啟動模擬...";

        try
        {
            var settings = new LineScanSimulatorSettings
            {
                AnchorX = SimulatorAnchorX,
                AnchorY = SimulatorAnchorY,
                ScanWidth = SimulatorScanWidth,
                ScanHeight = SimulatorScanHeight,
                LineRate = SimulatorLineRate,
                EnableTiming = SimulatorEnableTiming,
                SourceImagePath = SimulatorSourceImagePath ?? string.Empty
            };

            await _simulator.StartAsync(settings);

            IsSimulating = true;
            IsSimulatorPaused = false;
            TotalLines = SimulatorScanHeight;
            CurrentLine = 0;
            ScanProgress = 0;
            ProgressText = $"0 / {SimulatorScanHeight}";
            CompletedImages = 0;
            StatusMessage = "模擬中...";

            NotifySimulatorCommandsChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"啟動模擬失敗: {ex.Message}";
            IsSimulating = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStartSimulation() =>
        !IsBusy && !IsSimulating && SimulatorSourcePreview is not null;

    [RelayCommand(CanExecute = nameof(CanStopSimulation))]
    private async Task StopSimulationAsync()
    {
        IsBusy = true;
        StatusMessage = "正在停止模擬...";

        try
        {
            await _simulator.StopAsync();
            IsSimulating = false;
            IsSimulatorPaused = false;
            StatusMessage = $"模擬已停止，共完成 {CompletedImages} 張圖像";

            NotifySimulatorCommandsChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"停止模擬失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStopSimulation() => IsSimulating;

    [RelayCommand(CanExecute = nameof(CanPauseSimulation))]
    private void PauseSimulation()
    {
        _simulator.Pause();
        IsSimulatorPaused = true;
        StatusMessage = "模擬已暫停";
        NotifySimulatorCommandsChanged();
    }

    private bool CanPauseSimulation() => IsSimulating && !IsSimulatorPaused;

    [RelayCommand(CanExecute = nameof(CanResumeSimulation))]
    private void ResumeSimulation()
    {
        _simulator.Resume();
        IsSimulatorPaused = false;
        StatusMessage = "模擬繼續中...";
        NotifySimulatorCommandsChanged();
    }

    private bool CanResumeSimulation() => IsSimulating && IsSimulatorPaused;

    private void NotifySimulatorCommandsChanged()
    {
        LoadSimulatorImageCommand.NotifyCanExecuteChanged();
        StartSimulationCommand.NotifyCanExecuteChanged();
        StopSimulationCommand.NotifyCanExecuteChanged();
        PauseSimulationCommand.NotifyCanExecuteChanged();
        ResumeSimulationCommand.NotifyCanExecuteChanged();
    }

    private bool ValidateSimulatorSettings(out string errorMessage)
    {
        errorMessage = string.Empty;

        if (SimulatorSourcePreview is null)
        {
            errorMessage = "請先載入來源圖片";
            return false;
        }

        if (SimulatorAnchorX < 0)
        {
            errorMessage = "AnchorX 不能為負數";
            return false;
        }

        if (SimulatorAnchorY < 0)
        {
            errorMessage = "AnchorY 不能為負數";
            return false;
        }

        if (SimulatorScanWidth <= 0)
        {
            errorMessage = "掃描寬度必須大於 0";
            return false;
        }

        if (SimulatorScanHeight <= 0)
        {
            errorMessage = "掃描高度必須大於 0";
            return false;
        }

        if (SimulatorLineRate <= 0)
        {
            errorMessage = "線掃頻率必須大於 0";
            return false;
        }

        // 檢查 ROI 是否超出來源圖片範圍
        if (SimulatorAnchorX + SimulatorScanWidth > SimulatorSourceWidth)
        {
            errorMessage = $"ROI 超出圖片範圍 (AnchorX + ScanWidth = {SimulatorAnchorX + SimulatorScanWidth} > 圖片寬度 = {SimulatorSourceWidth})";
            return false;
        }

        if (SimulatorAnchorY >= SimulatorSourceHeight)
        {
            errorMessage = $"AnchorY 超出圖片範圍 (AnchorY = {SimulatorAnchorY} >= 圖片高度 = {SimulatorSourceHeight})";
            return false;
        }

        return true;
    }

    private static FormatConvertedBitmap ConvertToGrayscale(BitmapSource source)
    {
        return new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Gray8, null, 0);
    }

    // === 滑鼠事件處理 ===

    /// <summary>
    /// 處理滑鼠在影像上的移動事件
    /// </summary>
    /// <param name="position">滑鼠在 Image 控件上的位置</param>
    /// <param name="controlSize">Image 控件的實際大小</param>
    public void OnImageMouseMove(System.Windows.Point position, System.Windows.Size controlSize)
    {
        if (PreviewBitmap is null) return;

        // 計算縮放比例
        var scaleX = PreviewBitmap.PixelWidth / controlSize.Width;
        var scaleY = PreviewBitmap.PixelHeight / controlSize.Height;

        // 轉換為像素座標
        MouseX = Math.Clamp((int)(position.X * scaleX), 0, PreviewBitmap.PixelWidth - 1);
        MouseY = Math.Clamp((int)(position.Y * scaleY), 0, PreviewBitmap.PixelHeight - 1);
        MouseCoordinateText = $"X: {MouseX}, Y: {MouseY}";
    }

    /// <summary>
    /// 滑鼠離開影像區域
    /// </summary>
    public void OnImageMouseLeave()
    {
        MouseCoordinateText = "X: -, Y: -";
    }

    // === 私有方法 ===

    private void UpdateBoundsFromService()
    {
        _logger.LogInformation("  ──── UpdateBoundsFromService 開始 ────");
        var bounds = _lineScanService.Bounds;
        if (bounds is null)
        {
            _logger.LogWarning("  Bounds 是 null，跳過更新");
            return;
        }

        _logger.LogInformation("  Bounds 資訊:");
        _logger.LogInformation("    SensorSize: {W}x{H}", bounds.SensorWidth, bounds.SensorHeight);
        _logger.LogInformation("    OffsetXMax: {V}, OffsetYMax: {Y}", bounds.OffsetXMax, bounds.OffsetYMax);
        _logger.LogInformation("    Width: {Min}~{Max}", bounds.WidthMin, bounds.WidthMax);
        _logger.LogInformation("    LineRate: {Min}~{Max}", bounds.LineRateMin, bounds.LineRateMax);

        OffsetXMax = bounds.OffsetXMax;
        OffsetYMax = bounds.OffsetYMax;
        WidthMin = bounds.WidthMin;
        WidthMax = bounds.WidthMax;
        LineRateMin = bounds.LineRateMin;
        LineRateMax = bounds.LineRateMax;
        SensorWidth = bounds.SensorWidth;
        SensorHeight = bounds.SensorHeight;

        // 同時更新相機參數邊界
        _logger.LogInformation("  呼叫 UpdateCameraParameterBoundsAsync()...");
        _ = UpdateCameraParameterBoundsAsync();
        _logger.LogInformation("  ──── UpdateBoundsFromService 結束 ────");
    }

    private async Task UpdateCameraParameterBoundsAsync()
    {
        _logger.LogInformation("  ──── UpdateCameraParameterBoundsAsync 開始 ────");
        try
        {
            _logger.LogInformation("    呼叫 _cameraControl.GetParametersAsync()...");
            _logger.LogInformation("    ICameraControlPort 類型: {Type}", _cameraControl.GetType().FullName);
            var parameters = await _cameraControl.GetParametersAsync(default);
            _logger.LogInformation("    取得 {Count} 個參數", parameters.Count);

            foreach (var param in parameters)
            {
                _logger.LogInformation("    參數: {Kind} = {Value} (範圍: {Min}~{Max})",
                    param.Kind, param.Value, param.Minimum, param.Maximum);

                switch (param.Kind)
                {
                    case CameraParameterKind.ExposureTime:
                        ExposureTimeMin = param.Minimum;
                        ExposureTimeMax = param.Maximum;
                        ExposureTime = param.Value;
                        break;
                    case CameraParameterKind.Gain:
                        GainMin = param.Minimum;
                        GainMax = param.Maximum;
                        Gain = param.Value;
                        break;
                    case CameraParameterKind.Height:
                        HeightMax = (long)param.Maximum;
                        break;
                    case CameraParameterKind.AcquisitionLineRate:
                        LineRateMin = param.Minimum;
                        LineRateMax = param.Maximum;
                        // 如果目前設定值超出範圍，自動修正
                        if (LineRate < param.Minimum) LineRate = param.Minimum;
                        if (LineRate > param.Maximum) LineRate = param.Maximum;
                        break;
                }
            }
            _logger.LogInformation("  ──── UpdateCameraParameterBoundsAsync 完成 ────");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "    UpdateCameraParameterBoundsAsync 發生例外");
            StatusMessage = $"讀取相機參數失敗: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyCameraParameters))]
    private async Task ApplyCameraParametersAsync()
    {
        _logger.LogInformation("──────── ApplyCameraParametersAsync 開始 ────────");
        _logger.LogInformation("  參數: ExposureTime={Exposure}, Gain={Gain}, LineRate={LineRate}",
            ExposureTime, Gain, LineRate);

        IsBusy = true;
        StatusMessage = "正在套用相機參數...";

        try
        {
            _logger.LogInformation("  設定 ExposureTime...");
            await _cameraControl.SetParameterAsync(CameraParameterKind.ExposureTime, ExposureTime, default);

            _logger.LogInformation("  設定 Gain...");
            await _cameraControl.SetParameterAsync(CameraParameterKind.Gain, Gain, default);

            // 只有在 LineRate 有效時才設定
            if (LineRate > 0)
            {
                _logger.LogInformation("  設定 AcquisitionLineRate...");
                try
                {
                    await _cameraControl.SetParameterAsync(CameraParameterKind.AcquisitionLineRate, LineRate, default);
                }
                catch (Exception lineRateEx)
                {
                    _logger.LogWarning(lineRateEx, "  設定 LineRate 失敗（Area Scan 模式下可能不支援）");
                }
            }

            StatusMessage = $"✓ 相機參數已套用 (曝光: {(int)ExposureTime}µs, 增益: {(int)Gain})";
            _logger.LogInformation("  ✓ 參數套用完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "  套用參數失敗");
            StatusMessage = $"套用相機參數失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _logger.LogInformation("──────── ApplyCameraParametersAsync 結束 ────────");
        }
    }

    private bool CanApplyCameraParameters()
    {
        var canExecute = IsCameraConnected && !IsBusy && !IsScanning;
        _logger.LogDebug("CanApplyCameraParameters: {CanExecute} (Connected={Connected}, Busy={Busy}, Scanning={Scanning})",
            canExecute, IsCameraConnected, IsBusy, IsScanning);
        return canExecute;
    }

    private bool ValidateSettings(out string errorMessage)
    {
        errorMessage = string.Empty;

        if (OffsetX < 0)
        {
            errorMessage = "OffsetX 不能為負數";
            return false;
        }

        if (OffsetY < 0)
        {
            errorMessage = "OffsetY 不能為負數";
            return false;
        }

        if (Width <= 0)
        {
            errorMessage = "寬度必須大於 0";
            return false;
        }

        if (TargetHeight <= 0)
        {
            errorMessage = "目標高度必須大於 0";
            return false;
        }

        if (LineRate <= 0)
        {
            errorMessage = "線掃頻率必須大於 0";
            return false;
        }

        // 檢查 ROI 是否超出感測器範圍
        if (OffsetX + Width > SensorWidth)
        {
            errorMessage = $"ROI 超出感測器範圍 (OffsetX + Width = {OffsetX + Width} > SensorWidth = {SensorWidth})";
            return false;
        }

        return true;
    }

    private void OnLineReceived(object? sender, LineScanLineEventArgs e)
    {
        // 硬體累積模式下，不會收到逐行事件
        // 此事件保留給模擬器使用
        var now = DateTime.UtcNow;
        if ((now - _lastUiUpdate).TotalMilliseconds < UI_UPDATE_INTERVAL_MS)
        {
            return;
        }

        _lastUiUpdate = now;

        _dispatcher.BeginInvoke(() =>
        {
            CurrentLine = e.LineIndex;
            ScanProgress = e.ProgressPercent;
            ProgressText = $"{e.LineIndex} / {e.TotalLines}";
        });
    }

    private void OnImageCompleted(object? sender, LineScanImageEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            ResultBitmap = BitmapSourceFactory.FromImageData(e.Image);
            CompletedImages = e.ImageIndex + 1;

            // 硬體累積模式：每次收到完整影像，進度直接顯示 100%
            CurrentLine = TotalLines;
            ScanProgress = 100;
            ProgressText = $"已完成 {CompletedImages} 張";

            // 自動儲存影像
            string? savedPath = null;
            if (AutoSaveImages)
            {
                savedPath = SaveImageToFile(e.Image, CompletedImages);
            }

            if (savedPath != null)
            {
                StatusMessage = $"✓ 圖像 #{CompletedImages} 已儲存: {Path.GetFileName(savedPath)}";
            }
            else
            {
                StatusMessage = $"✓ 圖像 #{CompletedImages} 完成 ({e.Image.Width}x{e.Image.Height}), 行頻: {e.ActualLineRate:F0} Hz";
            }

            // 如果啟用連續掃描且還在掃描狀態，觸發下一次取像
            if (ContinuousScan && IsScanning)
            {
                _logger.LogDebug("連續掃描模式：觸發下一次取像");
                _ = TriggerCaptureAsync();
            }
            else if (!ContinuousScan && IsScanning)
            {
                // 單張模式：完成一張後自動停止
                _logger.LogInformation("單張模式：已完成 1 張，自動停止掃描");
                _ = StopScanAsync();
            }
        });
    }

    private string? SaveImageToFile(ImageData image, int imageIndex)
    {
        try
        {
            // 確保儲存資料夾存在
            if (!Directory.Exists(SaveFolder))
            {
                Directory.CreateDirectory(SaveFolder);
                _logger.LogInformation("建立儲存資料夾: {Folder}", SaveFolder);
            }

            // 產生檔名: LineScan_yyyyMMdd_HHmmss_001.png
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"LineScan_{timestamp}_{imageIndex:D3}.png";
            var filePath = Path.Combine(SaveFolder, fileName);

            // 轉換為 BitmapSource 並儲存
            var bitmapSource = BitmapSourceFactory.FromImageData(image);
            using var fileStream = new FileStream(filePath, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            encoder.Save(fileStream);

            _logger.LogInformation("影像已儲存: {FilePath} ({Width}x{Height})", filePath, image.Width, image.Height);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "儲存影像失敗");
            return null;
        }
    }

    private void OnScanError(object? sender, LineScanErrorEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            StatusMessage = $"錯誤: {e.Message}";

            if (e.IsFatal)
            {
                IsScanning = false;
                CapturePreviewCommand.NotifyCanExecuteChanged();
                StartScanCommand.NotifyCanExecuteChanged();
                StopScanCommand.NotifyCanExecuteChanged();
            }
        });
    }

    // === 模擬器事件處理 ===

    private void OnSimulatorLineReceived(object? sender, LineScanLineEventArgs e)
    {
        // 節流 UI 更新
        var now = DateTime.UtcNow;
        if ((now - _lastUiUpdate).TotalMilliseconds < UI_UPDATE_INTERVAL_MS)
        {
            return;
        }

        _lastUiUpdate = now;

        _dispatcher.BeginInvoke(() =>
        {
            CurrentLine = e.LineIndex;
            ScanProgress = (double)e.LineIndex / e.TotalLines * 100;
            ProgressText = $"{e.LineIndex} / {e.TotalLines}";
        });
    }

    private void OnSimulatorImageCompleted(object? sender, LineScanImageEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            ResultBitmap = BitmapSourceFactory.FromImageData(e.Image);
            CompletedImages = e.ImageIndex + 1;
            CurrentLine = 0;
            ScanProgress = 100;
            ProgressText = $"{TotalLines} / {TotalLines}";
            StatusMessage = $"✓ 模擬圖像 #{CompletedImages} 完成 ({e.Image.Width}x{e.Image.Height}), 實際行頻: {e.ActualLineRate:F0} Hz, 耗時: {e.ElapsedTime.TotalSeconds:F2}s";

            // 模擬完成後自動更新狀態
            IsSimulating = false;
            IsSimulatorPaused = false;
            NotifySimulatorCommandsChanged();
        });
    }

    private void OnSimulatorError(object? sender, LineScanErrorEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            StatusMessage = $"模擬器錯誤: {e.Message}";

            if (e.IsFatal)
            {
                IsSimulating = false;
                IsSimulatorPaused = false;
                NotifySimulatorCommandsChanged();
            }
        });
    }

    // === 設定儲存/載入 ===

    /// <summary>
    /// 儲存目前的設定到 JSON 檔案
    /// </summary>
    [RelayCommand]
    private void SaveSettings()
    {
        var settings = new LineScanUserSettings
        {
            OffsetX = OffsetX,
            OffsetY = OffsetY,
            Width = Width,
            TargetHeight = TargetHeight,
            LineRate = LineRate,
            ExposureTime = ExposureTime,
            Gain = Gain,
            UserSetName = SelectedUserSet,
            AutoSaveImages = AutoSaveImages,
            SaveFolder = SaveFolder,
            ContinuousScan = ContinuousScan
        };

        if (settings.Save())
        {
            _logger.LogInformation("Line Scan 設定已儲存: {FilePath}", LineScanUserSettings.DefaultFilePath);
            StatusMessage = $"✓ 設定已儲存至 {Path.GetFileName(LineScanUserSettings.DefaultFilePath)}";
        }
        else
        {
            _logger.LogWarning("儲存 Line Scan 設定失敗");
            StatusMessage = "✗ 儲存設定失敗";
        }
    }

    /// <summary>
    /// 從 JSON 檔案載入設定
    /// </summary>
    [RelayCommand]
    private void LoadSettings()
    {
        var settings = LineScanUserSettings.Load();
        if (settings != null)
        {
            ApplySettings(settings);
            _logger.LogInformation("Line Scan 設定已載入: {FilePath}", LineScanUserSettings.DefaultFilePath);
            StatusMessage = $"✓ 設定已從 {Path.GetFileName(LineScanUserSettings.DefaultFilePath)} 載入";
        }
        else
        {
            _logger.LogWarning("載入 Line Scan 設定失敗，檔案可能不存在");
            StatusMessage = "✗ 載入設定失敗 (檔案不存在或格式錯誤)";
        }
    }

    /// <summary>
    /// 套用設定到 ViewModel 屬性
    /// </summary>
    private void ApplySettings(LineScanUserSettings settings)
    {
        OffsetX = settings.OffsetX;
        OffsetY = settings.OffsetY;
        Width = settings.Width;
        TargetHeight = settings.TargetHeight;
        LineRate = settings.LineRate;
        ExposureTime = settings.ExposureTime;
        Gain = settings.Gain;
        SelectedUserSet = settings.UserSetName;
        AutoSaveImages = settings.AutoSaveImages;
        SaveFolder = settings.SaveFolder;
        ContinuousScan = settings.ContinuousScan;

        _logger.LogInformation("  OffsetX={OffsetX}, OffsetY={OffsetY}, Width={Width}, Height={Height}",
            OffsetX, OffsetY, Width, TargetHeight);
        _logger.LogInformation("  LineRate={LineRate} Hz, Exposure={Exposure} µs, Gain={Gain}, UserSet={UserSet}",
            LineRate, ExposureTime, Gain, SelectedUserSet);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _lineScanService.LineReceived -= OnLineReceived;
        _lineScanService.ImageCompleted -= OnImageCompleted;
        _lineScanService.ScanError -= OnScanError;

        _simulator.LineReceived -= OnSimulatorLineReceived;
        _simulator.ImageCompleted -= OnSimulatorImageCompleted;
        _simulator.SimulatorError -= OnSimulatorError;

        if (_simulator is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _disposed = true;
    }
}
