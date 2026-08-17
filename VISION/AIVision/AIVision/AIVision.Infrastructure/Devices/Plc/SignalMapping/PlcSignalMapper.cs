using AIVision.Application.Ports.Devices;
using AIVision.Domain.Plc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.Devices.Plc.SignalMapping;

/// <summary>
/// PLC 訊號映射實現
/// </summary>
public class PlcSignalMapper : IPlcSignalMapper
{
    private readonly IPlcCommunicationPort _communicationPort;
    private readonly PlcSignalMapOptions _options;
    private readonly ILogger<PlcSignalMapper> _logger;
    private readonly Dictionary<string, PlcSignalDefinition> _signalMap;

    public PlcSignalMapper(
        IPlcCommunicationPort communicationPort,
        IOptions<PlcSignalMapOptions> options,
        ILogger<PlcSignalMapper> logger)
    {
        _communicationPort = communicationPort;
        _options = options.Value;
        _logger = logger;

        // 如果沒有設定訊號，使用預設值
        if (_options.Signals.Count == 0)
        {
            _options = PlcSignalMapOptions.CreateDefault();
            _logger.LogInformation("使用預設訊號映射設定");
        }

        // 建立訊號名稱到定義的映射
        _signalMap = _options.Signals.ToDictionary(s => s.Name, s => s);
        _logger.LogInformation("載入 {Count} 個訊號定義", _signalMap.Count);
    }

    public async Task<bool> ReadSignalAsync(string signalName, CancellationToken cancellationToken = default)
    {
        var signal = GetSignalDefinitionOrThrow(signalName);

        _logger.LogTrace("讀取訊號 {Name}: 配置地址={ConfigAddr}, Area={Area}, BaseMode={BaseMode}",
            signalName, signal.Address, signal.Area, _options.AddressBaseMode);

        var value = await ReadSignalValueAsync(signal, cancellationToken);

        _logger.LogTrace("讀取訊號 {Name} ({Address}) = {Value}",
            signalName, signal.DisplayAddress, value);

        return value;
    }

    public async Task WriteSignalAsync(string signalName, bool value, CancellationToken cancellationToken = default)
    {
        var signal = GetSignalDefinitionOrThrow(signalName);

        if (signal.Direction == PlcSignalDirection.PlcToPc)
        {
            throw new InvalidOperationException($"訊號 '{signalName}' 是 PLC→PC 方向 (唯讀)，無法寫入");
        }

        await WriteSignalValueAsync(signal, value, cancellationToken);

        _logger.LogDebug("寫入訊號 {Name} ({Address}) = {Value}",
            signalName, signal.DisplayAddress, value);
    }

    public async Task WriteMultipleSignalsAsync(Dictionary<string, bool> signalValues, CancellationToken cancellationToken = default)
    {
        if (signalValues.Count == 0)
        {
            return;
        }

        // 取得所有訊號定義並驗證
        var signals = new List<(PlcSignalDefinition Signal, bool Value)>();
        foreach (var kv in signalValues)
        {
            var signal = GetSignalDefinitionOrThrow(kv.Key);
            if (signal.Direction == PlcSignalDirection.PlcToPc)
            {
                throw new InvalidOperationException($"訊號 '{kv.Key}' 是 PLC→PC 方向 (唯讀)，無法寫入");
            }
            if (signal.Area != PlcSignalArea.Coil)
            {
                throw new InvalidOperationException($"訊號 '{kv.Key}' 不是 Coil 類型，無法批量寫入");
            }
            signals.Add((signal, kv.Value));
        }

        // 按地址排序
        signals.Sort((a, b) => a.Signal.Address.CompareTo(b.Signal.Address));

        // 檢查地址是否連續
        var minAddr = signals[0].Signal.Address;
        var maxAddr = signals[^1].Signal.Address;
        var expectedCount = maxAddr - minAddr + 1;

        if (expectedCount == signals.Count)
        {
            // 地址連續，使用批量寫入（單一 Modbus 請求）
            var values = new bool[expectedCount];
            foreach (var (signal, value) in signals)
            {
                var idx = signal.Address - minAddr;
                // 根據 ActiveLevel 轉換
                values[idx] = signal.ActiveLevel == PlcActiveLevel.High ? value : !value;
            }

            await _communicationPort.WriteMultipleCoilsAsync(minAddr, values, cancellationToken);
            _logger.LogDebug("批量寫入 {Count} 個訊號 (地址 {Start}~{End})",
                signals.Count, minAddr, maxAddr);
        }
        else
        {
            // 地址不連續，退回到逐一寫入（多個 Modbus 請求）
            _logger.LogDebug("訊號地址不連續，使用逐一寫入 (共 {Count} 個請求)", signals.Count);
            foreach (var (signal, value) in signals)
            {
                await WriteSignalValueAsync(signal, value, cancellationToken);
            }
        }
    }

