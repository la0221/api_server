using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Devices.Plc.Modbus;

/// <summary>
/// PLC 專用 Modbus TCP 客戶端
/// 支援 FC01 (Read Coils), FC02 (Read Discrete Inputs), FC03 (Read Holding Registers),
/// FC05 (Write Single Coil), FC06 (Write Single Register),
/// FC15 (Write Multiple Coils), FC16 (Write Multiple Registers)
/// </summary>
internal sealed class PlcModbusTcpClient : IAsyncDisposable
{
    private readonly string _ip;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly int _timeoutMs;
    private readonly ILogger _logger;
    private TcpClient? _tcpClient;
    private ushort _transactionId;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    // TIME_WAIT 重連機制配置
    private const int MaxReconnectAttempts = 5;
    private const int InitialReconnectDelayMs = 500;
    private const int MaxReconnectDelayMs = 5000;
    private DateTime _lastDisconnectTime = DateTime.MinValue;

    public bool IsConnected => _tcpClient?.Connected ?? false;

    public PlcModbusTcpClient(string ip, int port, byte unitId, int timeoutMs, ILogger logger)
    {
        _ip = ip ?? throw new ArgumentNullException(nameof(ip));
        _port = port;
        _unitId = unitId;
        _timeoutMs = Math.Clamp(timeoutMs, 500, 10000);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 主動建立連線（含 TIME_WAIT 重試機制）
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_tcpClient is { Connected: true })
        {
            return;
        }

        // 完全釋放舊連線資源
        await ResetConnectionAsync().ConfigureAwait(false);

        // 檢查是否需要等待 TIME_WAIT 釋放
        var timeSinceDisconnect = (DateTime.Now - _lastDisconnectTime).TotalMilliseconds;
        if (_lastDisconnectTime != DateTime.MinValue && timeSinceDisconnect < InitialReconnectDelayMs)
        {
            var waitTime = (int)(InitialReconnectDelayMs - timeSinceDisconnect);
            _logger.LogDebug("等待 TIME_WAIT 釋放: {WaitMs}ms", waitTime);
            await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
        }

        Exception? lastException = null;
        var delay = InitialReconnectDelayMs;

        for (var attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            try
            {
                await TryConnectOnceAsync(cancellationToken).ConfigureAwait(false);
                return; // 連線成功
            }
            catch (OperationCanceledException)
            {
                throw; // 取消操作直接拋出
            }
            catch (Exception ex)
            {
                lastException = ex;

                // 檢查是否為 TIME_WAIT 相關錯誤
                var isTimeWaitError = IsTimeWaitRelatedError(ex);

                if (attempt >= MaxReconnectAttempts)
                {
                    _logger.LogError(ex, "連接 PLC 失敗，已達最大重試次數 {Attempts}", MaxReconnectAttempts);
                    break;
                }

                if (isTimeWaitError)
                {
                    _logger.LogWarning("TIME_WAIT 問題，第 {Attempt}/{Max} 次重試，等待 {Delay}ms...",
                        attempt, MaxReconnectAttempts, delay);
                }
                else
                {
                    _logger.LogWarning(ex, "連接 PLC 失敗，第 {Attempt}/{Max} 次重試，等待 {Delay}ms...",
                        attempt, MaxReconnectAttempts, delay);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                // 指數退避，但不超過最大延遲
                delay = Math.Min(delay * 2, MaxReconnectDelayMs);
            }
        }

        throw lastException ?? new IOException($"無法連接到 PLC {_ip}:{_port}");
    }

    /// <summary>
    /// 單次連線嘗試
    /// </summary>
    private async Task TryConnectOnceAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("正在連接到 PLC {IP}:{Port}", _ip, _port);

        _tcpClient = new TcpClient();

