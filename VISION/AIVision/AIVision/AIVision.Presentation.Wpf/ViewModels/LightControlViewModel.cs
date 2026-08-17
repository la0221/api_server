using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// LTS 光源控制面板 ViewModel（WPF + MVVM）
/// </summary>
public partial class LightControlViewModel : ObservableObject, IDisposable
{
    private readonly ILightPort _lightPort;
    private readonly ILogger<LightControlViewModel> _logger;
    private readonly LightDeviceOptions _options;
    private readonly DispatcherTimer _pollTimer;
    private CancellationTokenSource? _cts;
    private bool _isRefreshing;
    private bool _disposed;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusText = "尚未連接";
    [ObservableProperty] private string _deviceIp = "192.168.1.95";
    [ObservableProperty] private int _devicePort = 8000;
    [ObservableProperty] private string _gatewayIp = "192.168.1.1";
    [ObservableProperty] private string _deviceVersion = "-";
    [ObservableProperty] private string _deviceSerial = "-";
    [ObservableProperty] private int _channelCount = 8;
    [ObservableProperty] private string _currentMode = "常亮";
    [ObservableProperty] private string _triggerPolarityDisplay = "上升沿";
    [ObservableProperty] private bool _isHeartbeatEnabled;
    [ObservableProperty] private string _onlineState = "離線";
    [ObservableProperty] private string _editableDeviceIp = "192.168.1.95";
    [ObservableProperty] private string _editableGatewayIp = "192.168.1.1";
    [ObservableProperty] private string _editableDevicePort = "8000";
    [ObservableProperty] private string _serverIp = "192.168.1.90";
    [ObservableProperty] private string _serverPort = "8000";

    [ObservableProperty] private SelectableOption<LightWorkMode>? _selectedMode;
    [ObservableProperty] private SelectableOption<LightTriggerPolarity>? _selectedTriggerPolarity;

    public ObservableCollection<LightChannelViewModel> Channels { get; } = new();
    public ObservableCollection<SelectableOption<LightWorkMode>> AvailableModes { get; }
    public ObservableCollection<SelectableOption<LightTriggerPolarity>> TriggerPolarityOptions { get; }
    public ObservableCollection<string> DiagnosticLog { get; } = new();

    public LightControlViewModel(
        ILightPort lightPort,
        ILogger<LightControlViewModel> logger,
        IOptions<LightDeviceOptions>? options = null)
    {
        _lightPort = lightPort ?? throw new ArgumentNullException(nameof(lightPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? new LightDeviceOptions();

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
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.PollIntervalMs ?? 500, 250, 2000))
        };
        _pollTimer.Tick += async (_, _) => await RefreshStateInternalAsync();

