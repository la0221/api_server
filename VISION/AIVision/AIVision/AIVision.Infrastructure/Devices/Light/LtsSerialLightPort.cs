using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Devices.Light;

/// <summary>
/// LTS 光源控制器 RS232 串口實作
/// 使用與 TCP 版本相同的 ASCII 協定
/// </summary>
public sealed class LtsSerialLightPort : ILightPort, IAsyncDisposable
{
    private readonly ILogger<LtsSerialLightPort> _logger;
    private readonly int _channelCount;
    private readonly int _timeoutMs;
    private readonly int _commandIntervalMs;
    private LightBrightnessControlOptions? _brightnessControl;

    private SerialPort? _serialPort;
    private string _currentPortName = "";
    private int _currentBaudRate = 19200;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private bool _disposed;
    private bool _isConnected;

    // Auto Run 亮度控制狀態
    private bool _isWorkingBrightness = false; // 預設待機模式（0%）

    public bool IsWorkingBrightness => _isWorkingBrightness;

    public LtsSerialLightPort(
        ILogger<LtsSerialLightPort> logger,
        int channelCount = 2,
        int timeoutMs = 1000,
        int commandIntervalMs = 20,
        LightBrightnessControlOptions? brightnessControl = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channelCount = channelCount;
        _timeoutMs = timeoutMs;
        _commandIntervalMs = commandIntervalMs;
        _brightnessControl = brightnessControl;

        _logger.LogInformation("LtsSerialLightPort 初始化：通道數={Channels}，亮度控制={Enabled}",
            _channelCount, _brightnessControl?.Enabled ?? false);
    }

    #region 連線管理

    /// <summary>
    /// 連接到指定串口
    /// </summary>
    public async Task<bool> ConnectAsync(string portName, int baudRate = 19200, CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            // 如果已連接到相同串口，直接返回
            if (_serialPort?.IsOpen == true && _currentPortName == portName)
            {
                return true;
            }

            // 關閉舊連接
            CloseSerialPort();

            _currentPortName = portName;
            _currentBaudRate = baudRate;

            _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = _timeoutMs,
                WriteTimeout = _timeoutMs,
                Encoding = Encoding.ASCII
            };

            _serialPort.Open();
            _isConnected = true;
            _logger.LogInformation("✓ 串口 {Port} 已開啟 (Baud: {BaudRate})", portName, baudRate);

            // 發送探針確認連接
            await ProbeDeviceAsync();

            return true;
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _logger.LogError(ex, "開啟串口 {Port} 失敗", portName);
            return false;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// 斷開連接
    /// </summary>
    public void Disconnect()
    {
        CloseSerialPort();
        _isConnected = false;
        _logger.LogInformation("串口已斷開");
    }

    private void CloseSerialPort()
    {
        try
        {
            if (_serialPort != null)
            {
                if (_serialPort.IsOpen)
                {
                    _serialPort.Close();
                }
                _serialPort.Dispose();
                _serialPort = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "關閉串口時發生錯誤");
        }
    }

