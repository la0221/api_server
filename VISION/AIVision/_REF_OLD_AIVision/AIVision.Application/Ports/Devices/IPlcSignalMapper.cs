using AIVision.Domain.Plc;

namespace AIVision.Application.Ports.Devices;

/// <summary>
/// PLC 訊號映射介面 - 邏輯訊號與 Modbus 位址轉換
/// </summary>
public interface IPlcSignalMapper
{
    /// <summary>
    /// 讀取指定訊號的值
    /// </summary>
    /// <param name="signalName">訊號名稱</param>
    Task<bool> ReadSignalAsync(string signalName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 寫入指定訊號的值
    /// </summary>
    /// <param name="signalName">訊號名稱</param>
    /// <param name="value">值</param>
    Task WriteSignalAsync(string signalName, bool value, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量寫入多個連續的 Coil 訊號（合併為單一 Modbus 請求，減少網路往返）
    /// </summary>
    /// <param name="signalValues">訊號名稱與值的字典</param>
    Task WriteMultipleSignalsAsync(Dictionary<string, bool> signalValues, CancellationToken cancellationToken = default);

    /// <summary>
    /// 讀取所有訊號的值
    /// </summary>
    /// <returns>訊號名稱與值的字典</returns>
    Task<Dictionary<string, bool>> ReadAllSignalsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 讀取所有 PC→PLC 輸出訊號
    /// </summary>
    Task<Dictionary<string, bool>> ReadOutputSignalsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 讀取所有 PLC→PC 輸入訊號
    /// </summary>
    Task<Dictionary<string, bool>> ReadInputSignalsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 取得訊號定義
    /// </summary>
    /// <param name="signalName">訊號名稱</param>
    PlcSignalDefinition? GetSignalDefinition(string signalName);

    /// <summary>
    /// 取得所有訊號定義
    /// </summary>
    IReadOnlyList<PlcSignalDefinition> GetAllSignalDefinitions();
}
