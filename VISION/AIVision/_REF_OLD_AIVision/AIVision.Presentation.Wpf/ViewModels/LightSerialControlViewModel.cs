using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AIVision.Application.Ports.Devices;
using AIVision.Infrastructure.Devices.Light;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// LTS 光源控制面板 ViewModel - RS232 版本
/// </summary>
public partial class LightSerialControlViewModel : ObservableObject, IDisposable
{
    private readonly LtsSerialLightPort _lightPort;
    private readonly ILogger<LightSerialControlViewModel> _logger;
    private readonly LightSerialDeviceOptions _options;
    private readonly DispatcherTimer _pollTimer;
    private CancellationTokenSource? _cts;
    private bool _isRefreshing;
    private bool _disposed;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusText = "尚未連接";
    [ObservableProperty] private string? _selectedPortName;
    [ObservableProperty] private int _selectedBaudRate = 19200;
    [ObservableProperty] private string _deviceVersion = "-";
    [ObservableProperty] private string _deviceSerial = "-";
    [ObservableProperty] private int _channelCount = 2;
    [ObservableProperty] private string _currentMode = "常亮";
    [ObservableProperty] private string _triggerPolarityDisplay = "上升沿";
    [ObservableProperty] private bool _isHeartbeatEnabled;
    [ObservableProperty] private string _onlineState = "離線";

    [ObservableProperty] private SelectableOption<LightWorkMode>? _selectedMode;
    [ObservableProperty] private SelectableOption<LightTriggerPolarity>? _selectedTriggerPolarity;

    public ObservableCollection<LightSerialChannelViewModel> Channels { get; } = new();
    public ObservableCollection<string> AvailablePorts { get; } = new();
    public ObservableCollection<int> AvailableBaudRates { get; } = new() { 9600, 19200, 38400, 57600, 115200 };
    public ObservableCollection<SelectableOption<LightWorkMode>> AvailableModes { get; }
    public ObservableCollection<SelectableOption<LightTriggerPolarity>> TriggerPolarityOptions { get; }
    public ObservableCollection<string> DiagnosticLog { get; } = new();

    public LightSerialControlViewModel(
        LtsSerialLightPort lightPort,
        ILogger<LightSerialControlViewModel> logger,
        IOptions<LightSerialDeviceOptions>? options = null)
    {
        _lightPort = lightPort ?? throw new ArgumentNullException(nameof(lightPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? new LightSerialDeviceOptions();

        AvailableModes = new ObservableCollection<SelectableOption<LightWorkMode>>(SelectableOption<LightWorkMode>.CreateFromEnum(
            value => value switch
            {
                LightWorkMode.Constant => "常亮 (0)",
                LightWorkMode.Strobe => "頻閃 (1)",
                LightWorkMode.External => "外部觸發 (2)",
                LightWorkMode.Internal => "內部觸發 (3)",
                LightWorkMode.Software => "軟體觸發 (4)",
                _ => value.ToString()
            }));

        TriggerPolarityOptions = new ObservableCollection<SelectableOption<LightTriggerPolarity>>(SelectableOption<LightTriggerPolarity>.CreateFromEnum(
            value => value switch
            {
                LightTriggerPolarity.RisingEdge => "上升沿 (1)",
                LightTriggerPolarity.FallingEdge => "下降沿 (2)",
                LightTriggerPolarity.LevelHigh => "即時正電平 (3)",
                LightTriggerPolarity.LevelLow => "即時負電平 (4)",
                LightTriggerPolarity.PulseHigh => "限時正 (5)",
                LightTriggerPolarity.PulseLow => "限時負 (6)",
                _ => value.ToString()
            }));

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.PollIntervalMs, 250, 2000))
        };
        _pollTimer.Tick += async (_, _) => await RefreshStateInternalAsync();

