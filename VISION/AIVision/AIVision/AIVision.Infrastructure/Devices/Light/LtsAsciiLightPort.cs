using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Devices.Light;

/// <summary>
/// LTS 光源控制器 ASCII 協定實作
/// 設備會主動撥入到本機 TCP Server (預設 0.0.0.0:8000)
/// 使用 ASCII 協定：$ + 命令(1) + 通道(1) + 資料(3) + XOR(2)
/// </summary>
public sealed class LtsAsciiLightPort : ILightPort, IAsyncDisposable
{
    private readonly ILogger<LtsAsciiLightPort> _logger;
    private readonly string _listenIp;
    private readonly int _listenPort;
    private readonly int _channelCount;
    private readonly int _timeoutMs;
    private readonly int _commandIntervalMs;

    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _listenTask;
    private bool _disposed;
    private bool _protocolConfirmed = false;

    public LtsAsciiLightPort(
        ILogger<LtsAsciiLightPort> logger,
        string listenIp = "0.0.0.0",
        int listenPort = 8000,
        int channelCount = 2,
        int timeoutMs = 1000,
        int commandIntervalMs = 20)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _listenIp = listenIp;
        _listenPort = listenPort;
        _channelCount = channelCount;
        _timeoutMs = timeoutMs;
        _commandIntervalMs = commandIntervalMs;

        _logger.LogInformation("LtsAsciiLightPort 初始化：監聽 {Ip}:{Port}，通道數={Channels}",
            _listenIp, _listenPort, _channelCount);

