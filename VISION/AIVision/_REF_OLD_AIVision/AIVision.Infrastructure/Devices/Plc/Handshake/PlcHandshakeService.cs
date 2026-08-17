using AIVision.Application.Ports.Devices;
using AIVision.Domain.Plc;
using AIVision.Infrastructure.Devices.Plc.Communication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.Devices.Plc.Handshake;

/// <summary>
/// PLC 交握服務實現
/// </summary>
public class PlcHandshakeService : IPlcHandshakePort, IAsyncDisposable
{
    private readonly IPlcCommunicationPort _communicationPort;
    private readonly IPlcSignalMapper _signalMapper;
    private readonly PlcHandshakeOptions _options;
    private readonly PlcConnectionOptions _connectionOptions;
    private readonly ILogger<PlcHandshakeService> _logger;

    // 狀態鎖 - 保護所有狀態相關的字段
    private readonly object _stateLock = new();

    // 使用 volatile 確保多線程可見性
    private volatile PlcHandshakeState _currentState = PlcHandshakeState.Disconnected;
    private volatile bool _isRunning;
    private volatile bool _disposed;

    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    // 使用 Interlocked 進行原子操作的字段
    private int _lastTriggerValue; // 0 = false, 1 = true
    private int _triggerCount;

    // 這些字段由 _stateLock 保護
    private DateTime _lastTriggerTime = DateTime.MinValue;
    private DateTime _stateEntryTime = DateTime.Now;
    private DateTime _errorStateEntryTime = DateTime.MinValue;
    private DateTime _waitForResetEntryTime = DateTime.MinValue;
    private const int ErrorRecoveryDelaySeconds = 10; // 錯誤狀態自動恢復延遲
    private const int WaitForResetTimeoutSeconds = 5; // 等待 PLC 重置超時（秒）

    private TaskCompletionSource<PlcInspectionResult>? _resultTcs;
    private TaskCompletionSource<bool>? _captureTcs;
    private volatile bool _isCaptureInProgress;

