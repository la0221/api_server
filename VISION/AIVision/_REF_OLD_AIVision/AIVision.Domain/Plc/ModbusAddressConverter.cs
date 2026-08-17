namespace AIVision.Domain.Plc;

/// <summary>
/// Modbus 地址轉換器
/// 支援 Absolute-Address-Based 模式（台灣常見）
/// </summary>
/// <remarks>
/// 核心概念：
/// - Address: 設定檔中的地址，就是實際 PLC 使用的絕對地址
/// - baseMode: 只影響 UI 顯示格式，不影響實際通訊
///
/// Absolute-Address-Based 模式：
/// - 設定 Address=10001，PLC 就是收到 10001
/// - 設定 Address=1，PLC 就是收到 1
/// - UI 顯示直接顯示該地址（可選擇格式）
/// </remarks>
public static class ModbusAddressConverter
{
    /// <summary>
    /// 計算 UI 顯示地址
    /// 在 Absolute-Address-Based 模式下，直接返回地址本身
    /// </summary>
    /// <param name="area">訊號區域（目前未使用，保留供未來擴展）</param>
    /// <param name="address">絕對地址（設定檔中的 Address）</param>
    /// <param name="baseMode">顯示模式（目前未使用，保留供未來擴展）</param>
    /// <returns>UI 顯示地址（等於輸入地址）</returns>
    public static int GetUiAddress(
        PlcSignalArea area,
        ushort address,
        PlcAddressBaseMode baseMode)
    {
        // Absolute-Address-Based: 直接返回地址，不做轉換
        // BaseMode 只是用來記錄使用者偏好的顯示風格，實際地址不變
        return address;
    }

    /// <summary>
    /// 從 UI 地址還原 Modbus 地址
    /// 在 Absolute-Address-Based 模式下，直接返回地址本身
    /// </summary>
    public static ushort GetModbusAddress(
        PlcSignalArea area,
        int uiAddress,
        PlcAddressBaseMode baseMode)
    {
        if (uiAddress < 0 || uiAddress > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(uiAddress),
                $"UI地址 {uiAddress} 超出有效範圍 (0-{ushort.MaxValue})");
        }

        return (ushort)uiAddress;
    }

    /// <summary>
    /// 格式化 UI 地址為 5 位數字串 (前導零)
    /// </summary>
    /// <param name="uiAddress">UI 地址</param>
    /// <returns>格式化字串 (例如: "00001", "10001")</returns>
    public static string FormatUiAddress(int uiAddress) => $"{uiAddress:D5}";
}