        StartListening();
    }

    #region TCP Server 管理

    private void StartListening()
    {
        try
        {
            var ipAddress = IPAddress.Parse(_listenIp);
            _listener = new TcpListener(ipAddress, _listenPort);
            _listener.Start();

            _logger.LogInformation("✓ TCP Server 已啟動，等待設備連線...");

            _listenTask = Task.Run(async () => await AcceptConnectionsAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "啟動 TCP Server 失敗");
            throw;
        }
    }

    private async Task AcceptConnectionsAsync()
    {
        while (!_disposeCts.Token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(_disposeCts.Token);
                var remoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";

                _logger.LogInformation("✓ 設備已連線：{RemoteEndPoint}", remoteEndPoint);

                await _connectionLock.WaitAsync(_disposeCts.Token);
                try
                {
                    // 關閉舊連線
                    _stream?.Close();
                    _client?.Close();

                    // 重置協定確認標記
                    _protocolConfirmed = false;

                    // 設定新連線
                    _client = client;
                    _stream = client.GetStream();
                    _stream.ReadTimeout = _timeoutMs;
                    _stream.WriteTimeout = _timeoutMs;

                    // 發送探針確認協定
                    await ProbeDeviceAsync();
                }
                finally
                {
                    _connectionLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "接受連線時發生錯誤");
                await Task.Delay(1000, _disposeCts.Token);
            }
        }
    }

    private async Task ProbeDeviceAsync()
    {
        try
        {
            // 發送探針：讀取 CH1 亮度（命令 4）
            var probeFrame = MakeAsciiFrame(4, 1, null);
            _logger.LogDebug("發送探針：{Frame}", probeFrame);

            var response = await SendAndReceiveAsync(probeFrame, skipLock: true);

            if (response != null && response.StartsWith("$"))
            {
                _protocolConfirmed = true;
                _logger.LogInformation("✓ ASCII 協定確認成功");
            }
            else if (response == "&")
            {
                _protocolConfirmed = false;
                _logger.LogWarning("設備回覆失敗符號 '&'，但連線已建立");
            }
            else
            {
                _protocolConfirmed = false;
                _logger.LogWarning("未收到預期的探針回覆：{Response}", response ?? "(timeout)");
            }
        }
        catch (Exception ex)
        {
            _protocolConfirmed = false;
            _logger.LogWarning(ex, "探針測試失敗，但連線已建立");
        }
    }

    #endregion

    #region ASCII 協定核心

    /// <summary>
    /// 計算 ASCII 幀的 XOR 校驗碼
    /// </summary>
    private static string CalculateXor(string frameBody)
    {
        int xor = 0;
        foreach (char ch in frameBody)
        {
            xor ^= (byte)ch;
        }
        return xor.ToString("X2"); // 2 位十六進位大寫
    }

    /// <summary>
    /// 組裝 ASCII 協定幀
    /// </summary>
    /// <param name="cmd">命令：1=開, 2=關, 3=設定亮度, 4=讀取亮度, 5=全開, 6=全關</param>
    /// <param name="channel">通道：1~4, 5=全部, 6=CH1-3, 7=CH1-2</param>
    /// <param name="brightness">亮度 0-255（僅命令 3/4 需要）</param>
    private static string MakeAsciiFrame(int cmd, int channel, int? brightness)
    {
        if (cmd < 1 || cmd > 6)
            throw new ArgumentOutOfRangeException(nameof(cmd), "命令範圍 1-6");

        if (channel < 1 || channel > 7)
            throw new ArgumentOutOfRangeException(nameof(channel), "通道範圍 1-7");

        string data;
        if (cmd == 3 && brightness.HasValue)
        {
            // 命令 3：設定亮度，轉成 0XX (HEX)
            int value = Math.Clamp(brightness.Value, 0, 255);
            data = $"0{value:X2}"; // 例：200 → "0C8"
        }
        else
        {
            // 其他命令：資料欄固定 "000"
            data = "000";
        }

        string body = $"${cmd}{channel}{data}";
        string xor = CalculateXor(body);
        return body + xor;
    }

    /// <summary>
    /// 發送命令並接收回覆
    /// </summary>
    private async Task<string?> SendAndReceiveAsync(string frame, bool skipLock = false, bool isPolling = false)
    {
        if (!skipLock)
        {
            await _commandLock.WaitAsync(_disposeCts.Token);
        }

        try
        {
            if (_stream == null || !_client!.Connected)
            {
                _logger.LogWarning("設備未連線");
                return null;
            }

            // 發送
            var sendBytes = Encoding.ASCII.GetBytes(frame);
            await _stream.WriteAsync(sendBytes, _disposeCts.Token);

            // 只有非輪詢命令才輸出 Debug 日誌
            if (!isPolling)
            {
                _logger.LogDebug("[TX] {Frame}", frame);
            }

            // 延遲（避免黏包）
            await Task.Delay(_commandIntervalMs, _disposeCts.Token);

            // 接收
            var buffer = new byte[512];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            cts.CancelAfter(_timeoutMs);

            try
            {
                var bytesRead = await _stream.ReadAsync(buffer, cts.Token);
                if (bytesRead > 0)
                {
                    var response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();

                    // 只有非輪詢命令才輸出 Debug 日誌
                    if (!isPolling)
                    {
                        _logger.LogDebug("[RX] {Response}", response);
                    }

                    return response;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("接收回覆超時 ({Timeout}ms)", _timeoutMs);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "發送命令失敗：{Frame}", frame);
            return null;
        }
        finally
        {
            if (!skipLock)
            {
                _commandLock.Release();
            }
        }
    }

    #endregion

    #region ILightPort 實作

    public async Task SetIntensityAsync(int channel, int value, CancellationToken cancellationToken)
    {
        ValidateChannel(channel);
        value = Math.Clamp(value, 0, 255);

        var frame = MakeAsciiFrame(3, channel, value);
        var response = await SendAndReceiveAsync(frame);

        if (response == "$")
        {
            _logger.LogInformation("✓ 通道 {Channel} 亮度設為 {Value}", channel, value);
        }
        else if (response == "&")
        {
            _logger.LogError("✗ 設定亮度失敗（設備回覆 '&'）");
            throw new InvalidOperationException("設備拒絕命令");
        }
        else
        {
            _logger.LogWarning("設定亮度無回覆或超時");
        }
    }

    public async Task TurnAsync(int channel, bool on, CancellationToken cancellationToken)
    {
        ValidateChannel(channel);

        var cmd = on ? 1 : 2; // 1=開, 2=關
        var frame = MakeAsciiFrame(cmd, channel, null);
        var response = await SendAndReceiveAsync(frame);

        if (response == "$")
        {
            _logger.LogInformation("✓ 通道 {Channel} 已{State}", channel, on ? "開啟" : "關閉");
        }
        else if (response == "&")
        {
            _logger.LogError("✗ 開關操作失敗（設備回覆 '&'）");
            throw new InvalidOperationException("設備拒絕命令");
        }
        else
        {
            _logger.LogWarning("開關操作無回覆或超時");
        }
    }

    public async Task<LightState> GetStateAsync(CancellationToken cancellationToken)
    {
        var snapshot = new Dictionary<int, int>(_channelCount);

        for (int ch = 1; ch <= _channelCount; ch++)
        {
            try
            {
                var frame = MakeAsciiFrame(4, ch, null); // 命令 4：讀取亮度
                var response = await SendAndReceiveAsync(frame, isPolling: true); // 輪詢命令不輸出 Debug 日誌

                if (response != null && response.StartsWith("$4"))
                {
                    // 解析回覆：$4 + 通道(1) + 亮度(3位HEX) + XOR(2)
                    // 例：$410C876 → CH1 亮度=0xC8(200)
                    if (response.Length >= 6)
                    {
                        var dataHex = response.Substring(3, 3); // 取 "0C8"
                        if (dataHex.StartsWith("0") && int.TryParse(dataHex.Substring(1),
                            System.Globalization.NumberStyles.HexNumber, null, out int brightness))
                        {
                            snapshot[ch] = brightness;
                        }
                        else
                        {
                            _logger.LogWarning("無法解析通道 {Channel} 的亮度資料：{Data}", ch, dataHex);
                            snapshot[ch] = 0;
                        }
                    }
                    else
                    {
                        snapshot[ch] = 0;
                    }
                }
                else if (response == "&")
                {
                    _logger.LogWarning("讀取通道 {Channel} 失敗（設備回覆 '&'）", ch);
                    snapshot[ch] = 0;
                }
                else
                {
                    snapshot[ch] = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "讀取通道 {Channel} 發生錯誤", ch);
                snapshot[ch] = 0;
            }
        }

        return new LightState((_client?.Connected ?? false) && _protocolConfirmed, snapshot);
    }

    public Task SetModeAsync(LightWorkMode mode, CancellationToken cancellationToken)
    {
        _logger.LogWarning("ASCII 協定不支援工作模式設定");
        return Task.CompletedTask;
    }

    public Task<bool> SetHeartbeatAsync(bool enable, CancellationToken cancellationToken)
    {
        _logger.LogWarning("ASCII 協定不支援心跳設定");
        return Task.FromResult(false);
    }

    public Task SetTriggerPolarityAsync(LightTriggerPolarity polarity, CancellationToken cancellationToken)
    {
        _logger.LogWarning("ASCII 協定不支援觸發極性設定");
        return Task.CompletedTask;
    }

    public Task<LightDeviceInfo> ReadDeviceInfoAsync(CancellationToken cancellationToken)
    {
        var info = new LightDeviceInfo(
            FirmwareVersion: "ASCII Protocol",
            SerialNumber: "N/A",
            ChannelCount: _channelCount,
            WorkMode: LightWorkMode.Constant,
            TriggerPolarity: LightTriggerPolarity.RisingEdge,
            HeartbeatEnabled: false,
            IsOnline: (_client?.Connected ?? false) && _protocolConfirmed
        );

        return Task.FromResult(info);
    }

    public Task<LightNetworkProfile> ReadNetworkProfileAsync(CancellationToken cancellationToken)
    {
        var profile = new LightNetworkProfile(
            DeviceIp: "N/A",
            GatewayIp: "N/A",
            DevicePort: _listenPort
        );

        return Task.FromResult(profile);
    }

    public Task WriteNetworkProfileAsync(LightNetworkProfile profile, CancellationToken cancellationToken)
    {
        _logger.LogWarning("ASCII 協定不支援網路參數寫入");
        return Task.CompletedTask;
    }

    public Task BackupParametersAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning("ASCII 協定不支援參數備份");
        return Task.CompletedTask;
    }

    #endregion

    #region 輔助方法

    private void ValidateChannel(int channel)
    {
        if (channel < 1 || channel > _channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel),
                $"通道範圍需介於 1~{_channelCount}");
        }
    }

    #endregion

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation("正在關閉 LtsAsciiLightPort...");

        _disposeCts.Cancel();

        _stream?.Close();
        _client?.Close();
        _listener?.Stop();

        if (_listenTask != null)
        {
            try
            {
                await _listenTask;
            }
            catch (OperationCanceledException)
            {
                // 預期的取消
            }
        }

        _connectionLock.Dispose();
        _commandLock.Dispose();
        _disposeCts.Dispose();

        _logger.LogInformation("✓ LtsAsciiLightPort 已釋放");
    }

    #endregion

    #region Auto Run 亮度控制（TCP 版本不支援，提供空實作）

    /// <summary>
    /// 設定為工作亮度 - TCP 版本不支援自動亮度控制
    /// </summary>
    public Task SetWorkingBrightnessAsync(CancellationToken cancellationToken = default)
    {
        // TCP 版本不支援自動亮度控制，直接返回
        return Task.CompletedTask;
    }

    /// <summary>
    /// 設定為待機亮度 - TCP 版本不支援自動亮度控制
    /// </summary>
    public Task SetIdleBrightnessAsync(CancellationToken cancellationToken = default)
    {
        // TCP 版本不支援自動亮度控制，直接返回
        return Task.CompletedTask;
    }

    /// <summary>
    /// 當前是否為工作亮度模式 - TCP 版本固定返回 false
    /// </summary>
    public bool IsWorkingBrightness => false;

    #endregion
}
