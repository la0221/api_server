using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Devices.Light.Modbus;

/// <summary>
/// Minimal Modbus TCP client tailored for the LTS light controller.
/// Supports FC03 (Read Holding Registers), FC06 (Write Single Register),
/// and FC10 (Write Multiple Registers).
/// </summary>
internal sealed class ModbusTcpClient : IAsyncDisposable
{
    private readonly string _ip;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly int _timeoutMs;
    private readonly ILogger? _logger;
    private TcpClient? _tcpClient;
    private ushort _transactionId;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public ModbusTcpClient(string ip, int port, byte unitId = 1, int timeoutMs = 1000, ILogger? logger = null)
    {
        _ip = ip ?? throw new ArgumentNullException(nameof(ip));
        _port = port;
        _unitId = unitId;
        _timeoutMs = Math.Clamp(timeoutMs, 200, 10000);
        _logger = logger;
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        WriteBigEndian(payload, 0, startAddress);
        WriteBigEndian(payload, 2, count);

        var response = await SendRequestAsync(0x03, payload, cancellationToken).ConfigureAwait(false);
        if (response.Length < 2)
        {
            throw new InvalidOperationException("回覆長度不足（缺少 byte count）。");
        }

        var byteCount = response[1];
        if (response.Length < 2 + byteCount)
        {
            throw new InvalidOperationException($"回覆長度不足，預期 {byteCount} 位元組，實際 {response.Length - 2}");
        }

        var registers = new ushort[byteCount / 2];
        for (int i = 0; i < registers.Length; i++)
        {
            registers[i] = ReadBigEndian(response, 2 + (i * 2));
        }
        return registers;
    }

    public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        WriteBigEndian(payload, 0, address);
        WriteBigEndian(payload, 2, value);

        return SendRequestAsync(0x06, payload, cancellationToken);
    }

    public Task WriteMultipleRegistersAsync(ushort startAddress, ReadOnlySpan<ushort> values, CancellationToken cancellationToken)
    {
        if (values.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var byteCount = values.Length * 2;
        var payload = new byte[5 + byteCount];
        WriteBigEndian(payload, 0, startAddress);
        WriteBigEndian(payload, 2, (ushort)values.Length);
        payload[4] = (byte)byteCount;

        for (int i = 0; i < values.Length; i++)
        {
            WriteBigEndian(payload, 5 + (i * 2), values[i]);
        }

        return SendRequestAsync(0x10, payload, cancellationToken);
    }

    private async Task<byte[]> SendRequestAsync(byte functionCode, byte[] payload, CancellationToken cancellationToken)
    {
        var stream = await GetStreamAsync(cancellationToken).ConfigureAwait(false);
        var requestLength = 7 + 1 + payload.Length;
        var request = new byte[requestLength];

        var txId = unchecked(++_transactionId);
        WriteBigEndian(request, 0, txId);
        WriteBigEndian(request, 2, 0);
        WriteBigEndian(request, 4, (ushort)(payload.Length + 2));
        request[6] = _unitId;
        request[7] = functionCode;
        Buffer.BlockCopy(payload, 0, request, 8, payload.Length);

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(request, 0, request.Length, cancellationToken).ConfigureAwait(false);

            var header = await ReadExactAsync(stream, 7, cancellationToken).ConfigureAwait(false);
            var responseLength = ReadBigEndian(header, 4);
            var response = await ReadExactAsync(stream, responseLength - 1, cancellationToken).ConfigureAwait(false);

            var responseFunction = response[0];
            if ((responseFunction & 0x80) != 0)
            {
                var exceptionCode = response.Length > 1 ? response[1] : (byte)0xFF;
                throw new InvalidOperationException($"Modbus 回覆錯誤，功能碼 0x{responseFunction:X2}，Exception Code 0x{exceptionCode:X2}");
            }

            return response;
        }
        catch (IOException)
        {
            ResetConnection();
            throw;
        }
        catch (ObjectDisposedException)
        {
            ResetConnection();
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

        ResetConnection();
        _logger?.LogDebug("正在連接到 Modbus TCP 服務器 {IP}:{Port}，超時 {Timeout}ms", _ip, _port, _timeoutMs);

        _tcpClient = new TcpClient
        {
            ReceiveTimeout = _timeoutMs,
            SendTimeout = _timeoutMs
        };

        using var timeoutCts = new CancellationTokenSource(_timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            await _tcpClient.ConnectAsync(_ip, _port).WaitAsync(linkedCts.Token).ConfigureAwait(false);
            _logger?.LogInformation("成功連接到 Modbus TCP 服務器 {IP}:{Port}", _ip, _port);
            return _tcpClient.GetStream();
        }
        catch (OperationCanceledException ex)
        {
            _logger?.LogError("連接到 {IP}:{Port} 超時（{Timeout}ms）", _ip, _port, _timeoutMs);
            ResetConnection();
            throw new TimeoutException($"連接到 {_ip}:{_port} 超時（{_timeoutMs}ms）", ex);
        }
        catch (SocketException ex)
        {
            _logger?.LogError("Socket 連接失敗 {IP}:{Port} - {Message}", _ip, _port, ex.Message);
            ResetConnection();
            throw new IOException($"無法連接到 {_ip}:{_port} - {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "連接到 {IP}:{Port} 時發生未預期的錯誤", _ip, _port);
            ResetConnection();
            throw;
        }
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer, offset, count - offset, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Modbus 連線已關閉。");
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

    private void ResetConnection()
    {
        try
        {
            _tcpClient?.Close();
        }
        catch
        {
        }
        finally
        {
            _tcpClient = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        ResetConnection();
        _ioLock.Dispose();
        if (_tcpClient != null)
        {
            await Task.Run(() => _tcpClient.Dispose()).ConfigureAwait(false);
        }
    }
}