    private async Task ProbeDeviceAsync()
    {
        try
        {
            var probeFrame = MakeAsciiFrame(4, 1, null);
            _logger.LogDebug("發送探針：{Frame}", probeFrame);

            var response = await SendAndReceiveInternalAsync(probeFrame, skipLock: true);

            if (response != null && response.StartsWith("$"))
            {
                _logger.LogInformation("✓ ASCII 協定確認成功");
            }
            else
            {
                _logger.LogWarning("探針測試無預期回覆：{Response}", response ?? "(timeout)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "探針測試失敗");
        }
    }

    /// <summary>
    /// 取得可用的 COM Port 列表
    /// </summary>
    public static string[] GetAvailablePorts()
    {
        return SerialPort.GetPortNames();
    }

    public bool IsConnected => _isConnected && (_serialPort?.IsOpen ?? false);
    public string CurrentPortName => _currentPortName;
    public int CurrentBaudRate => _currentBaudRate;

    #endregion

    #region ASCII 協定核心（與 TCP 版本相同）

    private static string CalculateXor(string frameBody)
    {
        int xor = 0;
        foreach (char ch in frameBody)
        {
            xor ^= (byte)ch;
        }
        return xor.ToString("X2");
    }

    private static string MakeAsciiFrame(int cmd, int channel, int? brightness)
    {
        if (cmd < 1 || cmd > 6)
            throw new ArgumentOutOfRangeException(nameof(cmd), "命令範圍 1-6");

        if (channel < 1 || channel > 7)
            throw new ArgumentOutOfRangeException(nameof(channel), "通道範圍 1-7");

        string data;
        if (cmd == 3 && brightness.HasValue)
        {
            int value = Math.Clamp(brightness.Value, 0, 255);
            data = $"0{value:X2}";
        }
        else
        {
            data = "000";
        }

        string body = $"${cmd}{channel}{data}";
        string xor = CalculateXor(body);
        return body + xor;
    }

    private async Task<string?> SendAndReceiveAsync(string frame, bool isPolling = false)
    {
        await _commandLock.WaitAsync();
        try
        {
            return await SendAndReceiveInternalAsync(frame, skipLock: true, isPolling: isPolling);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<string?> SendAndReceiveInternalAsync(string frame, bool skipLock = false, bool isPolling = false)
    {
        try
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                _logger.LogWarning("串口未開啟");
                _isConnected = false;
                return null;
            }

            // 清空緩衝區
            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();

            // 發送
            _serialPort.Write(frame);

            if (!isPolling)
            {
                _logger.LogDebug("[TX] {Frame}", frame);
            }

            // 延遲
            await Task.Delay(_commandIntervalMs);

            // 接收
            try
            {
                var buffer = new byte[64];
                int bytesRead = _serialPort.Read(buffer, 0, buffer.Length);

                if (bytesRead > 0)
                {
                    var response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();

                    if (!isPolling)
                    {
                        _logger.LogDebug("[RX] {Response}", response);
                    }

                    return response;
                }
            }
            catch (TimeoutException)
            {
                if (!isPolling)
                {
                    _logger.LogWarning("接收回覆超時 ({Timeout}ms)", _timeoutMs);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "發送命令失敗：{Frame}", frame);
            _isConnected = false;
            return null;
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

        var cmd = on ? 1 : 2;
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
        bool connected = IsConnected;

        for (int ch = 1; ch <= _channelCount; ch++)
        {
            try
            {
                var frame = MakeAsciiFrame(4, ch, null);
                var response = await SendAndReceiveAsync(frame, isPolling: true);

                if (response != null && response.StartsWith("$4"))
                {
                    if (response.Length >= 6)
                    {
                        var dataHex = response.Substring(3, 3);
                        if (dataHex.StartsWith("0") && int.TryParse(dataHex.Substring(1),
                            System.Globalization.NumberStyles.HexNumber, null, out int brightness))
                        {
                            snapshot[ch] = brightness;
                            connected = true;
                        }
                        else
                        {
                            snapshot[ch] = 0;
                        }
                    }
                    else
                    {
                        snapshot[ch] = 0;
                    }
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

        _isConnected = connected;
        return new LightState(connected, snapshot);
    }

    public Task SetModeAsync(LightWorkMode mode, CancellationToken cancellationToken)
    {
        _logger.LogWarning("RS232 ASCII 協定不支援工作模式設定");
        return Task.CompletedTask;
    }

    public Task<bool> SetHeartbeatAsync(bool enable, CancellationToken cancellationToken)
    {
        _logger.LogWarning("RS232 ASCII 協定不支援心跳設定");
        return Task.FromResult(false);
    }

    public Task SetTriggerPolarityAsync(LightTriggerPolarity polarity, CancellationToken cancellationToken)
    {
        _logger.LogWarning("RS232 ASCII 協定不支援觸發極性設定");
        return Task.CompletedTask;
    }

    public Task<LightDeviceInfo> ReadDeviceInfoAsync(CancellationToken cancellationToken)
    {
        var info = new LightDeviceInfo(
            FirmwareVersion: "RS232 ASCII",
            SerialNumber: _currentPortName,
            ChannelCount: _channelCount,
            WorkMode: LightWorkMode.Constant,
            TriggerPolarity: LightTriggerPolarity.RisingEdge,
            HeartbeatEnabled: false,
            IsOnline: IsConnected
        );

        return Task.FromResult(info);
    }

    public Task<LightNetworkProfile> ReadNetworkProfileAsync(CancellationToken cancellationToken)
    {
        var profile = new LightNetworkProfile(
            DeviceIp: $"RS232: {_currentPortName}",
            GatewayIp: "N/A",
            DevicePort: _currentBaudRate
        );

        return Task.FromResult(profile);
    }

    public Task WriteNetworkProfileAsync(LightNetworkProfile profile, CancellationToken cancellationToken)
    {
        _logger.LogWarning("RS232 模式不支援網路參數寫入");
        return Task.CompletedTask;
    }

    public Task BackupParametersAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning("RS232 ASCII 協定不支援參數備份");
        return Task.CompletedTask;
    }

    // === Auto Run 亮度控制 ===

    /// <summary>
    /// 更新亮度控制設定（用於專案載入時覆蓋 appsettings.json 的預設值）
    /// </summary>
    /// <param name="channelBrightnessMap">每個通道的獨立亮度設定</param>
    /// <param name="idleBrightness">待機亮度（預設 0）</param>
    public void UpdateBrightnessControl(Dictionary<int, int> channelBrightnessMap, int idleBrightness = 0)
    {
        _brightnessControl = new LightBrightnessControlOptions
        {
            Enabled = true,
            IdleBrightness = idleBrightness,
            Channels = channelBrightnessMap.Keys.ToArray(),
            ChannelBrightnessMap = channelBrightnessMap
        };

        var brightnessStr = string.Join(", ", channelBrightnessMap.Select(kv => $"CH{kv.Key}={kv.Value}"));
        _logger.LogInformation("✓ 亮度控制設定已更新: {Brightness}, 待機={Idle}",
            brightnessStr, idleBrightness);
    }

    /// <summary>
    /// 設定為工作亮度（15%）- 取像時使用
    /// </summary>
    public async Task SetWorkingBrightnessAsync(CancellationToken cancellationToken = default)
    {
        if (_isWorkingBrightness)
        {
            // 已經是工作亮度，跳過
            return;
        }

        if (_brightnessControl?.Enabled != true)
        {
            // 未啟用亮度控制，跳過
            return;
        }

        _logger.LogDebug("調整光源到工作亮度...");

        // 設定各通道到工作亮度（支援每通道獨立亮度）
        foreach (var channel in _brightnessControl.Channels)
        {
            if (channel >= 1 && channel <= _channelCount)
            {
                var brightness = _brightnessControl.GetChannelBrightness(channel);
                await SetIntensityAsync(channel, brightness, cancellationToken);
                _logger.LogDebug("通道 {Channel} 設定工作亮度: {Brightness}", channel, brightness);
            }
        }

        _isWorkingBrightness = true;
        _logger.LogInformation("✓ 光源已調整為工作亮度");
    }

    /// <summary>
    /// 設定為待機亮度（0%）- 待機時使用
    /// </summary>
    public async Task SetIdleBrightnessAsync(CancellationToken cancellationToken = default)
    {
        if (!_isWorkingBrightness)
        {
            // 已經是待機亮度，跳過
            return;
        }

        if (_brightnessControl?.Enabled != true)
        {
            // 未啟用亮度控制，跳過
            return;
        }

        _logger.LogDebug("調整光源到待機亮度 ({Value})", _brightnessControl.IdleBrightness);

        // 設定各通道到待機亮度
        foreach (var channel in _brightnessControl.Channels)
        {
            if (channel >= 1 && channel <= _channelCount)
            {
                await SetIntensityAsync(channel, _brightnessControl.IdleBrightness, cancellationToken);
            }
        }

        _isWorkingBrightness = false;
        _logger.LogInformation("✓ 光源已調整為待機亮度 ({Value})", _brightnessControl.IdleBrightness);
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

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _logger.LogInformation("正在關閉 LtsSerialLightPort...");

        CloseSerialPort();
        _commandLock.Dispose();

        _logger.LogInformation("✓ LtsSerialLightPort 已釋放");
        return ValueTask.CompletedTask;
    }

    #endregion
}
