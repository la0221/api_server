using AIVision.Domain.Plc;

namespace AIVision.Infrastructure.Devices.Plc.SignalMapping;

/// <summary>
/// PLC 訊號映射設定
/// </summary>
public class PlcSignalMapOptions
{
    public const string SectionName = "Devices:PlcSignalMap";

    /// <summary>
    /// 地址顯示模式 (預設 OneBased 符合 Modbus 標準文件習慣)
    /// </summary>
    public PlcAddressBaseMode AddressBaseMode { get; set; } = PlcAddressBaseMode.OneBased;

    /// <summary>訊號定義列表</summary>
    public List<PlcSignalDefinition> Signals { get; set; } = new();

    /// <summary>
    /// 取得預設的訊號定義（根據架構設計文件）
    /// </summary>
    public static PlcSignalMapOptions CreateDefault()
    {
        return new PlcSignalMapOptions
        {
            Signals = new List<PlcSignalDefinition>
            {
                new()
                {
                    Name = "CaptureStatus",
                    Direction = PlcSignalDirection.PcToPlc,
                    Area = PlcSignalArea.Coil,
                    Address = 0,
                    ActiveLevel = PlcActiveLevel.High,
                    Description = "取相訊號 (00001): 0=取相完畢, 1=取相中"
                },
                new()
                {
                    Name = "InspectDone",
                    Direction = PlcSignalDirection.PcToPlc,
                    Area = PlcSignalArea.Coil,
                    Address = 1,
                    ActiveLevel = PlcActiveLevel.High,
                    Description = "檢測完成訊號 (00002): 0=檢測中, 1=AI檢測完畢"
                },
                new()
                {
                    Name = "ResultOk",
                    Direction = PlcSignalDirection.PcToPlc,
                    Area = PlcSignalArea.Coil,
                    Address = 2,
                    ActiveLevel = PlcActiveLevel.High,
                    Description = "OK結果 (00003): 1=產品為OK"
                },
                new()
                {
                    Name = "ResultNg",
                    Direction = PlcSignalDirection.PcToPlc,
                    Area = PlcSignalArea.Coil,
                    Address = 3,
                    ActiveLevel = PlcActiveLevel.High,
                    Description = "NG結果 (00004): 1=產品為NG"
                },
                new()
                {
                    Name = "TriggerCapture",
                    Direction = PlcSignalDirection.PlcToPc,
                    Area = PlcSignalArea.Coil,
                    Address = 10000,
                    ActiveLevel = PlcActiveLevel.High,
                    EdgeMode = PlcEdgeMode.Rising,
                    Description = "PLC觸發拍照 (10001): Rising edge 觸發"
                }
            }
        };
    }
}
