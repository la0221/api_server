using AIVision.Application.Ports.Devices;
using AIVision.Infrastructure.Common;
using AIVision.Infrastructure.Devices.Plc.Modbus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.Devices.Plc.Communication;

/// <summary>
/// Modbus TCP PLC 通訊適配器
/// </summary>
public sealed class ModbusTcpPlcAdapter : IPlcCommunicationPort
{
    private readonly PlcConnectionOptions _options;
    private readonly ILogger<ModbusTcpPlcAdapter> _logger;
    private readonly ExponentialBackoff _reconnectBackoff;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private PlcModbusTcpClient? _client;
    private bool _isConnected;
    private string? _currentIp;
    private int _currentPort;

    // 心跳檢測相關
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private DateTime _lastSuccessfulCommunication = DateTime.MinValue;
    private int _consecutiveErrorCount;

    public bool IsConnected => _isConnected && (_client?.IsConnected ?? false);

    /// <summary>
    /// 最後一次成功通訊的時間
    /// </summary>
    public DateTime LastSuccessfulCommunication => _lastSuccessfulCommunication;

    /// <summary>
    /// 連續通訊錯誤次數
    /// </summary>
    public int ConsecutiveErrorCount => _consecutiveErrorCount;

    public event EventHandler<bool>? ConnectionStateChanged;

    public ModbusTcpPlcAdapter(
        IOptions<PlcConnectionOptions> options,
        ILogger<ModbusTcpPlcAdapter> logger)
    {
        _options = options.Value;
        _logger = logger;

        // 使用配置的重連參數初始化退避策略
        _reconnectBackoff = new ExponentialBackoff(
            initialDelayMs: _options.InitialReconnectDelayMs,
            maxDelayMs: _options.MaxReconnectDelayMs,
            multiplier: 2.0);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // 使用設定檔的預設值
        return ConnectAsync(_options.Ip, _options.Port, cancellationToken);
    }

