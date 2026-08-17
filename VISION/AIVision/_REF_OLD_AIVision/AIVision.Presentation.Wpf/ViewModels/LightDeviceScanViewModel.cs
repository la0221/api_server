using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// LTS 光源設備掃描與重新配置 ViewModel
/// </summary>
public partial class LightDeviceScanViewModel : ObservableObject
{
    private readonly ILogger<LightDeviceScanViewModel> _logger;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _statusText = "準備掃描";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _scanRange = "192.168.1.1-192.168.1.254";
    [ObservableProperty] private string _targetDeviceIp = "";
    [ObservableProperty] private string _newDeviceIp = "192.168.10.10";
    [ObservableProperty] private string _newServerIp = "192.168.10.10";
    [ObservableProperty] private int _newPort = 8000;
    [ObservableProperty] private string _newGateway = "192.168.10.1";
    [ObservableProperty] private string _currentConfig = "請先選擇設備並讀取配置";

    public ObservableCollection<DeviceFoundInfo> FoundDevices { get; } = new();

    public LightDeviceScanViewModel(ILogger<LightDeviceScanViewModel> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 掃描網段尋找設備
    /// </summary>
    [RelayCommand]
    private async Task ScanNetworkAsync()
    {
        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            IsScanning = true;
            FoundDevices.Clear();
            StatusText = "正在掃描網段...";
            Progress = 0;

            // 解析掃描範圍
            var (startIp, endIp) = ParseIpRange(ScanRange);
            if (startIp == null || endIp == null)
            {
                StatusText = "✗ IP 範圍格式錯誤";
                return;
            }

            var startBytes = startIp.GetAddressBytes();
            var endBytes = endIp.GetAddressBytes();
            var start = startBytes[3];
            var end = endBytes[3];
            var total = end - start + 1;

            _logger.LogInformation("開始掃描 {StartIp} - {EndIp} (共 {Total} 個 IP)", startIp, endIp, total);

            var baseIp = $"{startBytes[0]}.{startBytes[1]}.{startBytes[2]}";
            var completed = 0;

            // 並行掃描（每次測試 10 個 IP）
            var batchSize = 10;
            for (int i = start; i <= end; i += batchSize)
            {
                if (_cts.Token.IsCancellationRequested) break;

                var tasks = new List<Task>();
                for (int j = 0; j < batchSize && (i + j) <= end; j++)
                {
                    var currentIp = $"{baseIp}.{i + j}";
                    // 嘗試標準 Modbus TCP 端口 502 和常見的 8000
                    tasks.Add(TryConnectDeviceAsync(currentIp, 502, _cts.Token));
                    tasks.Add(TryConnectDeviceAsync(currentIp, 8000, _cts.Token));
                }

                await Task.WhenAll(tasks);

                completed += tasks.Count;
                Progress = (int)((completed / (double)total) * 100);
                StatusText = $"掃描中... {completed}/{total} ({Progress}%)";
            }

            StatusText = FoundDevices.Count > 0
                ? $"✓ 掃描完成，找到 {FoundDevices.Count} 個設備"
                : "✓ 掃描完成，未找到設備";

            _logger.LogInformation("掃描完成，找到 {Count} 個設備", FoundDevices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "掃描網段時發生錯誤");
            StatusText = $"✗ 掃描失敗: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// 嘗試連接指定 IP 的設備（使用 TCP 端口掃描）
    /// </summary>
    private async Task TryConnectDeviceAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            // 先 Ping 測試
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 100);

            if (reply.Status != IPStatus.Success)
            {
                return; // Ping 不通，跳過
            }

            // 嘗試 TCP 連接測試
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(ip, port);
            var timeoutTask = Task.Delay(500, ct);
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);

            if (completedTask == connectTask && client.Connected)
            {
                // 成功連接，添加到列表
                App.Current.Dispatcher.Invoke(() =>
                {
                    FoundDevices.Add(new DeviceFoundInfo
                    {
                        IpAddress = ip,
                        Port = port,
                        ChannelCount = 8, // Modbus TCP 無法直接獲取，預設 8
                        Status = "✓ 端口開放"
                    });

                    _logger.LogInformation("找到設備: {Ip}:{Port} (TCP 端口開放)", ip, port);
                });
            }
        }
        catch
        {
            // 忽略所有錯誤
        }
    }

    /// <summary>
    /// 讀取設備當前配置（需要使用光源控制面板的 Modbus TCP 功能）
    /// </summary>
    [RelayCommand]
    private Task ReadDeviceConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetDeviceIp))
        {
            StatusText = "✗ 請先選擇或輸入設備 IP";
            return Task.CompletedTask;
        }

        CurrentConfig = $@"【提示】
請使用「光源控制」面板連接到設備 {TargetDeviceIp}，
所有配置資訊會在該面板中顯示。

此掃描工具僅用於發現網段中的 Modbus TCP 設備。";

        StatusText = "ℹ️ 請使用光源控制面板查看詳細配置";
        return Task.CompletedTask;
    }

    /// <summary>
    /// 重新配置選中的設備（需要使用光源控制面板）
    /// </summary>
    [RelayCommand]
    private Task ReconfigureDeviceAsync()
    {
        StatusText = "ℹ️ 請使用光源控制面板的「網路參數」功能進行設備重新配置";

        CurrentConfig = $@"【提示】
設備網路參數配置功能已整合至「光源控制」面板。

請按以下步驟操作：
1. 切換到「光源控制」面板
2. 連接到設備 {TargetDeviceIp}
3. 在「網路參數」區塊中設定新的 IP、網關、端口
4. 點擊「儲存參數」
5. 點擊「斷電備份」以永久保存";

        _logger.LogInformation("提示用戶使用光源控制面板進行網路配置");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 快速掃描常見 IP
    /// </summary>
    [RelayCommand]
    private async Task QuickScanAsync()
    {
        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            IsScanning = true;
            FoundDevices.Clear();
            StatusText = "快速掃描常見 IP...";

            // 常見的 IP 列表
            var commonIps = new[]
            {
                "192.168.1.90", "192.168.1.95", "192.168.1.10",
                "192.168.10.10", "192.168.10.1", "192.168.0.10",
                "192.168.0.1", "192.168.1.1", "192.168.1.100"
            };

            var tasks = commonIps.Select(ip => TryConnectDeviceAsync(ip, 8000, _cts.Token));
            await Task.WhenAll(tasks);

            StatusText = FoundDevices.Count > 0
                ? $"✓ 找到 {FoundDevices.Count} 個設備"
                : "✗ 未找到設備，請嘗試完整掃描";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "快速掃描時發生錯誤");
            StatusText = $"✗ 掃描失敗: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// 解析 IP 範圍
    /// </summary>
    private (IPAddress? start, IPAddress? end) ParseIpRange(string range)
    {
        try
        {
            var parts = range.Split('-');
            if (parts.Length != 2) return (null, null);

            var start = IPAddress.Parse(parts[0].Trim());
            var end = IPAddress.Parse(parts[1].Trim());

            return (start, end);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// 選擇設備並自動讀取配置
    /// </summary>
    [RelayCommand]
    private async Task SelectDeviceAsync(DeviceFoundInfo device)
    {
        TargetDeviceIp = device.IpAddress;
        StatusText = $"已選擇設備: {device.IpAddress}:{device.Port}";

        // 自動讀取配置
        await ReadDeviceConfigAsync();
    }
}

/// <summary>
/// 找到的設備信息
/// </summary>
public class DeviceFoundInfo
{
    public string IpAddress { get; set; } = "";
    public int Port { get; set; }
    public int ChannelCount { get; set; }
    public string Status { get; set; } = "";
}
