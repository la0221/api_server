using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Devices.Plc;

/// <summary>
/// Modbus TCP PLC 實作 - 實作 IPlcPort 介面
/// 透過 IPlcSignalMapper 讀寫訊號
/// </summary>
public sealed class ModbusPlcPort : IPlcPort
{
    private readonly IPlcSignalMapper _signalMapper;
    private readonly ILogger<ModbusPlcPort> _logger;

    // 訊號名稱常數（對應 appsettings.json 中的定義）
    private const string SignalTriggerCapture = "TriggerCapture";
    private const string SignalCaptureStatus = "CaptureStatus";
    private const string SignalInspectDone = "InspectDone";
    private const string SignalResultOk = "ResultOk";
    private const string SignalResultNg = "ResultNg";

    public ModbusPlcPort(
        IPlcSignalMapper signalMapper,
        ILogger<ModbusPlcPort> logger)
    {
        _signalMapper = signalMapper;
        _logger = logger;
    }

    public async Task<IoSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 讀取 PLC → PC 的觸發訊號
            var triggerCapture = await _signalMapper.ReadSignalAsync(SignalTriggerCapture, cancellationToken);

            // 讀取 PC → PLC 的輸出訊號（回讀確認）
            var captureStatus = await _signalMapper.ReadSignalAsync(SignalCaptureStatus, cancellationToken);
            var resultOk = await _signalMapper.ReadSignalAsync(SignalResultOk, cancellationToken);

            // 組合成 IoSnapshot
            // InReady = TriggerCapture (PLC 觸發拍照)
            // OutCapture = CaptureStatus (PC 取像中)
            // OutResult = ResultOk (PC 結果OK)
            var snapshot = new IoSnapshot(
                InReady: triggerCapture,
                OutCapture: captureStatus,
                OutResult: resultOk,
                RawMask: 0);

            _logger.LogTrace("PLC 讀取: Trigger={Trigger}, Capture={Capture}, ResultOk={Ok}",
                triggerCapture, captureStatus, resultOk);

            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "讀取 PLC 訊號失敗");
            throw;
        }
    }

    public async Task WriteAsync(IoCommand command, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("寫入 PLC: CaptureStart={CaptureStart}, ResultOn={ResultOn}",
                command.CaptureStart, command.ResultOn);

            // 寫入取像狀態
            await _signalMapper.WriteSignalAsync(SignalCaptureStatus, command.CaptureStart, cancellationToken);

            // 寫入檢測完成和結果
            if (!command.CaptureStart)
            {
                // 非取像中 = 檢測完成
                await _signalMapper.WriteSignalAsync(SignalInspectDone, true, cancellationToken);
                await _signalMapper.WriteSignalAsync(SignalResultOk, command.ResultOn, cancellationToken);
                await _signalMapper.WriteSignalAsync(SignalResultNg, !command.ResultOn, cancellationToken);
            }
            else
            {
                // 取像中 = 清除完成和結果訊號
                await _signalMapper.WriteSignalAsync(SignalInspectDone, false, cancellationToken);
                await _signalMapper.WriteSignalAsync(SignalResultOk, false, cancellationToken);
                await _signalMapper.WriteSignalAsync(SignalResultNg, false, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "寫入 PLC 訊號失敗");
            throw;
        }
    }
}