    public async Task ConnectAsync(string ip, int port, CancellationToken cancellationToken = default)
    {
        // 使用連線鎖防止競爭條件
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 檢查是否已連線到相同的 IP:Port
            if (_client?.IsConnected == true && _currentIp == ip && _currentPort == port)
            {
                _logger.LogDebug("PLC 已連線到 {Ip}:{Port}，跳過連線請求", ip, port);
                return;
            }

            _logger.LogInformation("正在連線到 PLC {Ip}:{Port} (UnitId={UnitId})",
                ip, port, _options.UnitId);

            // 完全釋放舊連線資源
            if (_client != null)
            {
                _logger.LogDebug("釋放舊的 PLC 連線資源...");
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;

                // 等待一小段時間讓 Socket 資源完全釋放
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            _client = new PlcModbusTcpClient(
                ip,
                port,
                _options.UnitId,
                _options.ReadTimeoutMs,
                _logger);

            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            // 記錄當前連線參數
            _currentIp = ip;
            _currentPort = port;

            SetConnectionState(true);
            _reconnectBackoff.Reset(); // 連線成功，重置退避計數器
            _consecutiveErrorCount = 0; // 重置錯誤計數
            _lastSuccessfulCommunication = DateTime.Now;

            // 啟動心跳檢測
            StartHeartbeat();

            _logger.LogInformation("PLC 連線成功");
        }
        catch (Exception ex)
        {
            SetConnectionState(false);
            var delay = _reconnectBackoff.GetNextDelay();
            _logger.LogWarning(ex, "PLC 連線失敗，下次重試延遲 {DelayMs}ms (嘗試次數: {Attempt})",
                delay, _reconnectBackoff.CurrentAttempt);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// 帶有指數退避的重新連線
    /// </summary>
    public async Task ReconnectWithBackoffAsync(CancellationToken cancellationToken)
    {
        var delay = _reconnectBackoff.GetNextDelay();
        _logger.LogInformation("等待 {DelayMs}ms 後重新連線 (嘗試次數: {Attempt})",
            delay, _reconnectBackoff.CurrentAttempt);

        await Task.Delay(delay, cancellationToken);
        await ConnectAsync(cancellationToken);
    }

    public async Task DisconnectAsync()
    {
        // 先停止心跳檢測
        await StopHeartbeatAsync().ConfigureAwait(false);

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _logger.LogInformation("斷開 PLC 連線");
            if (_client != null)
            {
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
            _currentIp = null;
            _currentPort = 0;
            SetConnectionState(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    #region Coil 操作

    public async Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort count, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        try
        {
            _logger.LogTrace("ReadCoils: Modbus地址={Address} (0-based), 數量={Count}", startAddress, count);
            var result = await _client!.ReadCoilsAsync(startAddress, count, cancellationToken);
            _logger.LogTrace("ReadCoils 結果: [{Values}]", string.Join(",", result));
            OnCommunicationSuccess();
            return result;
        }
        catch (Exception ex)
        {
            HandleCommunicationError(ex);
            throw;
        }
    }

    public async Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        try
        {
            await _client!.WriteSingleCoilAsync(address, value, cancellationToken);
            _logger.LogDebug("寫入 Coil[{Address}] = {Value}", address, value);
            OnCommunicationSuccess();
        }
        catch (Exception ex)
        {
            HandleCommunicationError(ex);
            throw;
        }
    }

    public async Task WriteMultipleCoilsAsync(ushort startAddress, bool[] values, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        try
        {
            await _client!.WriteMultipleCoilsAsync(startAddress, values, cancellationToken);
            _logger.LogDebug("寫入 Coils[{StartAddress}], 數量={Count}", startAddress, values.Length);
            OnCommunicationSuccess();
        }
        catch (Exception ex)
        {
            HandleCommunicationError(ex);
            throw;
        }
    }

    #endregion

    #region Discrete Input 操作

    public async Task<bool[]> ReadDiscreteInputsAsync(ushort startAddress, ushort count, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        try
        {
            var result = await _client!.ReadDiscreteInputsAsync(startAddress, count, cancellationToken);
            OnCommunicationSuccess();
            return result;
        }
        catch (Exception ex)
        {
            HandleCommunicationError(ex);
            throw;
        }
    }

    #endregion

    #region Register 操作

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        try
        {
            var result = await _client!.ReadHoldingRegistersAsync(startAddress, count, cancellationToken);
            OnCommunicationSuccess();
            return result;
        }
        catch (Exception ex)
        {
            HandleCommunicationError(ex);
            throw;
        }
    }

    public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        try
        {
            await _client!.WriteSingleRegisterAsync(address, value, cancellationToken);
            _logger.LogDebug("寫入 Register[{Address}] = {Value}", address, value);
            OnCommunicationSuccess();
        }
        catch (Exception ex)
        {
            HandleCommunicationError(ex);
            throw;
        }
    }

    #endregion

    private void EnsureConnected()
    {
        if (_client == null || !_client.IsConnected)
        {
            throw new InvalidOperationException("PLC 未連線，請先呼叫 ConnectAsync()");
        }
    }

    /// <summary>
    /// 通訊成功時的處理
    /// </summary>
    private void OnCommunicationSuccess()
    {
        _lastSuccessfulCommunication = DateTime.Now;
        Interlocked.Exchange(ref _consecutiveErrorCount, 0);
    }

    private void HandleCommunicationError(Exception ex)
    {
        var errorCount = Interlocked.Increment(ref _consecutiveErrorCount);
        _logger.LogWarning(ex, "PLC 通訊錯誤 (連續錯誤: {Count}/{Threshold})",
            errorCount, _options.ErrorThresholdForReconnect);

        if (ex is IOException || ex is TimeoutException)
        {
            SetConnectionState(false);
        }

        // 連續錯誤達到閾值時觸發重連事件
        if (errorCount >= _options.ErrorThresholdForReconnect)
        {
            _logger.LogWarning("連續通訊錯誤達到閾值 ({Threshold})，標記連線失效",
                _options.ErrorThresholdForReconnect);
            SetConnectionState(false);
        }
    }

    private void SetConnectionState(bool connected)
    {
        if (_isConnected != connected)
        {
            _isConnected = connected;
            ConnectionStateChanged?.Invoke(this, connected);
        }
    }

    #region 心跳檢測

    /// <summary>
    /// 啟動心跳檢測
    /// </summary>
    private void StartHeartbeat()
    {
        if (_options.HeartbeatIntervalMs <= 0)
        {
            _logger.LogDebug("心跳檢測已停用 (HeartbeatIntervalMs={Interval})", _options.HeartbeatIntervalMs);
            return;
        }

        StopHeartbeatSync();

        _heartbeatCts = new CancellationTokenSource();
        _heartbeatTask = HeartbeatLoopAsync(_heartbeatCts.Token);

        _logger.LogInformation("心跳檢測已啟動，間隔 {Interval}ms", _options.HeartbeatIntervalMs);
    }

    /// <summary>
    /// 停止心跳檢測（非同步）
    /// </summary>
    private async Task StopHeartbeatAsync()
    {
        if (_heartbeatCts == null)
        {
            return;
        }

        _heartbeatCts.Cancel();

        if (_heartbeatTask != null)
        {
            try
            {
                await _heartbeatTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 預期的取消
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("心跳檢測任務未能在 2 秒內停止");
            }
        }

        _heartbeatCts.Dispose();
        _heartbeatCts = null;
        _heartbeatTask = null;

        _logger.LogDebug("心跳檢測已停止");
    }

    /// <summary>
    /// 停止心跳檢測（同步）
    /// </summary>
    private void StopHeartbeatSync()
    {
        if (_heartbeatCts == null)
        {
            return;
        }

        _heartbeatCts.Cancel();
        _heartbeatCts.Dispose();
        _heartbeatCts = null;
        _heartbeatTask = null;
    }

    /// <summary>
    /// 心跳檢測迴圈
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatIntervalMs, ct).ConfigureAwait(false);

                if (_client?.IsConnected != true)
                {
                    continue;
                }

                // 檢查最後成功通訊時間
                var timeSinceLastSuccess = (DateTime.Now - _lastSuccessfulCommunication).TotalMilliseconds;

                // 如果超過 1.5 倍心跳間隔沒有成功通訊，主動進行心跳檢測
                // 縮短倍數以更積極地維持連線，避免 TCP 假死
                if (timeSinceLastSuccess > _options.HeartbeatIntervalMs * 1.5)
                {
                    _logger.LogDebug("執行心跳檢測 (距離上次成功通訊: {Ms:F0}ms)", timeSinceLastSuccess);

                    try
                    {
                        // 讀取一個無副作用的暫存器確認連線（使用地址 0，讀取 1 個暫存器）
                        await _client.ReadHoldingRegistersAsync(0, 1, ct).ConfigureAwait(false);
                        OnCommunicationSuccess();
                        _logger.LogTrace("心跳檢測成功");
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // 向上傳遞取消
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "心跳檢測失敗");
                        HandleCommunicationError(ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "心跳檢測迴圈異常");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        // 先停止心跳檢測
        await StopHeartbeatAsync().ConfigureAwait(false);

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client != null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
            _currentIp = null;
            _currentPort = 0;
            SetConnectionState(false);
        }
        finally
        {
            _connectionLock.Release();
            _connectionLock.Dispose();
        }
    }
}