        InitializeFromOptions();
        RefreshAvailablePorts();
        InitializeChannels(ChannelCount);
    }

    private void InitializeFromOptions()
    {
        SelectedBaudRate = _options.BaudRate;
        ChannelCount = _options.ChannelCount;
        SelectedMode = AvailableModes.FirstOrDefault();
        SelectedTriggerPolarity = TriggerPolarityOptions.FirstOrDefault();
    }

    partial void OnChannelCountChanged(int value) => InitializeChannels(value);

    private void InitializeChannels(int? countOverride = null)
    {
        Channels.Clear();
        var count = countOverride ?? ChannelCount;
        for (int i = 1; i <= count; i++)
        {
            Channels.Add(new LightSerialChannelViewModel(i, this));
        }
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        RefreshAvailablePorts();
        AddLog($"已刷新串口列表，找到 {AvailablePorts.Count} 個串口");
    }

    private void RefreshAvailablePorts()
    {
        var currentSelection = SelectedPortName;
        AvailablePorts.Clear();

        foreach (var port in SerialPort.GetPortNames().OrderBy(p => p))
        {
            AvailablePorts.Add(port);
        }

        // 嘗試保留之前的選擇，或選擇設定檔中的預設值
        if (!string.IsNullOrEmpty(currentSelection) && AvailablePorts.Contains(currentSelection))
        {
            SelectedPortName = currentSelection;
        }
        else if (AvailablePorts.Contains(_options.PortName))
        {
            SelectedPortName = _options.PortName;
        }
        else if (AvailablePorts.Count > 0)
        {
            SelectedPortName = AvailablePorts[0];
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected)
        {
            return;
        }

        if (string.IsNullOrEmpty(SelectedPortName))
        {
            StatusText = "❌ 請選擇串口";
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            StatusText = $"🔌 正在連接 {SelectedPortName} (Baud: {SelectedBaudRate})...";
            AddLog(StatusText);

            var success = await _lightPort.ConnectAsync(SelectedPortName, SelectedBaudRate, _cts.Token);
            if (!success)
            {
                StatusText = $"❌ 無法開啟串口 {SelectedPortName}";
                AddLog(StatusText);
                return;
            }

            // 嘗試讀取狀態確認連接
            var state = await _lightPort.GetStateAsync(_cts.Token);
            if (!state.IsConnected)
            {
                StatusText = "⚠️ 串口已開啟，但設備未回應";
                AddLog(StatusText);
                // 仍然標記為已連接，讓用戶可以繼續嘗試
            }
            else
            {
                StatusText = "✅ 已連接";
                AddLog($"已連接 {SelectedPortName}");
            }

            IsConnected = true;
            await LoadDeviceInfoAsync();
            await RefreshStateInternalAsync();
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 連接失敗: {ex.Message}";
            AddLog(StatusText);
            _logger.LogError(ex, "連接光源控制器失敗");
        }
    }

    [RelayCommand]
    private Task DisconnectAsync()
    {
        _pollTimer.Stop();
        _cts?.Cancel();
        _lightPort.Disconnect();
        IsConnected = false;
        StatusText = "⭕ 已斷開";
        AddLog("已斷開與光源控制器的連線");
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshStateAsync() => await RefreshStateInternalAsync();

    [RelayCommand]
    private async Task TurnAllOnAsync()
    {
        foreach (var channel in Channels)
        {
            await channel.TurnOnInternalAsync();
        }
        AddLog("全部通道已指示開啟");
    }

    [RelayCommand]
    private async Task TurnAllOffAsync()
    {
        foreach (var channel in Channels)
        {
            await channel.TurnOffInternalAsync();
        }
        AddLog("全部通道已關閉");
    }

    [RelayCommand]
    private async Task SetModeAsync()
    {
        if (!IsConnected || SelectedMode is null)
        {
            StatusText = "❌ 請先連接設備";
            return;
        }

        try
        {
            await _lightPort.SetModeAsync(SelectedMode.Value, ConnectionToken);
            CurrentMode = SelectedMode.Display;
            StatusText = $"✅ 工作模式已設為 {SelectedMode.Display}";
            AddLog(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 設定模式失敗: {ex.Message}";
            _logger.LogError(ex, "設定工作模式失敗");
        }
    }

    [RelayCommand]
    private async Task ApplyTriggerPolarityAsync()
    {
        if (!IsConnected || SelectedTriggerPolarity is null)
        {
            StatusText = "❌ 請先連接設備";
            return;
        }

        try
        {
            await _lightPort.SetTriggerPolarityAsync(SelectedTriggerPolarity.Value, ConnectionToken);
            TriggerPolarityDisplay = SelectedTriggerPolarity.Display;
            StatusText = $"✅ 觸發極性已設為 {TriggerPolarityDisplay}";
            AddLog(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 設定觸發極性失敗: {ex.Message}";
            _logger.LogError(ex, "設定觸發極性失敗");
        }
    }

    [RelayCommand]
    private async Task ToggleHeartbeatAsync()
    {
        if (!IsConnected)
        {
            StatusText = "❌ 請先連接設備";
            return;
        }

        try
        {
            var next = !IsHeartbeatEnabled;
            var result = await _lightPort.SetHeartbeatAsync(next, ConnectionToken);
            IsHeartbeatEnabled = result;
            StatusText = result ? "✅ 心跳已啟用" : "✅ 心跳已停用";
            AddLog(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 切換心跳失敗: {ex.Message}";
            _logger.LogError(ex, "切換心跳包失敗");
        }
    }

    [RelayCommand]
    private async Task BackupParametersAsync()
    {
        if (!IsConnected)
        {
            StatusText = "❌ 請先連接設備";
            return;
        }

        try
        {
            await _lightPort.BackupParametersAsync(ConnectionToken);
            StatusText = "✅ 已觸發斷電備份";
            AddLog(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 備份失敗: {ex.Message}";
            _logger.LogError(ex, "備份參數失敗");
        }
    }

    private async Task RefreshStateInternalAsync()
    {
        if (!IsConnected || _isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var state = await _lightPort.GetStateAsync(ConnectionToken);
            if (!state.IsConnected)
            {
                IsConnected = false;
                StatusText = "⚠️ 連線中斷";
                _pollTimer.Stop();
                AddLog("讀取狀態失敗，設備回覆離線");
                return;
            }

            foreach (var kvp in state.ChannelValue)
            {
                var vm = Channels.FirstOrDefault(c => c.ChannelNumber == kvp.Key);
                vm?.UpdateFromDevice(kvp.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新狀態失敗");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task LoadDeviceInfoAsync()
    {
        try
        {
            var info = await _lightPort.ReadDeviceInfoAsync(ConnectionToken);

            DeviceVersion = info.FirmwareVersion;
            DeviceSerial = info.SerialNumber;
            ChannelCount = info.ChannelCount;
            CurrentMode = AvailableModes.FirstOrDefault(m => m.Value == info.WorkMode)?.Display ?? info.WorkMode.ToString();
            SelectedMode = AvailableModes.FirstOrDefault(m => m.Value == info.WorkMode) ?? AvailableModes.FirstOrDefault();
            SelectedTriggerPolarity = TriggerPolarityOptions.FirstOrDefault(p => p.Value == info.TriggerPolarity) ?? TriggerPolarityOptions.FirstOrDefault();
            TriggerPolarityDisplay = SelectedTriggerPolarity?.Display ?? info.TriggerPolarity.ToString();
            IsHeartbeatEnabled = info.HeartbeatEnabled;
            OnlineState = info.IsOnline ? "在線" : "離線";

            AddLog($"讀取資訊：版本 {DeviceVersion}, 通道 {ChannelCount}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取設備資訊失敗");
            StatusText = $"⚠️ 讀取設備資訊失敗: {ex.Message}";
            AddLog(StatusText);
        }
    }

    internal CancellationToken ConnectionToken => _cts?.Token ?? CancellationToken.None;

    internal async Task SetChannelIntensityAsync(int channel, int value)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            await _lightPort.SetIntensityAsync(channel, value, ConnectionToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "設定通道 {Channel} 亮度失敗", channel);
            StatusText = $"⚠️ 通道{channel} 亮度設定失敗: {ex.Message}";
        }
    }

    internal async Task TurnChannelAsync(int channel, bool on)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            await _lightPort.TurnAsync(channel, on, ConnectionToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "切換通道 {Channel} 失敗", channel);
            StatusText = $"⚠️ 通道{channel} 切換失敗: {ex.Message}";
        }
    }

    private void AddLog(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        DiagnosticLog.Insert(0, entry);
        if (DiagnosticLog.Count > 150)
        {
            DiagnosticLog.RemoveAt(DiagnosticLog.Count - 1);
        }
    }

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
            _pollTimer.Stop();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            foreach (var channel in Channels)
            {
                (channel as IDisposable)?.Dispose();
            }
        }

        _disposed = true;
    }

    #endregion
}

/// <summary>
/// 單一通道 ViewModel - RS232 版本
/// </summary>
public partial class LightSerialChannelViewModel : ObservableObject, IDisposable
{
    private readonly LightSerialControlViewModel _parent;
    private System.Timers.Timer? _debounceTimer;
    private bool _disposed;
    private bool _suppressIntensityNotification;

    [ObservableProperty] private int _channelNumber;
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private int _intensity;

    public LightSerialChannelViewModel(int channelNumber, LightSerialControlViewModel parent)
    {
        _channelNumber = channelNumber;
        _parent = parent;
    }

    partial void OnIntensityChanged(int value)
    {
        if (_suppressIntensityNotification || !_parent.IsConnected)
        {
            return;
        }

        _debounceTimer?.Stop();
        _debounceTimer?.Dispose();

        _debounceTimer = new System.Timers.Timer(280)
        {
            AutoReset = false
        };
        _debounceTimer.Elapsed += async (_, _) =>
        {
            await _parent.SetChannelIntensityAsync(ChannelNumber, value);
        };
        _debounceTimer.Start();
    }

    [RelayCommand]
    private async Task TurnOnAsync() => await TurnOnInternalAsync();

    [RelayCommand]
    private async Task TurnOffAsync() => await TurnOffInternalAsync();

    internal async Task TurnOnInternalAsync()
    {
        IsOn = true;
        if (Intensity == 0)
        {
            _suppressIntensityNotification = true;
            Intensity = 128;
            _suppressIntensityNotification = false;
        }
        await _parent.TurnChannelAsync(ChannelNumber, true);
    }

    internal async Task TurnOffInternalAsync()
    {
        IsOn = false;
        await _parent.TurnChannelAsync(ChannelNumber, false);
    }

    internal void UpdateFromDevice(int intensity)
    {
        _suppressIntensityNotification = true;
        Intensity = intensity;
        IsOn = intensity > 0;
        _suppressIntensityNotification = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _debounceTimer?.Stop();
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _disposed = true;
    }
}
