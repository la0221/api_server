namespace AIVision.Domain.Plc;

/// <summary>
/// PLC 地址顯示模式
/// </summary>
public enum PlcAddressBaseMode
{
    /// <summary>
    /// 0-based: Coil 從 0 開始顯示 (00000, 00001, 00002...)
    /// </summary>
    ZeroBased,

    /// <summary>
    /// 1-based: Coil 從 1 開始顯示 (00001, 00002, 00003...)
    /// 符合 Modbus 標準文件習慣
    /// </summary>
    OneBased
}
