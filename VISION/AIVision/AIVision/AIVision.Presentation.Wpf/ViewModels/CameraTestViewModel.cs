using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIVision.Application.Contracts.Camera;
using AIVision.Domain.Shared;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using peak.core;
using peak.core.nodes;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class CameraTestViewModel : ObservableObject
{
    private readonly ILogger<CameraTestViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly string _sdkPath;
    private readonly StringBuilder _logBuilder = new();

    // IDS Peak SDK objects
    private Device? _device;
    private NodeMap? _nodeMap;
    private DataStream? _dataStream;

    // Debounce timers and cancellation tokens for parameter changes
    private System.Timers.Timer? _exposureDebounceTimer;
    private System.Timers.Timer? _gainDebounceTimer;
    private System.Timers.Timer? _heightDebounceTimer;
    private System.Timers.Timer? _lineRateDebounceTimer;
    private readonly SemaphoreSlim _parameterLock = new(1, 1);

    // Acquisition control
    private CancellationTokenSource? _acquisitionCts;
    private Task? _acquisitionTask;
    private bool _isAcquiring = false;

    // Observable Properties
    [ObservableProperty] private BitmapSource? _frameBitmap;
    [ObservableProperty] private string _statusText = "尚未開啟相機";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _canOpen = true;
    [ObservableProperty] private string _savePath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    [ObservableProperty] private bool _isLinescanMode = false;
    [ObservableProperty] private bool _linescanSupported = false;

    // Exposure Time (internal in µs, display in ms)
    [ObservableProperty] private double _exposureTime = 15000;
    [ObservableProperty] private double _exposureMin = 13;
    [ObservableProperty] private double _exposureMax = 1000000;
    [ObservableProperty] private double _exposureIncrement = 1;
    [ObservableProperty] private string _exposureRange = "";

    // Exposure Time in milliseconds (for display)
    public double ExposureTimeMs
    {
        get => ExposureTime / 1000.0;
        set
        {
            var newValueUs = value * 1000.0;
            if (Math.Abs(ExposureTime - newValueUs) > 0.001)
            {
                ExposureTime = newValueUs;
                OnPropertyChanged();
            }
        }
    }

    public double ExposureMinMs => ExposureMin / 1000.0;
    public double ExposureMaxMs => ExposureMax / 1000.0;
    public double ExposureIncrementMs => ExposureIncrement / 1000.0;
    public string ExposureRangeMs => $"{ExposureMinMs:F2}~{ExposureMaxMs:F0}";

    partial void OnExposureTimeChanged(double value)
    {
        OnPropertyChanged(nameof(ExposureTimeMs));
        if (IsOpen)
            _ = SetExposureTimeAsync();
    }

    partial void OnExposureMinChanged(double value)
    {
        OnPropertyChanged(nameof(ExposureMinMs));
        OnPropertyChanged(nameof(ExposureRangeMs));
    }

    partial void OnExposureMaxChanged(double value)
    {
        OnPropertyChanged(nameof(ExposureMaxMs));
        OnPropertyChanged(nameof(ExposureRangeMs));
    }

    partial void OnExposureIncrementChanged(double value)
    {
        OnPropertyChanged(nameof(ExposureIncrementMs));
    }

    // Gain
    [ObservableProperty] private double _gain = 0;
    [ObservableProperty] private double _gainMin = 0;
    [ObservableProperty] private double _gainMax = 36;
    [ObservableProperty] private double _gainIncrement = 0.1;
    [ObservableProperty] private string _gainRange = "";

    partial void OnGainChanged(double value)
    {
        if (IsOpen)
            _ = SetGainAsync();
    }

    // Height
    [ObservableProperty] private long _height = 1024;
    [ObservableProperty] private long _heightMin = 2;
    [ObservableProperty] private long _heightMax = 2076;
    [ObservableProperty] private long _heightIncrement = 2;
    [ObservableProperty] private string _heightRange = "";

    partial void OnHeightChanged(long value)
    {
        if (IsOpen)
            _ = SetHeightAsync();
    }

    // Line Rate (Linescan only)
    [ObservableProperty] private double _lineRate = 1000;
    [ObservableProperty] private double _lineRateMin = 1;
    [ObservableProperty] private double _lineRateMax = 10000;
    [ObservableProperty] private double _lineRateIncrement = 1;
    [ObservableProperty] private string _lineRateRange = "";
    [ObservableProperty] private bool _lineRateAvailable = false;

    partial void OnLineRateChanged(double value)
    {
        if (IsOpen && LineRateAvailable)
            _ = SetLineRateAsync();
    }

    public CameraTestViewModel(ILogger<CameraTestViewModel> logger, IMessenger messenger)
    {
        _logger = logger;
        _messenger = messenger;
        _sdkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs", "ids_peak");
        InitializeSDK();
    }

    private void InitializeSDK()
    {
        try
        {
            if (Directory.Exists(_sdkPath))
            {
                Environment.SetEnvironmentVariable("PATH",
                    _sdkPath + ";" + Environment.GetEnvironmentVariable("PATH"));
            }

            peak.Library.Initialize();
            LogMessage("✓ IDS Peak SDK 初始化成功");
        }
        catch (Exception ex)
        {
            LogMessage($"✗ SDK 初始化失敗: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task OpenCameraAsync()
    {
        try
        {
            LogMessage("正在開啟相機...");

            // Update device list
            peak.DeviceManager.Instance().Update();
            var devices = peak.DeviceManager.Instance().Devices();

            if (devices.Count == 0)
            {
                LogMessage("✗ 未找到相機");
                return;
            }

            var deviceDescriptor = devices[0];
            LogMessage($"找到相機: {deviceDescriptor.ModelName()}");

            // Open device
            _device = deviceDescriptor.OpenDevice(DeviceAccessType.Exclusive);
            _nodeMap = _device.RemoteDevice().NodeMaps()[0];

            LogMessage("✓ 相機已開啟");

            // 讀取參數範圍
            await ReadParameterRangesAsync();

            // 啟動連續預覽
            await StartContinuousAcquisitionAsync();

            IsOpen = true;
            CanOpen = false;
            StatusText = "相機預覽中";
        }
        catch (Exception ex)
        {
            LogMessage($"✗ 開啟相機失敗: {ex.Message}");
            _logger.LogError(ex, "開啟相機失敗");
        }
    }

    [RelayCommand]
    private async Task CloseCameraAsync()
    {
        try
        {
            LogMessage("正在關閉相機...");

            // Stop continuous acquisition
            await StopContinuousAcquisitionAsync();

            // Stop and dispose debounce timers
            _exposureDebounceTimer?.Stop();
            _exposureDebounceTimer?.Dispose();
            _exposureDebounceTimer = null;

            _gainDebounceTimer?.Stop();
            _gainDebounceTimer?.Dispose();
            _gainDebounceTimer = null;

            _heightDebounceTimer?.Stop();
            _heightDebounceTimer?.Dispose();
            _heightDebounceTimer = null;

            _lineRateDebounceTimer?.Stop();
            _lineRateDebounceTimer?.Dispose();
            _lineRateDebounceTimer = null;

            _dataStream?.Dispose();
            _dataStream = null;

            _device?.Dispose();
            _device = null;
            _nodeMap = null;

            IsOpen = false;
            CanOpen = true;
            StatusText = "相機已關閉";
            FrameBitmap = null;

            LogMessage("✓ 相機已關閉");
        }
        catch (Exception ex)
        {
            LogMessage($"✗ 關閉相機失敗: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private void SelectSavePath()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "選擇儲存位置",
            FileName = "Capture.png",
            DefaultExt = ".png",
            Filter = "PNG 影像|*.png|所有檔案|*.*",
            InitialDirectory = SavePath
        };

        if (dialog.ShowDialog() == true)
        {
            SavePath = Path.GetDirectoryName(dialog.FileName) ?? SavePath;
            LogMessage($"✓ 儲存路徑已設定: {SavePath}");
        }
    }

    [RelayCommand]
    private async Task CaptureImageAsync()
    {
        if (FrameBitmap == null)
        {
            LogMessage("✗ 無可儲存的影像");
            return;
        }

        try
        {
            LogMessage("正在儲存照片...");

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var filename = $"Capture_{timestamp}.png";
            var filepath = Path.Combine(SavePath, filename);

            // Ensure directory exists
            Directory.CreateDirectory(SavePath);

            // Save to file
            using var fileStream = new FileStream(filepath, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(FrameBitmap));
            encoder.Save(fileStream);

            LogMessage($"✓ 照片已儲存: {filepath}");
        }
        catch (Exception ex)
        {
            LogMessage($"✗ 儲存照片失敗: {ex.Message}");
            _logger.LogError(ex, "儲存照片失敗");
        }

        await Task.CompletedTask;
    }

    private async Task SetOptimalLinescanParametersAsync()
    {
        if (_nodeMap == null || !IsLinescanMode) return;

        try
        {
            LogMessage("正在設定 Linescan 最佳參數...");

            // 1. 先設定較短的曝光時間 (5ms) 以支援較高的 LineRate
            var exposureNode = _nodeMap.FindNode<FloatNode>("ExposureTime");
            if (exposureNode != null && exposureNode.IsWriteable())
            {
                var targetExposureUs = 5000.0; // 5ms in microseconds
                var minExposure = exposureNode.Minimum();
                var maxExposure = exposureNode.Maximum();

                targetExposureUs = Math.Clamp(targetExposureUs, minExposure, maxExposure);
                exposureNode.SetValue(targetExposureUs);
                ExposureTime = exposureNode.Value();
                LogMessage($"✓ 曝光時間已設定為: {ExposureTimeMs:F2} ms");
            }

            // 2. 等待曝光時間生效後，設定 LineRate
            await Task.Delay(200);

            // 3. 設定目標 LineRate (200 Hz)
            var lineRateNode = _nodeMap.FindNode<FloatNode>("AcquisitionLineRate");
            if (lineRateNode != null && lineRateNode.IsWriteable())
            {
                // 重新讀取 LineRate 範圍（因為曝光時間改變了）
                LineRateMin = lineRateNode.Minimum();
                LineRateMax = lineRateNode.Maximum();
                LineRateIncrement = lineRateNode.HasConstantIncrement() ? lineRateNode.Increment() : 1;
                LineRateRange = $"範圍: {LineRateMin:F2} ~ {LineRateMax:F2} Hz";

                // 設定目標 LineRate (200 Hz 或最大值，取較小者)
                var targetLineRate = Math.Min(200.0, LineRateMax);

                // Disable LineStart trigger before setting LineRate
                var triggerSelector = _nodeMap.FindNode<EnumerationNode>("TriggerSelector");
                var triggerMode = _nodeMap.FindNode<EnumerationNode>("TriggerMode");

                if (triggerSelector != null && triggerMode != null &&
                    triggerSelector.IsWriteable() && triggerMode.IsWriteable())
                {
                    if (triggerSelector.HasEntry("LineStart"))
                    {
                        triggerSelector.SetCurrentEntry("LineStart");
                        if (triggerMode.HasEntry("Off"))
                        {
                            triggerMode.SetCurrentEntry("Off");
                        }
                    }
                }

                lineRateNode.SetValue(targetLineRate);
                LineRate = lineRateNode.Value();
                LineRateAvailable = true;
                LogMessage($"✓ 行掃描頻率已設定為: {LineRate:F2} Hz (範圍: {LineRateMin:F2}~{LineRateMax:F2})");
            }

            // 4. 設定 Height 為 1024
            var heightNode = _nodeMap.FindNode<IntegerNode>("Height");
            if (heightNode != null && heightNode.IsWriteable())
            {
                var targetHeight = 1024L;
                var minHeight = heightNode.Minimum();
                var maxHeight = heightNode.Maximum();

                targetHeight = Math.Clamp(targetHeight, minHeight, maxHeight);
                heightNode.SetValue(targetHeight);
                Height = heightNode.Value();
                LogMessage($"✓ 影像高度已設定為: {Height} px");
            }

            LogMessage("✓ Linescan 參數設定完成 (曝光: 5ms, LineRate: 200Hz, 高度: 1024px)");
        }
        catch (Exception ex)
        {
            LogMessage($"✗ 設定 Linescan 參數失敗: {ex.Message}");
            _logger.LogError(ex, "設定 Linescan 最佳參數失敗");
        }
    }

    [RelayCommand]
    private async Task ToggleLinescanModeAsync()
    {
        if (_nodeMap == null || !IsOpen)
        {
            LogMessage("✗ 請先開啟相機");
            return;
        }

        try
        {
            LogMessage(IsLinescanMode ? "正在切換到面陣模式..." : "正在切換到線掃描模式...");

            // 停止當前採集
            await StopContinuousAcquisitionAsync();

            // 解鎖 TLParams (必要!)
            var tlParamsLocked = _nodeMap.FindNode<IntegerNode>("TLParamsLocked");
            if (tlParamsLocked != null && tlParamsLocked.IsWriteable())
            {
                tlParamsLocked.SetValue(0);
                LogMessage("✓ TLParamsLocked 已解鎖");
            }

            // 切換 User Set
            var userSetSelector = _nodeMap.FindNode<EnumerationNode>("UserSetSelector");
            var userSetLoad = _nodeMap.FindNode<CommandNode>("UserSetLoad");

            if (userSetSelector != null && userSetLoad != null)
            {
                // Find the appropriate UserSet
                string? targetUserSet = null;

                if (IsLinescanMode)
                {
                    // Switching to Area Scan mode - try Default or UserSet1
                    if (userSetSelector.HasEntry("Default"))
                        targetUserSet = "Default";
                    else if (userSetSelector.HasEntry("UserSet1"))
                        targetUserSet = "UserSet1";
                }
                else
                {
                    // Switching to Linescan mode - try UserSet2, linescan, or Linescan
                    if (userSetSelector.HasEntry("UserSet2"))
                        targetUserSet = "UserSet2";
                    else if (userSetSelector.HasEntry("linescan"))
                        targetUserSet = "linescan";
                    else if (userSetSelector.HasEntry("Linescan"))
                        targetUserSet = "Linescan";
                }

                if (targetUserSet != null && userSetSelector.IsWriteable())
                {
                    userSetSelector.SetCurrentEntry(targetUserSet);
                    LogMessage($"正在載入 {targetUserSet} UserSet...");

                    if (userSetLoad.IsWriteable())
                    {
                        userSetLoad.Execute();
                        userSetLoad.WaitUntilDone();

                        LogMessage($"✓ 已載入 {targetUserSet} UserSet");

                        // 檢查切換後的狀態
                        await Task.Delay(500); // 等待設定生效
                        CheckLinescanMode();

                        // 重新讀取參數範圍
                        await ReadParameterRangesAsync();

                        // 如果切換到 Linescan 模式，設定最佳初始參數
                        if (IsLinescanMode)
                        {
                            await SetOptimalLinescanParametersAsync();
                        }

                        // 重新啟動採集
                        await StartContinuousAcquisitionAsync();

                        LogMessage(IsLinescanMode ? "✓ 已切換到線掃描模式" : "✓ 已切換到面陣模式");
                    }
                    else
                    {
                        LogMessage("✗ UserSetLoad 節點無法寫入");
                    }
                }
                else
                {
                    // Log available UserSets for debugging
                    var entries = userSetSelector.Entries();
                    var availableUserSets = string.Join(", ", entries.Select(e => e.SymbolicValue()));
                    LogMessage($"✗ 找不到合適的 UserSet。可用的 UserSets: {availableUserSets}");
                }
            }
            else
            {
                LogMessage("✗ 相機不支援 UserSet 切換");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"✗ 切換模式失敗: {ex.Message}");
            _logger.LogError(ex, "切換 Linescan 模式失敗");
        }
    }

    private void CheckLinescanMode()
    {
        if (_nodeMap == null) return;

        try
        {
            // Check SensorOperationMode (official way to detect Linescan mode)
            var sensorMode = _nodeMap.FindNode<EnumerationNode>("SensorOperationMode");

            if (sensorMode != null && sensorMode.IsReadable())
            {
                var currentMode = sensorMode.CurrentEntry().SymbolicValue();
                IsLinescanMode = (currentMode == "Linescan");

                LogMessage($"✓ 偵測到感測器模式: {currentMode}");

                // Check if Linescan UserSet is available
                var userSetSelector = _nodeMap.FindNode<EnumerationNode>("UserSetSelector");
                if (userSetSelector != null)
                {
                    var entries = userSetSelector.Entries();
                    var hasLinescan = false;

                    foreach (var entry in entries)
                    {
                        if (entry.SymbolicValue() == "UserSet2" || entry.DisplayName().Contains("Linescan"))
                        {
                            hasLinescan = true;
                            break;
                        }
                    }

                    LinescanSupported = hasLinescan;

                    if (hasLinescan)
                    {
                        LogMessage(IsLinescanMode
                            ? "✓ 線掃描模式已啟用"
                            : "✓ 相機支援線掃描模式 (目前為面陣模式)");
                    }
                    else
                    {
                        LogMessage("此相機不支援線掃描模式");
                    }
                }
            }
            else
            {
                // Fallback: check if camera has AcquisitionLineRate parameter
                var lineRateNode = _nodeMap.FindNode<FloatNode>("AcquisitionLineRate");
                IsLinescanMode = (lineRateNode != null);
                LinescanSupported = IsLinescanMode;

                if (IsLinescanMode)
                {
                    LogMessage("✓ 偵測到線掃描參數");
                }
                else
                {
                    LogMessage("此相機不支援線掃描模式");
                }
            }
        }
        catch (Exception ex)
        {
            LinescanSupported = false;
            IsLinescanMode = false;
            _logger.LogWarning(ex, "檢查 Linescan 模式失敗");
        }
    }

    private async Task ReadParameterRangesAsync()
    {
        if (_nodeMap == null) return;

        await Task.Run(() =>
        {
            try
            {
                // 檢查 Linescan 模式
                CheckLinescanMode();

                // Read Exposure Time Range
                var exposureNode = _nodeMap.FindNode<FloatNode>("ExposureTime");
                if (exposureNode != null && exposureNode.IsReadable())
                {
                    ExposureMin = exposureNode.Minimum();
                    ExposureMax = exposureNode.Maximum();
                    ExposureIncrement = exposureNode.HasConstantIncrement() ? exposureNode.Increment() : 1;
                    ExposureTime = exposureNode.Value();
                    ExposureRange = $"範圍: {ExposureMin:F2} ~ {ExposureMax:F2} µs";
                    LogMessage($"曝光時間範圍: {ExposureMinMs:F2} ~ {ExposureMaxMs:F0} ms");
                }

                // Read Gain Range
                var gainNode = _nodeMap.FindNode<FloatNode>("Gain");
                if (gainNode != null && gainNode.IsReadable())
                {
                    GainMin = gainNode.Minimum();
                    GainMax = gainNode.Maximum();
                    GainIncrement = gainNode.HasConstantIncrement() ? gainNode.Increment() : 0.1;
                    Gain = gainNode.Value();
                    GainRange = $"範圍: {GainMin:F2} ~ {GainMax:F2}";
                    LogMessage($"增益範圍: {GainMin:F2} ~ {GainMax:F2}");
                }

                // Read Height Range
                var heightNode = _nodeMap.FindNode<IntegerNode>("Height");
                if (heightNode != null && heightNode.IsReadable())
                {
                    HeightMin = heightNode.Minimum();
                    HeightMax = heightNode.Maximum();
                    HeightIncrement = 2; // Integer nodes typically have increment of 2 for IDS cameras
                    Height = heightNode.Value();
                    HeightRange = $"範圍: {HeightMin} ~ {HeightMax} px";
                    LogMessage($"高度範圍: {HeightMin} ~ {HeightMax} px");
                }

                // Read Line Rate Range (if in Linescan mode)
                if (IsLinescanMode)
                {
                    var lineRateNode = _nodeMap.FindNode<FloatNode>("AcquisitionLineRate");
                    if (lineRateNode != null && lineRateNode.IsReadable())
                    {
                        LineRateMin = lineRateNode.Minimum();
                        LineRateMax = lineRateNode.Maximum();
                        LineRateIncrement = lineRateNode.HasConstantIncrement() ? lineRateNode.Increment() : 1;
                        LineRate = lineRateNode.Value();
                        LineRateRange = $"範圍: {LineRateMin:F2} ~ {LineRateMax:F2} Hz";
                        LineRateAvailable = true;
                        LogMessage($"行掃描頻率範圍: {LineRateMin:F2} ~ {LineRateMax:F2} Hz");
                    }
                    else
                    {
                        LineRateAvailable = false;
                        LogMessage("⚠ 線掃描模式下找不到 AcquisitionLineRate 節點");
                    }
                }
                else
                {
                    LineRateAvailable = false;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"讀取參數範圍失敗: {ex.Message}");
            }
        });
    }

    private async Task SetExposureTimeAsync()
    {
        if (_nodeMap == null || !IsOpen) return;

        // Debounce: reset timer on each call
        _exposureDebounceTimer?.Stop();
        _exposureDebounceTimer = new System.Timers.Timer(300); // 300ms delay
        _exposureDebounceTimer.Elapsed += async (s, e) =>
        {
            _exposureDebounceTimer?.Stop();
            await _parameterLock.WaitAsync();
            try
            {
                var node = _nodeMap.FindNode<FloatNode>("ExposureTime");
                if (node != null && node.IsWriteable())
                {
                    var value = Math.Clamp(ExposureTime, ExposureMin, ExposureMax);
                    node.SetValue(value);
                    ExposureTime = node.Value();
                    LogMessage($"✓ 曝光時間已設定: {ExposureTimeMs:F2} ms");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"設定曝光時間失敗: {ex.Message}");
            }
            finally
            {
                _parameterLock.Release();
            }
        };
        _exposureDebounceTimer.AutoReset = false;
        _exposureDebounceTimer.Start();
        await Task.CompletedTask;
    }

    private async Task SetGainAsync()
    {
        if (_nodeMap == null || !IsOpen) return;

        // Debounce: reset timer on each call
        _gainDebounceTimer?.Stop();
        _gainDebounceTimer = new System.Timers.Timer(300); // 300ms delay
        _gainDebounceTimer.Elapsed += async (s, e) =>
        {
            _gainDebounceTimer?.Stop();
            await _parameterLock.WaitAsync();
            try
            {
                var node = _nodeMap.FindNode<FloatNode>("Gain");
                if (node != null && node.IsWriteable())
                {
                    var value = Math.Clamp(Gain, GainMin, GainMax);
                    node.SetValue(value);
                    Gain = node.Value();
                    LogMessage($"✓ 增益已設定: {Gain:F2}");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"設定增益失敗: {ex.Message}");
            }
            finally
            {
                _parameterLock.Release();
            }
        };
        _gainDebounceTimer.AutoReset = false;
        _gainDebounceTimer.Start();
        await Task.CompletedTask;
    }

    private async Task SetHeightAsync()
    {
        if (_nodeMap == null || !IsOpen) return;

        // Debounce: reset timer on each call
        _heightDebounceTimer?.Stop();
        _heightDebounceTimer = new System.Timers.Timer(300); // 300ms delay
        _heightDebounceTimer.Elapsed += async (s, e) =>
        {
            _heightDebounceTimer?.Stop();
            await _parameterLock.WaitAsync();
            try
            {
                var node = _nodeMap.FindNode<IntegerNode>("Height");
                if (node != null && node.IsWriteable())
                {
                    var value = Math.Clamp(Height, HeightMin, HeightMax);
                    // Align to increment
                    if (HeightIncrement > 1)
                        value = HeightMin + ((value - HeightMin) / HeightIncrement) * HeightIncrement;

                    node.SetValue(value);
                    Height = node.Value();
                    LogMessage($"✓ 高度已設定: {Height} px");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"設定高度失敗: {ex.Message}");
            }
            finally
            {
                _parameterLock.Release();
            }
        };
        _heightDebounceTimer.AutoReset = false;
        _heightDebounceTimer.Start();
        await Task.CompletedTask;
    }

    private async Task SetLineRateAsync()
    {
        if (_nodeMap == null || !LineRateAvailable || !IsOpen) return;

        // Debounce: reset timer on each call
        _lineRateDebounceTimer?.Stop();
        _lineRateDebounceTimer = new System.Timers.Timer(300); // 300ms delay
        _lineRateDebounceTimer.Elapsed += async (s, e) =>
        {
            _lineRateDebounceTimer?.Stop();
            await _parameterLock.WaitAsync();
            try
            {
                // 先關閉 LineStart 觸發,確保可寫入
                var triggerSelector = _nodeMap.FindNode<EnumerationNode>("TriggerSelector");
                var triggerMode = _nodeMap.FindNode<EnumerationNode>("TriggerMode");

                if (triggerSelector != null && triggerMode != null &&
                    triggerSelector.IsWriteable() && triggerMode.IsWriteable())
                {
                    if (triggerSelector.HasEntry("LineStart"))
                    {
                        triggerSelector.SetCurrentEntry("LineStart");
                        if (triggerMode.HasEntry("Off"))
                        {
                            triggerMode.SetCurrentEntry("Off");
                        }
                    }
                }

                // 設定行掃描頻率
                var node = _nodeMap.FindNode<FloatNode>("AcquisitionLineRate");
                if (node != null && node.IsWriteable())
                {
                    var value = Math.Clamp(LineRate, LineRateMin, LineRateMax);

                    // 對齊到 increment
                    if (node.HasConstantIncrement())
                    {
                        var increment = node.Increment();
                        value = LineRateMin + Math.Round((value - LineRateMin) / increment) * increment;
                    }

                    node.SetValue(value);
                    LineRate = node.Value();
                    LogMessage($"✓ 行掃描頻率已設定: {LineRate:F2} Hz");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"設定行掃描頻率失敗: {ex.Message}");
            }
            finally
            {
                _parameterLock.Release();
            }
        };
        _lineRateDebounceTimer.AutoReset = false;
        _lineRateDebounceTimer.Start();
        await Task.CompletedTask;
    }

    private async Task StartContinuousAcquisitionAsync()
    {
        if (_device == null || _nodeMap == null || _isAcquiring)
            return;

        try
        {
            // Open data stream
            var dataStreams = _device.DataStreams();
            if (dataStreams.Count == 0)
            {
                LogMessage("✗ 無法取得資料流");
                return;
            }

            _dataStream = dataStreams[0].OpenDataStream();

            // Allocate buffers
            var payloadSize = _dataStream.PayloadSize();
            if (payloadSize == 0)
            {
                var payloadNode = _nodeMap.FindNode<IntegerNode>("PayloadSize");
                payloadSize = payloadNode != null ? (uint)payloadNode.Value() : 1024 * 1024 * 4;
            }

            var bufferCount = Math.Max(_dataStream.NumBuffersAnnouncedMinRequired(), 3u);
            for (var i = 0; i < bufferCount; i++)
            {
                var buf = _dataStream.AllocAndAnnounceBuffer(payloadSize, IntPtr.Zero);
                _dataStream.QueueBuffer(buf);
            }

            // Configure acquisition mode before starting
            var acquisitionMode = _nodeMap.FindNode<EnumerationNode>("AcquisitionMode");
            if (acquisitionMode != null && acquisitionMode.IsWriteable())
            {
                if (acquisitionMode.HasEntry("Continuous"))
                {
                    acquisitionMode.SetCurrentEntry("Continuous");
                    LogMessage("✓ AcquisitionMode 設定為 Continuous");
                }
            }

            // For Linescan mode: Ensure LineStart trigger is Off
            if (IsLinescanMode)
            {
                var triggerSelector = _nodeMap.FindNode<EnumerationNode>("TriggerSelector");
                var triggerMode = _nodeMap.FindNode<EnumerationNode>("TriggerMode");

                if (triggerSelector != null && triggerMode != null)
                {
                    if (triggerSelector.IsWriteable() && triggerSelector.HasEntry("LineStart"))
                    {
                        triggerSelector.SetCurrentEntry("LineStart");
                        if (triggerMode.IsWriteable() && triggerMode.HasEntry("Off"))
                        {
                            triggerMode.SetCurrentEntry("Off");
                            LogMessage("✓ LineStart 觸發已關閉 (Linescan 自由運行模式)");
                        }
                    }
                }
            }

            // Start acquisition
            var tlParamsLocked = _nodeMap.FindNode<IntegerNode>("TLParamsLocked");
            if (tlParamsLocked?.IsWriteable() == true)
                tlParamsLocked.SetValue(1);

            _dataStream.StartAcquisition();

            var acquisitionStart = _nodeMap.FindNode<CommandNode>("AcquisitionStart");
            if (acquisitionStart?.IsWriteable() == true)
            {
                acquisitionStart.Execute();
                acquisitionStart.WaitUntilDone();
            }

            _isAcquiring = true;
            LogMessage("✓ 開始連續預覽");

            // Start acquisition loop
            _acquisitionCts = new CancellationTokenSource();
            _acquisitionTask = Task.Run(() => AcquisitionLoop(_acquisitionCts.Token), _acquisitionCts.Token);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            LogMessage($"✗ 啟動連續預覽失敗: {ex.Message}");
            _logger.LogError(ex, "啟動連續預覽失敗");
        }
    }

    private async Task StopContinuousAcquisitionAsync()
    {
        if (!_isAcquiring)
            return;

        try
        {
            _acquisitionCts?.Cancel();

            if (_acquisitionTask != null)
                await _acquisitionTask;

            _acquisitionCts?.Dispose();
            _acquisitionCts = null;
            _acquisitionTask = null;

            // Stop acquisition
            if (_nodeMap != null)
            {
                var acquisitionStop = _nodeMap.FindNode<CommandNode>("AcquisitionStop");
                if (acquisitionStop?.IsWriteable() == true)
                {
                    acquisitionStop.Execute();
                    acquisitionStop.WaitUntilDone();
                }
            }

            // Stop and close data stream properly
            if (_dataStream != null)
            {
                try
                {
                    _dataStream.StopAcquisition(AcquisitionStopMode.Default);
                    _dataStream.Flush(DataStreamFlushMode.DiscardAll);
                    _dataStream.Dispose();
                }
                catch (Exception disposeEx)
                {
                    _logger.LogWarning(disposeEx, "DataStream Dispose 失敗");
                }
                finally
                {
                    _dataStream = null;
                }
            }

            _isAcquiring = false;
            LogMessage("✓ 停止連續預覽");
        }
        catch (Exception ex)
        {
            LogMessage($"停止連續預覽失敗: {ex.Message}");
            _isAcquiring = false;
        }
    }

    private void AcquisitionLoop(CancellationToken ct)
    {
        var frameCount = 0;
        var timeoutCount = 0;
        var lastLogTime = DateTime.Now;

        // Linescan mode needs longer timeout
        var timeoutMs = IsLinescanMode ? 5000ul : 1000ul;

        try
        {
            while (!ct.IsCancellationRequested && _dataStream != null)
            {
                try
                {
                    var buffer = _dataStream.WaitForFinishedBuffer(new peak.core.Timeout(timeoutMs));

                    if (buffer != null && buffer.HasImage() && !buffer.IsIncomplete())
                    {
                        frameCount++;
                        timeoutCount = 0;  // Reset timeout counter on successful frame
                        ProcessFrame(buffer);
                        _dataStream.QueueBuffer(buffer);

                        // Log frame acquisition in Linescan mode
                        if (IsLinescanMode && frameCount % 10 == 0)
                        {
                            LogMessage($"✓ Linescan 已捕獲 {frameCount} 幀");
                        }
                    }
                }
                catch (ApplicationException ex) when (ex.Message.Contains("TIMEOUT"))
                {
                    if (!ct.IsCancellationRequested)
                    {
                        timeoutCount++;

                        // In Linescan mode, timeout is expected when no object is moving
                        if (IsLinescanMode)
                        {
                            // Only log occasionally to avoid spam
                            if ((DateTime.Now - lastLogTime).TotalSeconds > 10)
                            {
                                _logger.LogDebug("Linescan 等待影像幀中... (無物體移動時正常)");
                                lastLogTime = DateTime.Now;
                            }
                        }
                        else
                        {
                            // In Area Scan mode, timeout is a problem
                            _logger.LogWarning(ex, "獲取影像幀失敗");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "獲取影像幀失敗");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "影像擷取迴圈異常");
        }
        finally
        {
            if (IsLinescanMode)
            {
                LogMessage($"連續預覽結束 (共 {frameCount} 幀) - Linescan 模式");
            }
            else
            {
                LogMessage($"連續預覽結束 (共 {frameCount} 幀)");
            }
        }
    }

    private void ProcessFrame(peak.core.Buffer buffer)
    {
        try
        {
            var width = (int)buffer.Width();
            var height = (int)buffer.Height();
            var dataSize = (int)buffer.DeliveredDataSize();
            var pixelFormat = buffer.PixelFormat();

            _logger.LogDebug("影像幀資訊: Width={Width}, Height={Height}, DataSize={DataSize}, PixelFormat={PixelFormat}",
                width, height, dataSize, pixelFormat);

            var imageData = new byte[dataSize];
            System.Runtime.InteropServices.Marshal.Copy(buffer.BasePtr(), imageData, 0, dataSize);

            // Convert to BitmapSource on UI thread
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // 根據像素格式計算正確的 stride 和 WPF PixelFormat
                    System.Windows.Media.PixelFormat wpfPixelFormat;
                    int stride;
                    int bytesPerPixel;

                    // 檢查實際的像素格式
                    var formatName = pixelFormat.ToString();
                    _logger.LogDebug("像素格式: {FormatName}", formatName);

                    // 根據資料大小判斷每像素位元組數
                    bytesPerPixel = dataSize / (width * height);
                    _logger.LogDebug("每像素位元組數: {BytesPerPixel}", bytesPerPixel);

                    if (bytesPerPixel == 1)
                    {
                        // Mono8 或 Bayer8
                        wpfPixelFormat = PixelFormats.Gray8;
                        stride = width;
                    }
                    else if (bytesPerPixel == 2)
                    {
                        // Mono16 或 Mono12/10 packed
                        wpfPixelFormat = PixelFormats.Gray16;
                        stride = width * 2;
                    }
                    else
                    {
                        _logger.LogError("未支援的像素格式: BytesPerPixel={BytesPerPixel}", bytesPerPixel);
                        return;
                    }

                    // 確保 stride 是 4 的倍數（記憶體對齊）
                    stride = (stride + 3) / 4 * 4;

                    _logger.LogDebug("建立 Bitmap: Width={Width}, Height={Height}, Stride={Stride}, DataSize={DataSize}, BytesPerPixel={BytesPerPixel}",
                        width, height, stride, dataSize, bytesPerPixel);

                    // 驗證緩衝區大小
                    var requiredSize = stride * height;
                    if (dataSize < requiredSize)
                    {
                        _logger.LogError("緩衝區不足: DataSize={DataSize} < Required={Required}", dataSize, requiredSize);
                        return;
                    }

                    try
                    {
                        var bitmap = BitmapSource.Create(
                            width, height,
                            96, 96,
                            wpfPixelFormat,
                            null,
                            imageData,
                            stride);
                        bitmap.Freeze();

                        FrameBitmap = bitmap;
                        StatusText = $"預覽中: {width}x{height} ({formatName})";
                    }
                    catch (ArgumentException ex)
                    {
                        _logger.LogError(ex, "BitmapSource.Create 失敗: Width={Width}, Height={Height}, Stride={Stride}, DataSize={DataSize}, PixelFormat={PixelFormat}",
                            width, height, stride, dataSize, wpfPixelFormat);

                        // 嘗試使用 WriteableBitmap 替代方案
                        try
                        {
                            var writeableBitmap = new System.Windows.Media.Imaging.WriteableBitmap(
                                width, height, 96, 96, wpfPixelFormat, null);

                            writeableBitmap.WritePixels(
                                new System.Windows.Int32Rect(0, 0, width, height),
                                imageData,
                                stride,
                                0);

                            writeableBitmap.Freeze();
                            FrameBitmap = writeableBitmap;
                            StatusText = $"預覽中: {width}x{height} ({formatName})";
                            _logger.LogInformation("使用 WriteableBitmap 成功");
                        }
                        catch (Exception ex2)
                        {
                            _logger.LogError(ex2, "WriteableBitmap 也失敗");
                            return;
                        }
                    }

                    // 發送影像訊息到主程式
                    var imageDataObj = new ImageData(
                        Bytes: imageData,
                        Width: width,
                        Height: height,
                        PixelFormat: formatName
                    );
                    _messenger.Send(new CameraCaptureMessage(imageDataObj));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "更新影像顯示失敗");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "處理影像幀失敗");
        }
    }

    private void LogMessage(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _logBuilder.AppendLine($"[{timestamp}] {message}");
        LogText = _logBuilder.ToString();
        _logger.LogInformation(message);
    }
}
