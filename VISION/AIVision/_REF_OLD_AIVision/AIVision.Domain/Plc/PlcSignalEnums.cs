namespace AIVision.Domain.Plc;

/// <summary>
/// 訊號方向
/// </summary>
public enum PlcSignalDirection
{
    /// <summary>PLC 輸出給 PC 讀取</summary>
    PlcToPc,

    /// <summary>PC 輸出給 PLC 讀取</summary>
    PcToPlc,

    /// <summary>雙向可讀寫 (PC 可讀取也可寫入)</summary>
    Bidirectional
}

/// <summary>
/// Modbus 區域類型
/// </summary>
public enum PlcSignalArea
{
    /// <summary>Coil (FC01/05/15) - 可讀寫的位元</summary>
    Coil,

    /// <summary>Discrete Input (FC02) - 只讀的位元</summary>
    DiscreteInput,

    /// <summary>Holding Register (FC03/06/16) - 可讀寫的暫存器</summary>
    HoldingRegister,

    /// <summary>Input Register (FC04) - 只讀的暫存器</summary>
    InputRegister
}

/// <summary>
/// 訊號觸發模式
/// </summary>
public enum PlcEdgeMode
{
    /// <summary>位準觸發 - 只看當前值</summary>
    Level,

    /// <summary>上升緣觸發 - 0→1 時觸發</summary>
    Rising,

    /// <summary>下降緣觸發 - 1→0 時觸發</summary>
    Falling
}

/// <summary>
/// 有效位準
/// </summary>
public enum PlcActiveLevel
{
    /// <summary>高位準有效 (1 = 有效)</summary>
    High,

    /// <summary>低位準有效 (0 = 有效)</summary>
    Low
}
