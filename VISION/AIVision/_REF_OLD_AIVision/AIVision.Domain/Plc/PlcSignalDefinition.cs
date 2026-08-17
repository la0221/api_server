namespace AIVision.Domain.Plc;

/// <summary>
/// PLC 訊號定義
/// </summary>
public class PlcSignalDefinition
{
    /// <summary>訊號名稱（邏輯名稱，如 "TriggerCapture"）</summary>
    public string Name { get; set; } = "";

    /// <summary>訊號方向</summary>
    public PlcSignalDirection Direction { get; set; }

    /// <summary>Modbus 區域類型</summary>
    public PlcSignalArea Area { get; set; } = PlcSignalArea.Coil;

    /// <summary>
    /// Modbus 協議位址 (protocolOffset)
    /// 這是實際發送到 Modbus 的地址，永遠是 0-based
    /// 例如：在 Coil 區域的第 0 個位置 = Address:0
    /// </summary>
    public ushort Address { get; set; }

    /// <summary>有效位準</summary>
    public PlcActiveLevel ActiveLevel { get; set; } = PlcActiveLevel.High;

    /// <summary>觸發模式（僅用於輸入訊號）</summary>
    public PlcEdgeMode EdgeMode { get; set; } = PlcEdgeMode.Level;

    /// <summary>訊號描述</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// 取得顯示用的位址字串 (根據指定的 BaseMode 計算)
    /// </summary>
    /// <param name="baseMode">顯示模式 (ZeroBased 或 OneBased)</param>
    /// <returns>格式化的 UI 地址字串 (5位數)</returns>
    public string GetDisplayAddress(PlcAddressBaseMode baseMode)
    {
        var uiAddress = ModbusAddressConverter.GetUiAddress(Area, Address, baseMode);
        return ModbusAddressConverter.FormatUiAddress(uiAddress);
    }

    /// <summary>
    /// 取得顯示用的位址字串 (預設使用 OneBased 模式以保持向後相容)
    /// </summary>
    [Obsolete("請使用 GetDisplayAddress(baseMode) 以明確指定顯示模式")]
    public string DisplayAddress => GetDisplayAddress(PlcAddressBaseMode.OneBased);
}
