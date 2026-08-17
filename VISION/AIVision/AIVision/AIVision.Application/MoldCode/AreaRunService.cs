using System;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.Persistence;
using AIVision.Domain.MoldCode;
using AIVision.Domain.Plc;
using MediatR;
using Microsoft.Extensions.Logging;
using InspectionEntity = AIVision.Domain.Entities.Inspection;

namespace AIVision.Application.MoldCode;

/// <summary>
/// 模號核對「運轉服務」(取代舊 AutoRunService 的角色,面掃站專用)。
/// <para/>
/// 每次觸發執行一個 <see cref="VerifyMoldCodeCycleCommand"/>(取像→多幀投票→三態→混料氣吹,
/// 由 <c>VerifyMoldCodeCycleCommandHandler</c> 自走),並以 <see cref="CycleCompleted"/> 事件把結果交給 UI。
/// <para/>
/// 觸發來源:
/// <list type="bullet">
///   <item>離線 / 手動:呼叫 <see cref="RunOnceAsync"/>(FakePlc + 連拍相機即可端到端驗證)。</item>
///   <item>實機:<see cref="StartAsync"/> 訂閱 <see cref="IPlcHandshakePort.CaptureRequested"/>(暫行;
///         待三菱 MELSEC PLC 協定確定後,與 cycle handler 的 <c>IPlcPort</c> 寫入/握手回報收斂)。</item>
/// </list>
/// 依賴 <see cref="IRequestHandler{TRequest,TResponse}"/> 而非 <c>ISender</c>,讓離線測試免 DI 容器即可直接構造。
/// </summary>
public sealed class AreaRunService : IDisposable
{
    private readonly IRequestHandler<VerifyMoldCodeCycleCommand, MoldCodeCycleResult> _cycle;
    private readonly IPlcHandshakePort? _handshake;
    private readonly IInspectionRepository? _repository;
    private readonly ILogger<AreaRunService>? _logger;

    private bool _subscribed;

    public AreaRunService(
        IRequestHandler<VerifyMoldCodeCycleCommand, MoldCodeCycleResult> cycle,
        IPlcHandshakePort? handshake = null,
        ILogger<AreaRunService>? logger = null,
        IInspectionRepository? repository = null)
    {
        _cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        _handshake = handshake;
        _logger = logger;
        _repository = repository;
    }

    /// <summary>操作員預期模號(通常由當前工單帶入,例 "M101/07")。</summary>
    public string? ExpectedCode { get; set; }

    /// <summary>當前工單 Id(由 ShellViewModel 載入工單時帶入;為 null 則不持久化檢測記錄)。</summary>
    public Guid? WorkOrderId { get; set; }

    /// <summary>運轉中(已開始接受觸發)。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>一個核對週期完成(Skip/Match/TrustInput/MixedAlarm)。</summary>
    public event EventHandler<MoldCodeCycleResult>? CycleCompleted;

    /// <summary>
    /// 執行一次模號核對週期(離線/手動,或單張觸發用)。
    /// </summary>
    /// <param name="expectedCode">操作員預期模號(必填)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<MoldCodeCycleResult> RunOnceAsync(string expectedCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedCode))
            throw new ArgumentException("expectedCode 不可為空", nameof(expectedCode));

        var result = await _cycle.Handle(new VerifyMoldCodeCycleCommand(expectedCode), cancellationToken);

        _logger?.LogInformation(
            "模號週期: {Outcome} 讀={Read} conf={Conf:F2} 幀={Frames} 票={Votes} 氣吹={Blow} {Ms}ms — {Reason}",
            result.Outcome, result.ReadCode, result.Confidence, result.Frames, result.WinnerVotes,
            result.AirBlown, result.ElapsedMs, result.Reason);

        // 持久化檢測記錄(三態 Outcome 語意)。持久化失敗不可中斷核對週期 → try/catch + log。
        if (_repository != null && WorkOrderId is Guid woid)
        {
            try
            {
                var inspection = new InspectionEntity(
                    workOrderId: woid,
                    result: result.Outcome.ToString(),
                    confidence: (float)result.Confidence,
                    imagePath: null,
                    modelVersion: "moldcode",
                    expectedCode: expectedCode,
                    readCode: result.ReadCode,
                    outcome: result.Outcome.ToString(),
                    airBlown: result.AirBlown,
                    occurredAtUtc: DateTime.UtcNow);

                await _repository.AddAsync(inspection, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "模號檢測記錄持久化失敗(工單 {WorkOrderId})，週期照常完成", woid);
            }
        }

        CycleCompleted?.Invoke(this, result);
        return result;
    }

    /// <summary>
    /// 開始運轉。實機:訂閱 PLC 握手觸發;離線(無握手注入):僅標記運轉,改由 <see cref="RunOnceAsync"/> 手動驅動。
    /// </summary>
    public Task StartAsync(string expectedCode, CancellationToken cancellationToken = default)
    {
        ExpectedCode = expectedCode;
        IsRunning = true;

        if (_handshake != null && !_subscribed)
        {
            _handshake.CaptureRequested += OnCaptureRequested;
            _subscribed = true;
            _logger?.LogInformation("AreaRunService 已訂閱 PLC 握手觸發(預期碼={Expected})", expectedCode);
        }
        else
        {
            _logger?.LogInformation("AreaRunService 啟動(離線/手動模式,預期碼={Expected})", expectedCode);
        }

        return Task.CompletedTask;
    }

    /// <summary>停止運轉(取消訂閱觸發)。</summary>
    public Task StopAsync()
    {
        IsRunning = false;
        Unsubscribe();
        return Task.CompletedTask;
    }

    // 實機觸發橋接(暫行):PLC 要求取像 → 跑一次週期 → 回報握手結果。
    // 注意:cycle handler 已自寫 IPlcPort(CaptureStart/AirBlow/Result),待三菱協定確定後與此握手回報收斂,
    // 避免雙重寫入(目前離線不訂閱此路徑,故無衝突)。
    private async void OnCaptureRequested(object? sender, PlcCaptureRequestedEventArgs e)
    {
        var expected = ExpectedCode;
        if (string.IsNullOrWhiteSpace(expected))
        {
            _logger?.LogWarning("收到取像觸發但無預期模號,略過此次週期");
            return;
        }

        try
        {
            var result = await RunOnceAsync(expected!, CancellationToken.None);

            if (_handshake != null)
            {
                await _handshake.NotifyCaptureCompleteAsync(CancellationToken.None);
                var ok = result.Outcome is MarkingVerifyOutcome.Match or MarkingVerifyOutcome.TrustInput;
                await _handshake.ReportResultAsync(
                    ok ? PlcInspectionResult.Ok : PlcInspectionResult.Ng, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AreaRunService 觸發週期失敗");
        }
    }

    private void Unsubscribe()
    {
        if (_handshake != null && _subscribed)
        {
            _handshake.CaptureRequested -= OnCaptureRequested;
            _subscribed = false;
        }
    }

    public void Dispose() => Unsubscribe();
}