        // 設定 Socket 選項 - 允許重用 TIME_WAIT 狀態的位址
        _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // 設定 Linger 選項 - 關閉時立即釋放資源
        _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 0));

        // 禁用 Nagle 演算法，減少延遲
        _tcpClient.Client.NoDelay = true;

        _tcpClient.ReceiveTimeout = _timeoutMs;
        _tcpClient.SendTimeout = _timeoutMs;

        using var timeoutCts = new CancellationTokenSource(_timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            await _tcpClient.ConnectAsync(_ip, _port, linkedCts.Token).ConfigureAwait(false);
            _logger.LogInformation("成功連接到 PLC {IP}:{Port}", _ip, _port);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("連接到 PLC {IP}:{Port} 超時（{Timeout}ms）", _ip, _port, _timeoutMs);
            await ResetConnectionAsync().ConfigureAwait(false);
            throw new TimeoutException($"連接到 PLC {_ip}:{_port} 超時（{_timeoutMs}ms）", ex);
        }
        catch (SocketException ex)
        {
            _logger.LogError("連接 PLC 失敗 {IP}:{Port} - {Message}", _ip, _port, ex.Message);
            await ResetConnectionAsync().ConfigureAwait(false);
            throw new IOException($"無法連接到 PLC {_ip}:{_port} - {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 判斷是否為 TIME_WAIT 相關錯誤
    /// </summary>
    private static bool IsTimeWaitRelatedError(Exception ex)
    {
        if (ex is SocketException socketEx)
        {
            // 常見的 TIME_WAIT 相關錯誤碼
            return socketEx.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => true,      // 10048
                SocketError.ConnectionRefused => true,         // 10061
                SocketError.TimedOut => true,                  // 10060
                SocketError.NetworkUnreachable => true,        // 10051
                SocketError.HostUnreachable => true,           // 10065
                _ => false
            };
        }

        return ex is IOException ioEx && ioEx.InnerException is SocketException;
    }

    /// <summary>
    /// 斷開連線
    /// </summary>
    public void Disconnect()
    {
        ResetConnectionSync();
        _lastDisconnectTime = DateTime.Now;
        _logger.LogInformation("已斷開 PLC 連線");
    }

    /// <summary>
    /// 非同步斷開連線
    /// </summary>
    public async Task DisconnectAsync()
    {
        await ResetConnectionAsync().ConfigureAwait(false);
        _lastDisconnectTime = DateTime.Now;
        _logger.LogInformation("已斷開 PLC 連線");
    }

    #region Coil 操作 (FC01, FC05, FC15)

    /// <summary>
    /// 讀取 Coil (FC01)
    /// </summary>
    public async Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort count, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        WriteBigEndian(payload, 0, startAddress);
        WriteBigEndian(payload, 2, count);

        var response = await SendRequestAsync(0x01, payload, cancellationToken).ConfigureAwait(false);
        if (response.Length < 2)
        {
            throw new InvalidOperationException("FC01 回覆長度不足");
        }

        var byteCount = response[1];
        if (byteCount > 250)
        {
            throw new InvalidOperationException($"FC01 回覆位元組計數超出 Modbus 限制: {byteCount}");
        }
        if (response.Length < 2 + byteCount)
        {
            throw new InvalidOperationException($"FC01 回覆長度不足，預期 {byteCount} 位元組");
        }

        var coils = new bool[count];
        for (int i = 0; i < count; i++)
        {
            var byteIndex = i / 8;
            var bitIndex = i % 8;
            coils[i] = (response[2 + byteIndex] & (1 << bitIndex)) != 0;
        }
        return coils;
    }

    /// <summary>
    /// 寫入單一 Coil (FC05)
    /// </summary>
    public async Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        WriteBigEndian(payload, 0, address);
        WriteBigEndian(payload, 2, (ushort)(value ? 0xFF00 : 0x0000));

        await SendRequestAsync(0x05, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 寫入多個 Coil (FC15)
    /// </summary>
    public async Task WriteMultipleCoilsAsync(ushort startAddress, bool[] values, CancellationToken cancellationToken)
    {
        if (values.Length == 0)
        {
            return;
        }

        var byteCount = (values.Length + 7) / 8;
        var payload = new byte[5 + byteCount];
        WriteBigEndian(payload, 0, startAddress);
        WriteBigEndian(payload, 2, (ushort)values.Length);
        payload[4] = (byte)byteCount;

        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
            {
                var byteIndex = i / 8;
                var bitIndex = i % 8;
                payload[5 + byteIndex] |= (byte)(1 << bitIndex);
            }
        }

        await SendRequestAsync(0x0F, payload, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Discrete Input 操作 (FC02)

    /// <summary>
    /// 讀取 Discrete Input (FC02)
    /// </summary>
    public async Task<bool[]> ReadDiscreteInputsAsync(ushort startAddress, ushort count, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        WriteBigEndian(payload, 0, startAddress);
        WriteBigEndian(payload, 2, count);

        var response = await SendRequestAsync(0x02, payload, cancellationToken).ConfigureAwait(false);
        if (response.Length < 2)
        {
            throw new InvalidOperationException("FC02 回覆長度不足");
        }

        var byteCount = response[1];
        if (byteCount > 250)
        {
            throw new InvalidOperationException($"FC02 回覆位元組計數超出 Modbus 限制: {byteCount}");
        }
        if (response.Length < 2 + byteCount)
        {
            throw new InvalidOperationException($"FC02 回覆長度不足，預期 {byteCount} 位元組");
        }

        var inputs = new bool[count];
        for (int i = 0; i < count; i++)
        {
            var byteIndex = i / 8;
            var bitIndex = i % 8;
            inputs[i] = (response[2 + byteIndex] & (1 << bitIndex)) != 0;
        }
        return inputs;
    }

    #endregion

    #region Register 操作 (FC03, FC06, FC16)

    /// <summary>
    /// 讀取 Holding Register (FC03)
    /// </summary>
    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        WriteBigEndian(payload, 0, startAddress);
        WriteBigEndian(payload, 2, count);

        var response = await SendRequestAsync(0x03, payload, cancellationToken).ConfigureAwait(false);
        if (response.Length < 2)
        {
            throw new InvalidOperationException("FC03 回覆長度不足");
        }

        var byteCount = response[1];
        if (byteCount > 250)
        {
            throw new InvalidOperationException($"FC03 回覆位元組計數超出 Modbus 限制: {byteCount}");
        }
        if (response.Length < 2 + byteCount)
        {
            throw new InvalidOperationException($"FC03 回覆長度不足，預期 {byteCount} 位元組");
        }

        var registers = new ushort[byteCount / 2];
        for (int i = 0; i < registers.Length; i++)
        {
            registers[i] = ReadBigEndian(response, 2 + (i * 2));
        }
        return registers;
    }

    /// <summary>
    /// 寫入單一 Register (FC06)
    /// </summary>
    public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        WriteBigEndian(payload, 0, address);
        WriteBigEndian(payload, 2, value);

        await SendRequestAsync(0x06, payload, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region 內部方法

    private async Task<byte[]> SendRequestAsync(byte functionCode, byte[] payload, CancellationToken cancellationToken)
    {
        var stream = await GetStreamAsync(cancellationToken).ConfigureAwait(false);
        var requestLength = 7 + 1 + payload.Length;
        var request = new byte[requestLength];

        var txId = unchecked(++_transactionId);
        WriteBigEndian(request, 0, txId);           // Transaction ID
        WriteBigEndian(request, 2, 0);              // Protocol ID (0 = Modbus)
        WriteBigEndian(request, 4, (ushort)(payload.Length + 2)); // Length
        request[6] = _unitId;                       // Unit ID
        request[7] = functionCode;                  // Function Code
        Buffer.BlockCopy(payload, 0, request, 8, payload.Length);

        // 使用超時等待 Semaphore，避免無限等待
        // 超時時間為配置的超時時間 + 1 秒緩衝
        var lockTimeout = _timeoutMs + 1000;
        var acquired = await _ioLock.WaitAsync(lockTimeout, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            _logger.LogWarning("等待 Modbus IO 鎖超時 ({Timeout}ms)，可能有其他操作卡住", lockTimeout);
            throw new TimeoutException($"等待 Modbus IO 鎖超時 ({lockTimeout}ms)");
        }
        try
        {
            _logger.LogTrace("發送 Modbus 請求: FC={FC:X2}, Addr={Addr}", functionCode, payload.Length >= 2 ? ReadBigEndian(payload, 0) : 0);

            // 建立獨立的超時 CancellationToken，確保 I/O 操作不會無限等待
            using var timeoutCts = new CancellationTokenSource(_timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            await stream.WriteAsync(request, linkedCts.Token).ConfigureAwait(false);

            var header = await ReadExactAsync(stream, 7, linkedCts.Token).ConfigureAwait(false);
            var responseLength = ReadBigEndian(header, 4);
            var response = await ReadExactAsync(stream, responseLength - 1, linkedCts.Token).ConfigureAwait(false);

            var responseFunction = response[0];
            if ((responseFunction & 0x80) != 0)
            {
                var exceptionCode = response.Length > 1 ? response[1] : (byte)0xFF;
                _logger.LogWarning("Modbus 錯誤回覆: FC=0x{FC:X2}, Exception=0x{Ex:X2}", responseFunction, exceptionCode);
                throw new InvalidOperationException($"Modbus 錯誤，功能碼 0x{responseFunction:X2}，Exception Code 0x{exceptionCode:X2}");
            }

            return response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 是 I/O 超時，不是外部取消
            _logger.LogWarning("Modbus I/O 操作超時 ({Timeout}ms)", _timeoutMs);
            ResetConnectionSync();
            _lastDisconnectTime = DateTime.Now;
            throw new TimeoutException($"Modbus I/O 操作超時 ({_timeoutMs}ms)", ex);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Modbus IO 錯誤");
            ResetConnectionSync();
            _lastDisconnectTime = DateTime.Now;
            throw;
        }
        catch (ObjectDisposedException)
        {
            ResetConnectionSync();
            _lastDisconnectTime = DateTime.Now;
            throw;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<NetworkStream> GetStreamAsync(CancellationToken cancellationToken)
    {
        if (_tcpClient is { Connected: true })
        {
            return _tcpClient.GetStream();
        }

        // 嘗試重新連線
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return _tcpClient!.GetStream();
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Modbus 連線已關閉");
            }
            offset += read;
        }

        return buffer;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }

    private static ushort ReadBigEndian(byte[] buffer, int offset) =>
        (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    /// <summary>
    /// 同步重置連線（用於同步斷線場景）
    /// </summary>
    private void ResetConnectionSync()
    {
        var client = Interlocked.Exchange(ref _tcpClient, null);
        if (client == null) return;

        try
        {
            // 嘗試優雅關閉 Socket
            if (client.Client?.Connected == true)
            {
                try
                {
                    client.Client.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                    // 忽略 shutdown 錯誤
                }
            }

            client.Close();
            client.Dispose();
        }
        catch
        {
            // 忽略清理過程中的錯誤
        }
    }

    /// <summary>
    /// 非同步重置連線（推薦使用）
    /// </summary>
    private async Task ResetConnectionAsync()
    {
        var client = Interlocked.Exchange(ref _tcpClient, null);
        if (client == null) return;

        try
        {
            // 嘗試優雅關閉 Socket
            if (client.Client?.Connected == true)
            {
                try
                {
                    client.Client.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                    // 忽略 shutdown 錯誤
                }
            }

            client.Close();
            client.Dispose();

            // 短暫等待，讓 OS 有時間釋放資源
            await Task.Delay(50).ConfigureAwait(false);
        }
        catch
        {
            // 忽略清理過程中的錯誤
        }
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        await ResetConnectionAsync().ConfigureAwait(false);
        _ioLock.Dispose();
    }
}
