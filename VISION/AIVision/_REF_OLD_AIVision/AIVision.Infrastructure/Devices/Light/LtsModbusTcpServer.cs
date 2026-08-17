using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace AIVision.Infrastructure.Devices.Light;

/// <summary>
/// LTS 光源控制器 Modbus TCP 服務器
/// 
/// 功能：監聽設備連接，處理 Modbus TCP 通訊
/// 
/// 連接流程：
/// 1. 服務器監聽在 192.168.1.90:8000
/// 2. 設備 (192.168.1.95) 主動連接
/// 3. DLL (127.0.0.1) 連接到本地監聽
/// 4. 三方建立通訊
/// </summary>
public sealed class LtsModbusTcpServer : IAsyncDisposable
{
    private readonly ILogger<LtsModbusTcpServer> _logger;
    private readonly string _listeningIp;
    private readonly int _listeningPort;
    
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    public LtsModbusTcpServer(
        ILogger<LtsModbusTcpServer> logger,
        string listeningIp = "192.168.1.90",
        int listeningPort = 8000)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _listeningIp = listeningIp;
        _listeningPort = listeningPort;
    }

    /// <summary>
    /// 啟動服務器，開始監聽設備連接
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_listener != null)
        {
            _logger.LogWarning("服務器已啟動，忽略重複啟動");
            return;
        }

        try
        {
            _logger.LogInformation(
                "正在啟動 Modbus TCP 服務器...\n" +
                "  監聽地址: {ListeningIp}:{ListeningPort}",
                _listeningIp, _listeningPort);

            // 創建 TCP 監聽器
            var endPoint = new IPEndPoint(IPAddress.Parse(_listeningIp), _listeningPort);
            _listener = new TcpListener(endPoint);

            // 設置 ReuseAddress 選項，允許立即重新綁定端口
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            _listener.Start();

            _logger.LogInformation("✓ 服務器已啟動，等待設備連接...");

            // 後台監聽任務
            _cts = new CancellationTokenSource();
            _listenTask = ListenForConnectionsAsync(_cts.Token);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            _logger.LogError(ex,
                "啟動服務器失敗：端口 {Port} 已被佔用。" +
                "請檢查是否有其他應用程式實例正在運行，或使用 'netstat -ano | findstr :{Port}' 查看佔用該端口的程式",
                _listeningPort);
            _listener = null;
            throw new InvalidOperationException($"端口 {_listeningPort} 已被佔用，無法啟動 Modbus TCP 服務器", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "啟動服務器失敗");
            _listener?.Stop();
            _listener = null;
            throw;
        }
    }

    /// <summary>
    /// 後台監聽連接
    /// </summary>
    private async Task ListenForConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _listener != null)
            {
                try
                {
                    // 接受連接（非同步）
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "未知";
                    
                    _logger.LogInformation("✓ 設備已連接: {RemoteIp}", remoteIp);

                    // 後台處理客戶端（暫時只記錄連接）
                    _ = HandleClientAsync(client, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "接受連接時發生錯誤");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "監聽循環發生異常");
        }
        finally
        {
            _logger.LogInformation("服務器已停止監聽");
        }
    }

    /// <summary>
    /// 處理單個客戶端連接
    /// </summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                // 設置讀寫超時
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;

                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "未知";
                _logger.LogInformation("客戶端 {RemoteIp} 已連接", remoteIp);

                // 讀取數據並轉發（暫時實現簡單的轉發）
                var buffer = new byte[4096];
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        if (bytesRead == 0)
                        {
                            _logger.LogInformation("客戶端 {RemoteIp} 已斷開", remoteIp);
                            break;
                        }

                        _logger.LogDebug("收到 {RemoteIp} 的數據: {ByteCount} 位元組", remoteIp, bytesRead);
                        
                        // 這裡應該實現 Modbus 協議處理
                        // 暫時只是轉發
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "處理客戶端 {RemoteIp} 數據時發生錯誤", remoteIp);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "處理客戶端連接時發生異常");
        }
    }

    /// <summary>
    /// 停止服務器
    /// </summary>
    public async Task StopAsync()
    {
        if (_listener == null) return;

        _logger.LogInformation("正在停止服務器...");

        try
        {
            _listener.Stop();
            _listener = null;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            if (_listenTask != null)
            {
                await _listenTask;
                _listenTask = null;
            }

            _logger.LogInformation("✓ 服務器已停止");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止服務器時發生錯誤");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await StopAsync();

        _cts?.Dispose();
        _listener?.Stop();

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LtsModbusTcpServer));
    }
}

