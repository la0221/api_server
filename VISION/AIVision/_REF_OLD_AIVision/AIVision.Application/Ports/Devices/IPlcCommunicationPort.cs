namespace AIVision.Application.Ports.Devices;

/// <summary>
/// PLC 底層通訊介面 - 負責 Modbus TCP 讀寫操作
/// </summary>
public interface IPlcCommunicationPort : IAsyncDisposable
{
    /// <summary>連線狀態</summary>
    bool IsConnected { get; }

    /// <summary>連線狀態變更事件</summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>建立連線（使用設定檔的 IP/Port）</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>建立連線（指定 IP/Port）</summary>
    /// <param name="ip">PLC IP 位址</param>
    /// <param name="port">Modbus TCP 埠號</param>
    Task ConnectAsync(string ip, int port, CancellationToken cancellationToken = default);

    /// <summary>斷開連線</summary>
    Task DisconnectAsync();

    /// <summary>讀取 Coil (FC01)</summary>
    /// <param name="startAddress">起始位址 (0-based)</param>
    /// <param name="count">讀取數量</param>
    Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort count, CancellationToken cancellationToken = default);

    /// <summary>讀取 Discrete Input (FC02)</summary>
    /// <param name="startAddress">起始位址 (0-based)</param>
    /// <param name="count">讀取數量</param>
    Task<bool[]> ReadDiscreteInputsAsync(ushort startAddress, ushort count, CancellationToken cancellationToken = default);

    /// <summary>寫入單一 Coil (FC05)</summary>
    /// <param name="address">位址 (0-based)</param>
    /// <param name="value">值</param>
    Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken cancellationToken = default);

    /// <summary>寫入多個 Coil (FC15)</summary>
    /// <param name="startAddress">起始位址 (0-based)</param>
    /// <param name="values">值陣列</param>
    Task WriteMultipleCoilsAsync(ushort startAddress, bool[] values, CancellationToken cancellationToken = default);

    /// <summary>讀取 Holding Register (FC03)</summary>
    /// <param name="startAddress">起始位址 (0-based)</param>
    /// <param name="count">讀取數量</param>
    Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken cancellationToken = default);

    /// <summary>寫入單一 Register (FC06)</summary>
    /// <param name="address">位址 (0-based)</param>
    /// <param name="value">值</param>
    Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default);
}