    public PlcHandshakeState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }
    public bool IsRunning => _isRunning;

    public event EventHandler<PlcHandshakeStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlcTriggerReceivedEventArgs>? TriggerReceived;
    public event EventHandler<PlcCaptureRequestedEventArgs>? CaptureRequested;

    public PlcHandshakeService(
        IPlcCommunicationPort communicationPort,
        IPlcSignalMapper signalMapper,
        IOptions<PlcHandshakeOptions> options,
        IOptions<PlcConnectionOptions> connectionOptions,
        ILogger<PlcHandshakeService> logger)
    {
        _communicationPort = communicationPort;
        _signalMapper = signalMapper;
        _options = options.Value;
        _connectionOptions = connectionOptions.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isRunning)
        {
            _logger.LogWarning("交握服務已在運行中");
            return;
        }

        _logger.LogInformation("啟動 PLC 交握服務");

        // 確保連線
        if (!_communicationPort.IsConnected)
        {
            await _communicationPort.ConnectAsync(
                _connectionOptions.Ip,
                _connectionOptions.Port,
                cancellationToken);
        }

        // 初始化訊號
        await InitializeSignalsAsync(cancellationToken);

        // 啟動輪詢
        _cts = new CancellationTokenSource();
        _isRunning = true;
        SetState(PlcHandshakeState.WaitForTrigger);

        _pollingTask = Task.Run(() => PollingLoopAsync(_cts.Token), _cts.Token);

        _logger.LogInformation("PLC 交握服務已啟動，輪詢任務已啟動");
    }

    public async Task StopAsync()
    {
        if (!_isRunning)
        {
            return;
        }

        // 記錄調用堆疊，幫助追蹤是誰呼叫了 StopAsync
        var stackTrace = Environment.StackTrace;
        _logger.LogInformation("停止 PLC 交握服務 (當前狀態: {State}, 取像中: {InProgress})\n調用堆疊:\n{StackTrace}",
            _currentState, _isCaptureInProgress, stackTrace);

        _cts?.Cancel();

        if (_pollingTask != null)
        {
            try
            {
                // 使用超時等待，避免無限等待
                await _pollingTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("輪詢任務未能在 5 秒內完成");
            }
        }

        _isRunning = false;
        SetState(PlcHandshakeState.Disconnected);

        // 釋放 CancellationTokenSource
        _cts?.Dispose();
        _cts = null;

        _logger.LogInformation("PLC 交握服務已停止");
    }

    public async Task ReportResultAsync(PlcInspectionResult result, CancellationToken cancellationToken = default)
    {
        // 等待狀態轉換到 RunningInspect（最多等待 5 秒）
        // 這是為了處理 AutoRunService 在 SkipInference 模式下推論太快，
        // 導致 PlcHandshakeService 還在處理 CaptureStatus=OFF 的情況
        const int maxWaitMs = 5000;
        const int pollIntervalMs = 50;
        var waited = 0;

        while (_currentState == PlcHandshakeState.StartCapture && waited < maxWaitMs)
        {
            _logger.LogDebug("等待狀態轉換到 RunningInspect... (已等待 {Ms}ms)", waited);
            await Task.Delay(pollIntervalMs, cancellationToken);
            waited += pollIntervalMs;
        }

        if (_currentState != PlcHandshakeState.RunningInspect)
        {
            _logger.LogWarning("目前狀態 {State} 不允許回報結果 (等待 {Ms}ms 後)", _currentState, waited);
            return;
        }

        _logger.LogInformation("收到檢測結果: {Result}", result);
        _resultTcs?.TrySetResult(result);
    }

    public Task NotifyCaptureCompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_currentState != PlcHandshakeState.StartCapture)
        {
            _logger.LogWarning("目前狀態 {State} 不允許通知取像完成", _currentState);
            return Task.CompletedTask;
        }

        _logger.LogInformation("收到取像完成通知");
        _captureTcs?.TrySetResult(true);
        return Task.CompletedTask;
    }

    public async Task ResetAsync()
    {
        _logger.LogInformation("重置交握狀態機");

        try
        {
            // 清除所有輸出訊號 - 使用批量寫入
            await ClearOutputSignalsAsync(CancellationToken.None);

            // 重新讀取當前觸發訊號值，確保 Rising Edge 偵測正確
            var currentTrigger = await _signalMapper.ReadSignalAsync(_options.TriggerSignal);
            Interlocked.Exchange(ref _lastTriggerValue, currentTrigger ? 1 : 0);
            _logger.LogDebug("重置後觸發訊號值: {Value}", currentTrigger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "重置訊號失敗");
        }

        _resultTcs?.TrySetCanceled();
        _resultTcs = null;
        _captureTcs?.TrySetCanceled();
        _captureTcs = null;
        _isCaptureInProgress = false;

        if (_isRunning)
        {
            SetState(PlcHandshakeState.WaitForTrigger);
        }
    }

    #region Private Methods

    private async Task InitializeSignalsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("初始化訊號狀態");

        // 注意: 10001 由 PLC 控制，PC 不寫入

        // 清除所有輸出訊號 (00001~00004) - 使用批量寫入
        try
        {
            _logger.LogDebug("正在批量清除輸出訊號 (00001~00004)...");
            await ClearOutputSignalsAsync(cancellationToken);
            _logger.LogDebug("已清除輸出訊號: 00001~00004=0");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "初始化訊號時發生錯誤，繼續執行");
        }

        // 強制設定 _lastTriggerValue = false，這樣如果 10001 已經是 1，會立即觸發
        // 因為 10001 的狀態由 PLC 控制，PC 無法決定
        Interlocked.Exchange(ref _lastTriggerValue, 0);
        _logger.LogDebug("初始化 _lastTriggerValue = false，若 10001=1 將立即觸發");
    }

    private async Task PollingLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("輪詢迴圈已進入，間隔 {Interval}ms", _connectionOptions.PollIntervalMs);
        int consecutiveErrors = 0;
        const int maxConsecutiveErrors = 5;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 檢查連線狀態，如果斷開則嘗試重連
                if (!_communicationPort.IsConnected)
                {
                    // 使用配置的重連等待時間
                    var reconnectDelay = _connectionOptions.InitialReconnectDelayMs;
                    _logger.LogWarning("PLC 連線已斷開，等待 {Delay}ms 後嘗試重新連線...", reconnectDelay);

                    // 等待一段時間，讓 PLC 端的 TCP 連線完全關閉
                    // 這是解決 TIME_WAIT 問題的關鍵
                    await Task.Delay(reconnectDelay, cancellationToken);

                    try
                    {
                        // 使用設定檔中的 IP 和 Port 重連
                        await _communicationPort.ConnectAsync(
                            _connectionOptions.Ip,
                            _connectionOptions.Port,
                            cancellationToken);
                        _logger.LogInformation("PLC 重新連線成功");
                        consecutiveErrors = 0;
                    }
                    catch (Exception connEx)
                    {
                        _logger.LogWarning(connEx, "PLC 重新連線失敗，等待後重試");
                        // 使用配置的重連間隔
                        await Task.Delay(_connectionOptions.ReconnectIntervalMs / 5, cancellationToken);
                        continue;
                    }
                }

                await ProcessStateAsync(cancellationToken);
                await Task.Delay(_connectionOptions.PollIntervalMs, cancellationToken);
                consecutiveErrors = 0; // 成功執行，重置錯誤計數
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                _logger.LogWarning(ex, "輪詢處理錯誤 (連續錯誤: {Count}/{Max})",
                    consecutiveErrors, maxConsecutiveErrors);

                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    _logger.LogError("連續錯誤次數過多，進入錯誤狀態");
                    SetState(PlcHandshakeState.Error);
                    consecutiveErrors = 0;
                }

                // 等待一段時間後重試（使用配置的輪詢間隔的 10 倍作為錯誤恢復延遲）
                var errorRetryDelay = Math.Min(_connectionOptions.PollIntervalMs * 10, 5000);
                await Task.Delay(errorRetryDelay, cancellationToken);
            }
        }
    }

    private async Task ProcessStateAsync(CancellationToken cancellationToken)
    {
        switch (_currentState)
        {
            case PlcHandshakeState.WaitForTrigger:
                await ProcessWaitForTriggerAsync(cancellationToken);
                break;

            case PlcHandshakeState.StartCapture:
                await ProcessStartCaptureAsync(cancellationToken);
                break;

            case PlcHandshakeState.RunningInspect:
                await ProcessRunningInspectAsync(cancellationToken);
                break;

            case PlcHandshakeState.SendResult:
                // SendResult 在 ProcessRunningInspectAsync 中處理
                break;

            case PlcHandshakeState.WaitForPlcReset:
                await ProcessWaitForPlcResetAsync(cancellationToken);
                break;

            case PlcHandshakeState.Error:
                // 錯誤狀態：等待一段時間後自動恢復
                await ProcessErrorStateAsync(cancellationToken);
                break;
        }
    }

    private DateTime _lastDebugLogTime = DateTime.MinValue;

    private async Task ProcessWaitForTriggerAsync(CancellationToken cancellationToken)
    {
        // 讀取觸發訊號
        var currentTrigger = await _signalMapper.ReadSignalAsync(_options.TriggerSignal, cancellationToken);
        var currentTriggerInt = currentTrigger ? 1 : 0;

        // 使用 Interlocked 讀取上一次的觸發值
        var lastTriggerInt = Interlocked.CompareExchange(ref _lastTriggerValue, 0, 0);
        var lastTrigger = lastTriggerInt != 0;

        // 每 0.5 秒輸出一次 Debug 日誌
        var now = DateTime.Now;
        if ((now - _lastDebugLogTime).TotalMilliseconds >= 500)
        {
            var currentTriggerCount = Interlocked.CompareExchange(ref _triggerCount, 0, 0);
            _logger.LogDebug("[輪詢] 10001={Current}, last={Last}, state={State}, triggerCount={Count}",
                currentTrigger, lastTrigger, _currentState, currentTriggerCount);
            _lastDebugLogTime = now;
        }

        // Rising Edge Detection
        if (currentTrigger && !lastTrigger)
        {
            DateTime lastTriggerTime;
            lock (_stateLock)
            {
                lastTriggerTime = _lastTriggerTime;
            }

            var elapsed = (now - lastTriggerTime).TotalMilliseconds;

            if (elapsed >= _options.MinTriggerIntervalMs)
            {
                _logger.LogInformation("偵測到觸發訊號 (Rising Edge), 10001=1");

                // 使用鎖保護時間更新
                lock (_stateLock)
                {
                    _lastTriggerTime = now;
                }

                // 使用 Interlocked 增加觸發計數
                var newTriggerCount = Interlocked.Increment(ref _triggerCount);

                TriggerReceived?.Invoke(this, new PlcTriggerReceivedEventArgs(newTriggerCount));

                // 【方案2優化】立即進入取像狀態，不等待清除訊號
                // 清除訊號會在 ProcessStartCaptureAsync 中處理（失敗也不影響取像）
                SetState(PlcHandshakeState.StartCapture);

                // 觸發成功後不更新 _lastTriggerValue，避免影響下一輪偵測
                // _lastTriggerValue 會在 SendResultAsync 結束時重置為 false
                return;
            }
            else
            {
                _logger.LogDebug("觸發間隔過短，忽略 ({Elapsed}ms < {Min}ms)", elapsed, _options.MinTriggerIntervalMs);
            }
        }

        // 使用 Interlocked 更新觸發值
        Interlocked.Exchange(ref _lastTriggerValue, currentTriggerInt);
    }

    private async Task ProcessStartCaptureAsync(CancellationToken cancellationToken)
    {
        // 防止重複進入
        if (_isCaptureInProgress)
        {
            return;
        }
        _isCaptureInProgress = true;

        try
        {
            _logger.LogDebug("開始取像流程");

            // 檢查是否有訂閱者監聽取像請求事件
            // 如果沒有訂閱者（例如 AutoRunService 已停止），則跳過取像流程但仍需回應 PLC
            if (CaptureRequested == null)
            {
                _logger.LogWarning("沒有訂閱者監聯 CaptureRequested 事件，跳過取像流程但仍回應 PLC");

                // 依據 ErrorPolicy 決定如何回應 PLC
                // 這裡視為系統未就緒，依策略送出 NG 或直接回到等待
                switch (_options.ErrorPolicy)
                {
                    case "TreatAsNg":
                        _logger.LogInformation("依 ErrorPolicy 送出 NG 結果給 PLC（無訂閱者）");
                        await SendResultAsync(PlcInspectionResult.Ng, cancellationToken);
                        break;

                    case "Ignore":
                    default:
                        // 即使忽略，也需要清除信號讓 PLC 知道這次觸發已處理
                        _logger.LogInformation("依 ErrorPolicy 清除信號並回到等待觸發（無訂閱者）");
                        await _signalMapper.WriteSignalAsync(_options.DoneSignal, true, cancellationToken);
                        await Task.Delay(100, cancellationToken); // 短暫維持信號讓 PLC 能讀取
                        await _signalMapper.WriteSignalAsync(_options.DoneSignal, false, cancellationToken);
                        Interlocked.Exchange(ref _lastTriggerValue, 0);
                        SetState(PlcHandshakeState.WaitForTrigger);
                        break;
                }
                return;
            }

            // ======================================================================
            // 【優化流程】取像優先，00001 訊號控制相機開關
            // 原因：收到 10001 時馬達已經開始轉動，需要立即開始取像
            // 00002/00003/00004 清除放到 SendResultAsync 結束後，避免超時延遲取像
            // ======================================================================

            // Step 1: 設定 00001=1 開始取像，並立即通知外部
            _captureTcs = new TaskCompletionSource<bool>();

            // 00001=1（開始取像）
            var startSuccess = await WriteSignalWithRetryAsync(_options.CaptureStatusSignal, true, cancellationToken);
            if (startSuccess)
            {
                _logger.LogInformation("00001=1 開始取像（馬達已啟動）");
            }
            else
            {
                _logger.LogWarning("00001=1 失敗，但仍繼續取像流程");
            }

            // 通知外部取像
            CaptureRequested?.Invoke(this, new PlcCaptureRequestedEventArgs(_triggerCount));

            // 等待外部呼叫 NotifyCaptureCompleteAsync 或超時
            var captureTimeout = TimeSpan.FromMilliseconds(_options.CaptureTimeoutMs);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(captureTimeout);

            try
            {
                await _captureTcs.Task.WaitAsync(cts.Token);
                _logger.LogDebug("取像完成通知已收到");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("等待取像完成超時");
                // 超時仍繼續，讓推論階段處理
            }

            // 檢查是否被取消
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("取像完成後發現已被取消，跳過後續處理");
                return;
            }

            // ======================================================================
            // Step 2: 取像完成後，設定 00001=0 停止相機
            // PLC 偵測到 00001 上升沿 (0→1) 已經發生，現在設回 0
            // 00002=0 已在 Step 0 清除，這裡只需停止相機
            // ======================================================================
            _logger.LogDebug("取像完成，設定 00001=0 停止相機...");

            var stopSuccess = await WriteSignalWithRetryAsync(_options.CaptureStatusSignal, false, cancellationToken);
            if (stopSuccess)
            {
                _logger.LogInformation("00001=0 成功，相機已停止");
            }
            else
            {
                _logger.LogError("00001=0 失敗，相機可能未正確停止");
            }

            // 準備接收結果
            _logger.LogDebug("準備建立 _resultTcs 並進入 RunningInspect 狀態");
            _resultTcs = new TaskCompletionSource<PlcInspectionResult>();
            SetState(PlcHandshakeState.RunningInspect);
        }
        finally
        {
            _isCaptureInProgress = false;
        }
    }

    private async Task ProcessRunningInspectAsync(CancellationToken cancellationToken)
    {
        // 檢查超時
        var elapsed = (DateTime.Now - _stateEntryTime).TotalMilliseconds;
        if (elapsed > _options.InferenceTimeoutMs)
        {
            _logger.LogWarning("推論超時 ({Elapsed}ms > {Timeout}ms)", elapsed, _options.InferenceTimeoutMs);

            switch (_options.ErrorPolicy)
            {
                case "TreatAsNg":
                    _logger.LogInformation("依 ErrorPolicy 送出 NG 結果");
                    await SendResultAsync(PlcInspectionResult.Ng, cancellationToken);
                    break;

                case "Ignore":
                    _logger.LogInformation("依 ErrorPolicy 忽略超時，回到等待觸發");
                    SetState(PlcHandshakeState.WaitForTrigger);
                    break;

                case "BlockAndAlarm":
                default:
                    _logger.LogError("依 ErrorPolicy 進入錯誤狀態");
                    SetState(PlcHandshakeState.Error);
                    break;
            }
            return;
        }

        // 檢查是否收到結果
        if (_resultTcs != null && _resultTcs.Task.IsCompleted)
        {
            var result = await _resultTcs.Task;
            await SendResultAsync(result, cancellationToken);
        }
    }

    private async Task SendResultAsync(PlcInspectionResult result, CancellationToken cancellationToken)
    {
        _logger.LogInformation("送出結果: {Result}", result);

        SetState(PlcHandshakeState.SendResult);

        // Step 4: 先送結果訊號 (00003 OK 或 00004 NG) - 使用重試機制
        bool resultSuccess;
        if (result == PlcInspectionResult.Ok)
        {
            resultSuccess = await WriteSignalWithRetryAsync(_options.OkSignal, true, cancellationToken);
            if (resultSuccess) _logger.LogDebug("結果: 00003=1 (OK)");
        }
        else
        {
            resultSuccess = await WriteSignalWithRetryAsync(_options.NgSignal, true, cancellationToken);
            if (resultSuccess) _logger.LogDebug("結果: 00004=1 (NG)");
        }

        if (!resultSuccess)
        {
            _logger.LogError("送出結果訊號失敗，但仍繼續流程");
        }

        // 再送檢測完成 (00002 = 1) - 使用重試機制
        var doneSuccess = await WriteSignalWithRetryAsync(_options.DoneSignal, true, cancellationToken);
        if (doneSuccess)
        {
            _logger.LogDebug("檢測完成: 00002=1");
        }
        else
        {
            _logger.LogError("送出檢測完成訊號失敗，但仍繼續流程");
        }

        // Step 5: 清除訊號 (00002=0, 00003=0, 00004=0)
        // 00001 已在停止相機時清除
        // 00002/00003/00004 放在這裡清除，避免超時延遲下一輪取像
        try
        {
            await _signalMapper.WriteSignalAsync(_options.DoneSignal, false, cancellationToken);
            await _signalMapper.WriteSignalAsync(_options.OkSignal, false, cancellationToken);
            await _signalMapper.WriteSignalAsync(_options.NgSignal, false, cancellationToken);
            _logger.LogDebug("已清除訊號: 00002=0, 00003=0, 00004=0");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清除訊號失敗，下一輪可能受影響");
        }

        // Step 6: 流程完成，重設狀態並回到等待觸發
        // 重設 lastTriggerValue=0，這樣：
        //   - 如果 10001 還是 1 → 下次輪詢會偵測到 Rising Edge，開始新一輪
        //   - 如果 10001 已經是 0 → 等待 PLC 下次觸發
        // 這樣確保流程完整走完後才允許下一次觸發
        _resultTcs = null;

        Interlocked.Exchange(ref _lastTriggerValue, 0);
        _logger.LogDebug("SendResult 結束，流程完成，重設 lastTriggerValue=0，回到等待觸發");
        SetState(PlcHandshakeState.WaitForTrigger);
    }

    private async Task ProcessWaitForPlcResetAsync(CancellationToken cancellationToken)
    {
        // 讀取觸發訊號
        var currentTrigger = await _signalMapper.ReadSignalAsync(_options.TriggerSignal, cancellationToken);

        // 等待 PLC 清除觸發訊號
        if (!currentTrigger)
        {
            _logger.LogDebug("PLC 已重置觸發訊號 10001=0");

            if (_options.AutoClearByPc)
            {
                _logger.LogDebug("自動清除結果訊號");
                await _signalMapper.WriteSignalAsync(_options.DoneSignal, false, cancellationToken);
                await _signalMapper.WriteSignalAsync(_options.OkSignal, false, cancellationToken);
                await _signalMapper.WriteSignalAsync(_options.NgSignal, false, cancellationToken);
            }

            // 10001 已歸零，現在可以正確偵測下一次 Rising Edge
            Interlocked.Exchange(ref _lastTriggerValue, 0);
            _waitForResetEntryTime = DateTime.MinValue;
            _resultTcs = null;
            SetState(PlcHandshakeState.WaitForTrigger);
        }
        else
        {
            // 檢查是否超時
            var elapsed = (DateTime.Now - _waitForResetEntryTime).TotalSeconds;
            if (elapsed >= WaitForResetTimeoutSeconds)
            {
                _logger.LogWarning("等待 PLC 重置觸發訊號超時 ({Seconds}秒)，強制回到等待狀態（10001 仍為 1）",
                    WaitForResetTimeoutSeconds);

                // 超時後強制回到 WaitForTrigger，但保持 lastTriggerValue=1
                // 這樣只有當 10001 真的從 0→1 時才會觸發下一輪
                Interlocked.Exchange(ref _lastTriggerValue, 1);
                _waitForResetEntryTime = DateTime.MinValue;
                _resultTcs = null;
                SetState(PlcHandshakeState.WaitForTrigger);
            }
        }
    }

    /// <summary>
    /// 處理錯誤狀態：等待一段時間後自動嘗試恢復
    /// </summary>
    private async Task ProcessErrorStateAsync(CancellationToken cancellationToken)
    {
        // 記錄進入錯誤狀態的時間
        if (_errorStateEntryTime == DateTime.MinValue)
        {
            _errorStateEntryTime = DateTime.Now;
            _logger.LogWarning("進入錯誤狀態，將在 {Seconds} 秒後自動嘗試恢復", ErrorRecoveryDelaySeconds);
        }

        var elapsed = (DateTime.Now - _errorStateEntryTime).TotalSeconds;

        if (elapsed >= ErrorRecoveryDelaySeconds)
        {
            _logger.LogInformation("錯誤狀態等待時間已過，嘗試自動恢復...");

            try
            {
                // 嘗試重置連線
                if (!_communicationPort.IsConnected)
                {
                    _logger.LogInformation("嘗試重新連線 PLC...");
                    await _communicationPort.ConnectAsync(
                        _connectionOptions.Ip,
                        _connectionOptions.Port,
                        cancellationToken);
                    _logger.LogInformation("PLC 重新連線成功");
                }

                // 重置訊號
                await ResetAsync();

                // 重置錯誤狀態計時器
                _errorStateEntryTime = DateTime.MinValue;

                _logger.LogInformation("錯誤狀態自動恢復成功，回到等待觸發狀態");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "錯誤狀態自動恢復失敗，將在 {Seconds} 秒後重試", ErrorRecoveryDelaySeconds);
                // 重置計時器，等待下一次嘗試
                _errorStateEntryTime = DateTime.Now;
            }
        }
    }

    /// <summary>
    /// 快速失敗策略：嘗試送出 NG 結果並恢復到等待狀態
    /// </summary>
    private async Task TryNotifyNgAndRecoverAsync(CancellationToken cancellationToken)
    {
        // 嘗試送出 NG 結果給 PLC（盡力而為，失敗也沒關係）
        try
        {
            _logger.LogInformation("嘗試送出 NG 結果給 PLC（快速失敗策略）");
            await _signalMapper.WriteSignalAsync(_options.NgSignal, true, cancellationToken);
            await _signalMapper.WriteSignalAsync(_options.DoneSignal, true, cancellationToken);
            _logger.LogInformation("已送出 NG + Done 訊號，通知 PLC 此輪失敗");
        }
        catch (Exception notifyEx)
        {
            _logger.LogWarning(notifyEx, "送出 NG 結果也失敗，PLC 可能需要等待超時");
        }

        // 重設狀態，立即回到等待觸發
        Interlocked.Exchange(ref _lastTriggerValue, 0);
        _resultTcs?.TrySetCanceled();
        _resultTcs = null;
        _captureTcs?.TrySetCanceled();
        _captureTcs = null;
        _isCaptureInProgress = false;

        _logger.LogInformation("快速恢復：重設狀態，回到等待觸發（人工可重新觸發）");
        SetState(PlcHandshakeState.WaitForTrigger);
    }

    /// <summary>
    /// 清除所有輸出訊號 (00001~00004) - 使用批量寫入減少網路往返
    /// </summary>
    private async Task ClearOutputSignalsAsync(CancellationToken cancellationToken)
    {
        // 使用批量寫入，將 4 個訊號合併為單一 Modbus 請求
        var signals = new Dictionary<string, bool>
        {
            { _options.CaptureStatusSignal, false },  // 00001=0
            { _options.DoneSignal, false },            // 00002=0
            { _options.OkSignal, false },              // 00003=0
            { _options.NgSignal, false }               // 00004=0
        };

        await _signalMapper.WriteMultipleSignalsAsync(signals, cancellationToken);
    }

    /// <summary>
    /// 帶重試的訊號寫入 (處理網路不穩定情況)
    /// </summary>
    private async Task<bool> WriteSignalWithRetryAsync(string signalName, bool value, CancellationToken cancellationToken, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // 確保連線
                if (!_communicationPort.IsConnected)
                {
                    _logger.LogDebug("寫入前發現斷線，嘗試重連 (attempt {Attempt}/{Max})", attempt, maxRetries);
                    try
                    {
                        // 使用設定檔中的 IP 和 Port 重連
                        await _communicationPort.ConnectAsync(
                            _connectionOptions.Ip,
                            _connectionOptions.Port,
                            cancellationToken);
                        _logger.LogInformation("重連成功");
                    }
                    catch (Exception connEx)
                    {
                        _logger.LogWarning(connEx, "重連失敗 (attempt {Attempt}/{Max})", attempt, maxRetries);
                        if (attempt < maxRetries)
                        {
                            await Task.Delay(500 * attempt, cancellationToken);
                            continue;
                        }
                        return false;
                    }
                }

                await _signalMapper.WriteSignalAsync(signalName, value, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw; // 不重試取消操作
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "寫入訊號 {Signal}={Value} 失敗 (attempt {Attempt}/{Max})",
                    signalName, value, attempt, maxRetries);

                if (attempt < maxRetries)
                {
                    await Task.Delay(200 * attempt, cancellationToken);
                }
            }
        }

        _logger.LogError("寫入訊號 {Signal}={Value} 重試 {Max} 次後仍失敗", signalName, value, maxRetries);
        return false;
    }

    private void SetState(PlcHandshakeState newState)
    {
        PlcHandshakeState oldState;
        lock (_stateLock)
        {
            if (_currentState == newState)
            {
                return;
            }

            oldState = _currentState;
            _currentState = newState;
            _stateEntryTime = DateTime.Now;

            // 記錄進入 WaitForPlcReset 的時間（用於超時處理）
            if (newState == PlcHandshakeState.WaitForPlcReset)
            {
                _waitForResetEntryTime = DateTime.Now;
            }
        }

        _logger.LogInformation("狀態變更: {OldState} → {NewState}", oldState, newState);
        StateChanged?.Invoke(this, new PlcHandshakeStateChangedEventArgs(oldState, newState));
    }

    /// <summary>
    /// 如果已釋放則拋出異常
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PlcHandshakeService));
        }
    }

    #endregion

    #region IAsyncDisposable

    /// <summary>
    /// 異步釋放資源
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.LogInformation("正在釋放 PlcHandshakeService 資源...");

        // 1. 取消輪詢
        _cts?.Cancel();

        // 2. 等待輪詢任務完成
        if (_pollingTask != null)
        {
            try
            {
                await _pollingTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // 預期的取消
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("輪詢任務未能在 5 秒內完成");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "等待輪詢任務時發生異常");
            }
        }

        // 3. 取消等待中的 TaskCompletionSource
        _resultTcs?.TrySetCanceled();
        _captureTcs?.TrySetCanceled();

        // 4. 釋放 CancellationTokenSource
        _cts?.Dispose();
        _cts = null;

        _isRunning = false;

        _logger.LogInformation("PlcHandshakeService 資源已釋放");

        // 抑制終結器
        GC.SuppressFinalize(this);
    }

    #endregion
}
