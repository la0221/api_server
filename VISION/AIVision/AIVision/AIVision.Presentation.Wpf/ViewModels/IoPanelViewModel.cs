using System.Collections.ObjectModel;
using AIVision.Application.Ports.Devices;
using AIVision.Domain.Plc;
using AIVision.Infrastructure.Devices.Plc.SignalMapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class IoPanelViewModel : ObservableObject
{
    private readonly IPlcCommunicationPort _communicationPort;
    private readonly IPlcSignalMapper _signalMapper;
    private readonly IPlcHandshakePort _handshakePort;
    private readonly PlcSignalMapOptions _signalMapOptions;
    private CancellationTokenSource? _pollCts;

    public IoPanelViewModel(
        IPlcCommunicationPort communicationPort,
        IPlcSignalMapper signalMapper,
        IPlcHandshakePort handshakePort,
        IOptions<PlcSignalMapOptions> signalMapOptions)
    {
        _communicationPort = communicationPort;
        _signalMapper = signalMapper;
        _handshakePort = handshakePort;
        _signalMapOptions = signalMapOptions.Value;

        // 初始化訊號集合
        InputSignals = new ObservableCollection<PlcSignalViewModel>();
        OutputSignals = new ObservableCollection<PlcSignalViewModel>();
        HandshakeLogs = new ObservableCollection<string>();

        // 初始化 AddressBaseMode
        _addressBaseMode = _signalMapOptions.AddressBaseMode;

        // 載入訊號定義
        LoadSignalDefinitions();

        // 訂閱事件
        _communicationPort.ConnectionStateChanged += OnConnectionStateChanged;
        _handshakePort.StateChanged += OnHandshakeStateChanged;
        _handshakePort.TriggerReceived += OnTriggerReceived;
    }

    #region Properties

    [ObservableProperty]
    private string _plcIp = "192.168.250.1";

    [ObservableProperty]
    private int _plcPort = 502;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isHandshakeRunning;

    [ObservableProperty]
    private string _handshakeStateText = "未啟動";

    [ObservableProperty]
    private PlcHandshakeState _handshakeState = PlcHandshakeState.Disconnected;

    [ObservableProperty]
    private PlcAddressBaseMode _addressBaseMode = PlcAddressBaseMode.OneBased;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isAutoRunActive;

    /// <summary>
    /// 當交握運行中時禁止切換 BaseMode
    /// </summary>
    public bool CanChangeAddressBaseMode => !IsHandshakeRunning;

    /// <summary>
    /// 只有在 Auto Run 未運行時才能斷開連線
    /// </summary>
    public bool CanDisconnect => IsConnected && !IsAutoRunActive;

    public ObservableCollection<PlcSignalViewModel> InputSignals { get; }

    public ObservableCollection<PlcSignalViewModel> OutputSignals { get; }

    public ObservableCollection<string> HandshakeLogs { get; }

    // 舊的屬性（保持相容）
    public ObservableCollection<IoSignalViewModel> Inputs => new(
        InputSignals.Select(s => new IoSignalViewModel(s.Name, s.IsOn)));

    public ObservableCollection<IoSignalViewModel> Outputs => new(
        OutputSignals.Select(s => new IoSignalViewModel(s.Name, s.IsOn)));

    #endregion

    #region Commands

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            AddLog($"正在連線到 {PlcIp}:{PlcPort}...");
            await _communicationPort.ConnectAsync(PlcIp, PlcPort);
            StartPolling();
            AddLog("連線成功");
        }
        catch (Exception ex)
        {
            AddLog($"連線失敗: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        // 再次檢查 Auto Run 狀態（雙重保護）
        if (IsAutoRunActive)
        {
            AddLog("⚠️ Auto Run 運行中，無法斷開連線");
            return;
        }

        try
        {
            StopPolling();
            if (_handshakePort.IsRunning)
            {
                await _handshakePort.StopAsync();
            }
            await _communicationPort.DisconnectAsync();
            AddLog("已斷線");
        }
        catch (Exception ex)
        {
            AddLog($"斷線失敗: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StartHandshakeAsync()
    {
        try
        {
            AddLog("啟動交握服務...");

            // 停止 IoPanel 輪詢，避免與交握服務競爭 PLC 連線
            StopPolling();
            AddLog("已停止 IoPanel 輪詢（避免 PLC 競爭）");

            await _handshakePort.StartAsync();
            IsHandshakeRunning = true;
            AddLog("交握服務已啟動");
        }
        catch (Exception ex)
        {
            AddLog($"❌ 啟動失敗: {ex.Message}");
            AddLog($"錯誤類型: {ex.GetType().Name}");
            if (ex.InnerException != null)
            {
                AddLog($"內部錯誤: {ex.InnerException.Message}");
            }
            // 顯示 Stack Trace 的前幾行
            var stackLines = ex.StackTrace?.Split('\n').Take(3);
            if (stackLines != null)
            {
                foreach (var line in stackLines)
                {
                    AddLog($"  {line.Trim()}");
                }
            }
        }
    }

    [RelayCommand]
    private async Task StopHandshakeAsync()
    {
        try
        {
            AddLog("停止交握服務...");
            await _handshakePort.StopAsync();
            IsHandshakeRunning = false;
            AddLog("交握服務已停止");

            // 恢復 IoPanel 輪詢
            if (_communicationPort.IsConnected)
            {
                StartPolling();
                AddLog("已恢復 IoPanel 輪詢");
            }
        }
        catch (Exception ex)
        {
            AddLog($"停止失敗: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        try
        {
            await _handshakePort.ResetAsync();
            AddLog("已重置狀態機");
        }
        catch (Exception ex)
        {
            AddLog($"重置失敗: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SendManualOkAsync()
    {
        try
        {
            await _handshakePort.ReportResultAsync(PlcInspectionResult.Ok);
            AddLog("手動送出 OK");
        }
        catch (Exception ex)
        {
            AddLog($"送出失敗: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SendManualNgAsync()
    {
        try
        {
            await _handshakePort.ReportResultAsync(PlcInspectionResult.Ng);
            AddLog("手動送出 NG");
        }
        catch (Exception ex)
        {
            AddLog($"送出失敗: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task NotifyCaptureCompleteAsync()
    {
        try
        {
            await _handshakePort.NotifyCaptureCompleteAsync();
            AddLog("手動通知取像完成");
        }
        catch (Exception ex)
        {
            AddLog($"通知失敗: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        HandshakeLogs.Clear();
    }

    #endregion

    #region Private Methods

    private void LoadSignalDefinitions()
    {
        var definitions = _signalMapper.GetAllSignalDefinitions();

        InputSignals.Clear();
        OutputSignals.Clear();

        // 輸入訊號: PlcToPc 和 Bidirectional (因為 Bidirectional 也需要讀取 PLC 的值)
        foreach (var def in definitions.Where(d => d.Direction == PlcSignalDirection.PlcToPc || d.Direction == PlcSignalDirection.Bidirectional))
        {
            InputSignals.Add(new PlcSignalViewModel
            {
                Name = def.Name,
                Address = def.GetDisplayAddress(AddressBaseMode),
                Description = def.Description,
                IsOn = false
            });
        }

        // 輸出訊號: PcToPlc
        foreach (var def in definitions.Where(d => d.Direction == PlcSignalDirection.PcToPlc))
        {
            OutputSignals.Add(new PlcSignalViewModel
            {
                Name = def.Name,
                Address = def.GetDisplayAddress(AddressBaseMode),
                Description = def.Description,
                IsOn = false
            });
        }
    }

    /// <summary>
    /// 當 AddressBaseMode 改變時重新載入訊號顯示
    /// </summary>
    partial void OnAddressBaseModeChanged(PlcAddressBaseMode value)
    {
        LoadSignalDefinitions();
    }

    /// <summary>
    /// 當 IsHandshakeRunning 改變時，通知 CanChangeAddressBaseMode 屬性更新
    /// </summary>
    partial void OnIsHandshakeRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChangeAddressBaseMode));
    }

    /// <summary>
    /// 啟動 PLC 訊號輪詢
    /// </summary>
    public void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _ = PollSignalsAsync(_pollCts.Token);
    }

    /// <summary>
    /// 停止 PLC 訊號輪詢（Auto Run 啟動時應呼叫此方法避免競爭條件）
    /// </summary>
    public void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts = null;
    }

    private async Task PollSignalsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_communicationPort.IsConnected)
                {
                    // 使用獨立的 CancellationToken，避免被外部取消影響
                    using var timeoutCts = new CancellationTokenSource(500); // 500ms 超時
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, timeoutCts.Token);

                    try
                    {
                        var allSignals = await _signalMapper.ReadAllSignalsAsync(linkedCts.Token);

                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            foreach (var signal in InputSignals)
                            {
                                if (allSignals.TryGetValue(signal.Name, out var value))
                                {
                                    signal.IsOn = value;
                                }
                            }

                            foreach (var signal in OutputSignals)
                            {
                                if (allSignals.TryGetValue(signal.Name, out var value))
                                {
                                    signal.IsOn = value;
                                }
                            }
                        });
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // 超時或其他取消，繼續下一輪
                    }
                    catch (InvalidOperationException)
                    {
                        // PLC 未連線，繼續等待
                    }
                }

                await Task.Delay(500, cancellationToken); // 500ms 輪詢間隔 (降低頻率減輕 PLC 負擔)
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            IsConnected = connected;
            if (!connected)
            {
                IsHandshakeRunning = false;
                HandshakeStateText = "未連線";
            }
        });
    }

    private void OnHandshakeStateChanged(object? sender, PlcHandshakeStateChangedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            HandshakeState = e.NewState;
            HandshakeStateText = e.NewState.ToString();
            AddLog($"狀態: {e.OldState} → {e.NewState}");
        });
    }

    private void OnTriggerReceived(object? sender, PlcTriggerReceivedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            AddLog($"觸發 #{e.TriggerCount}");
        });
    }

    private void AddLog(string message)
    {
        var log = $"[{DateTime.Now:HH:mm:ss}] {message}";

        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
        {
            HandshakeLogs.Insert(0, log);
            while (HandshakeLogs.Count > 100)
            {
                HandshakeLogs.RemoveAt(HandshakeLogs.Count - 1);
            }
        }
        else
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                HandshakeLogs.Insert(0, log);
                while (HandshakeLogs.Count > 100)
                {
                    HandshakeLogs.RemoveAt(HandshakeLogs.Count - 1);
                }
            });
        }
    }

    #endregion
}

public partial class PlcSignalViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _address = "";

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private string _description = "";
}

public partial class IoSignalViewModel : ObservableObject
{
    public IoSignalViewModel(string name, bool isOn)
    {
        _name = name;
        _isOn = isOn;
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isOn;
}