    public async Task<Dictionary<string, bool>> ReadAllSignalsAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, bool>();

        foreach (var signal in _options.Signals)
        {
            try
            {
                var value = await ReadSignalValueAsync(signal, cancellationToken);
                result[signal.Name] = value;
            }
            catch (OperationCanceledException)
            {
                // ✅ 忽略取消操作（服務正在停止），這是正常行為
                _logger.LogDebug("讀取訊號 {Name} 被取消（服務可能正在停止）", signal.Name);
                result[signal.Name] = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "讀取訊號 {Name} 失敗", signal.Name);
                result[signal.Name] = false;
            }
        }

        return result;
    }

    public async Task<Dictionary<string, bool>> ReadOutputSignalsAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, bool>();
        var outputSignals = _options.Signals.Where(s => s.Direction == PlcSignalDirection.PcToPlc);

        foreach (var signal in outputSignals)
        {
            try
            {
                var value = await ReadSignalValueAsync(signal, cancellationToken);
                result[signal.Name] = value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "讀取輸出訊號 {Name} 失敗", signal.Name);
                result[signal.Name] = false;
            }
        }

        return result;
    }

    public async Task<Dictionary<string, bool>> ReadInputSignalsAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, bool>();
        var inputSignals = _options.Signals.Where(s => s.Direction == PlcSignalDirection.PlcToPc);

        foreach (var signal in inputSignals)
        {
            try
            {
                var value = await ReadSignalValueAsync(signal, cancellationToken);
                result[signal.Name] = value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "讀取輸入訊號 {Name} 失敗", signal.Name);
                result[signal.Name] = false;
            }
        }

        return result;
    }

    public PlcSignalDefinition? GetSignalDefinition(string signalName)
    {
        return _signalMap.GetValueOrDefault(signalName);
    }

    public IReadOnlyList<PlcSignalDefinition> GetAllSignalDefinitions()
    {
        return _options.Signals.AsReadOnly();
    }

    #region Private Methods

    private PlcSignalDefinition GetSignalDefinitionOrThrow(string signalName)
    {
        if (!_signalMap.TryGetValue(signalName, out var signal))
        {
            throw new ArgumentException($"找不到訊號定義: '{signalName}'", nameof(signalName));
        }
        return signal;
    }

    private async Task<bool> ReadSignalValueAsync(PlcSignalDefinition signal, CancellationToken cancellationToken)
    {
        bool rawValue;

        switch (signal.Area)
        {
            case PlcSignalArea.Coil:
                var coils = await _communicationPort.ReadCoilsAsync(signal.Address, 1, cancellationToken);
                rawValue = coils[0];
                break;

            case PlcSignalArea.DiscreteInput:
                var inputs = await _communicationPort.ReadDiscreteInputsAsync(signal.Address, 1, cancellationToken);
                rawValue = inputs[0];
                break;

            case PlcSignalArea.HoldingRegister:
                var registers = await _communicationPort.ReadHoldingRegistersAsync(signal.Address, 1, cancellationToken);
                rawValue = registers[0] != 0;
                break;

            default:
                throw new NotSupportedException($"不支援的訊號區域類型: {signal.Area}");
        }

        // 根據 ActiveLevel 轉換
        return signal.ActiveLevel == PlcActiveLevel.High ? rawValue : !rawValue;
    }

    private async Task WriteSignalValueAsync(PlcSignalDefinition signal, bool value, CancellationToken cancellationToken)
    {
        // 根據 ActiveLevel 轉換
        var rawValue = signal.ActiveLevel == PlcActiveLevel.High ? value : !value;

        switch (signal.Area)
        {
            case PlcSignalArea.Coil:
                await _communicationPort.WriteSingleCoilAsync(signal.Address, rawValue, cancellationToken);
                break;

            case PlcSignalArea.HoldingRegister:
                await _communicationPort.WriteSingleRegisterAsync(signal.Address, (ushort)(rawValue ? 1 : 0), cancellationToken);
                break;

            default:
                throw new NotSupportedException($"不支援寫入的訊號區域類型: {signal.Area}");
        }
    }

    #endregion
}