        InitializeFromOptions();
        InitializeChannels(ChannelCount);
    }

    private void InitializeFromOptions()
    {
        DeviceIp = _options.DeviceIp ?? DeviceIp;
        DevicePort = _options.DevicePort ?? DevicePort;
        GatewayIp = _options.GatewayIp ?? GatewayIp;
        EditableDeviceIp = DeviceIp;
        EditableGatewayIp = GatewayIp;
        EditableDevicePort = DevicePort.ToString();
        ChannelCount = _options.ChannelCount ?? ChannelCount;
        ServerIp = _options.ServerIp ?? ServerIp;
        ServerPort = (_options.ServerPort ?? DevicePort).ToString();
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
            Channels.Add(new LightChannelViewModel(i, this));
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            StatusText = $"🔌 正在連接 {DeviceIp}:{DevicePort} ...";
            var state = await _lightPort.GetStateAsync(_cts.Token);
            if (!state.IsConnected)
            {
                StatusText = "❌ 設備未回應，請確認 IP / Port";
                AddLog(StatusText);
                return;
            }

            IsConnected = true;
            StatusText = "✅ 已連接";
            AddLog($"已連接 {DeviceIp}:{DevicePort}");

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
            StatusText = result ? "✅ 心跳已啟用 (0x01A1=1)" : "✅ 心跳已停用 (0x01A1=0)";
            AddLog(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 切換心跳失敗: {ex.Message}";
            _logger.LogError(ex, "切換心跳包失敗");
        }
    }

    [RelayCommand]
    private async Task SaveNetworkSettingsAsync()
    {
        if (!IsConnected)
        {
            StatusText = "❌ 請先連接設備";
            return;
        }

        if (!TryBuildNetworkProfile(out var profile, out var error))
        {
            StatusText = $"❌ {error}";
            return;
        }

        try
        {
            await _lightPort.WriteNetworkProfileAsync(profile, ConnectionToken);
            DeviceIp = profile.DeviceIp;
            GatewayIp = profile.GatewayIp;
            DevicePort = profile.DevicePort;
            StatusText = "✅ 網路參數已寫入（請視需求執行斷電備份）";
            AddLog(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 寫入網路參數失敗: {ex.Message}";
            _logger.LogError(ex, "寫入網路參數失敗");
        }
    }

    [RelayCommand]
    private void ResetNetworkForm()
    {
        EditableDeviceIp = DeviceIp;
        EditableGatewayIp = GatewayIp;
        EditableDevicePort = DevicePort.ToString();
        StatusText = "ℹ️ 已重新帶入目前設備網址";
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
            StatusText = "✅ 已觸發斷電備份 (0x0101=1)";
            AddLog(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 備份失敗: {ex.Message}";
            _logger.LogError(ex, "備份參數失敗");
        }
    }

    private bool TryBuildNetworkProfile(out LightNetworkProfile profile, out string error)
    {
        profile = default;
        error = string.Empty;

        if (!int.TryParse(EditableDevicePort, out var newPort) || newPort <= 0 || newPort > 65535)
        {
            error = "Port 必須在 1~65535";
            return false;
        }

        profile = new LightNetworkProfile(EditableDeviceIp.Trim(), EditableGatewayIp.Trim(), newPort);
        return true;
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
            var deviceInfoTask = _lightPort.ReadDeviceInfoAsync(ConnectionToken);
            var networkTask = _lightPort.ReadNetworkProfileAsync(ConnectionToken);

            var info = await deviceInfoTask;
            var network = await networkTask;

            DeviceVersion = info.FirmwareVersion;
            DeviceSerial = info.SerialNumber;
            ChannelCount = info.ChannelCount;
            CurrentMode = AvailableModes.FirstOrDefault(m => m.Value == info.WorkMode)?.Display ?? info.WorkMode.ToString();
            SelectedMode = AvailableModes.FirstOrDefault(m => m.Value == info.WorkMode) ?? AvailableModes.FirstOrDefault();
            SelectedTriggerPolarity = TriggerPolarityOptions.FirstOrDefault(p => p.Value == info.TriggerPolarity) ?? TriggerPolarityOptions.FirstOrDefault();
            TriggerPolarityDisplay = SelectedTriggerPolarity?.Display ?? info.TriggerPolarity.ToString();
            IsHeartbeatEnabled = info.HeartbeatEnabled;
            OnlineState = info.IsOnline ? "在線 (0x00A1=1)" : "離線 (0x00A1=0)";

            DeviceIp = network.DeviceIp;
            GatewayIp = network.GatewayIp;
            DevicePort = network.DevicePort;
            EditableDeviceIp = network.DeviceIp;
            EditableGatewayIp = network.GatewayIp;
            EditableDevicePort = network.DevicePort.ToString();

            AddLog($"讀取資訊：版本 {DeviceVersion}, SN {DeviceSerial}, 通道 {ChannelCount}");
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

            // 釋放所有通道的 debounce timer
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
/// 單一通道 ViewModel
/// </summary>
public partial class LightChannelViewModel : ObservableObject, IDisposable
{
    private readonly LightControlViewModel _parent;
    private System.Timers.Timer? _debounceTimer;
    private bool _disposed;
    private bool _suppressIntensityNotification;

    [ObservableProperty] private int _channelNumber;
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private int _intensity;

    public LightChannelViewModel(int channelNumber, LightControlViewModel parent)
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

/// <summary>
/// UI 綁定用的選項封裝
/// </summary>
public sealed record SelectableOption<T>(T Value, string Display)
{
    public static IEnumerable<SelectableOption<T>> CreateFromEnum(Func<T, string> displayFactory)
    {
        if (!typeof(T).IsEnum) throw new InvalidOperationException("SelectableOption 只支援列舉型別");
        return Enum.GetValues(typeof(T)).Cast<T>().Select(v => new SelectableOption<T>(v, displayFactory(v)));
    }
}

// LightDeviceOptions 已移至 AIVision.Infrastructure.Devices.Light 命名空間
