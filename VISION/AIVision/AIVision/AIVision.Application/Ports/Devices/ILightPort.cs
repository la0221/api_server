using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AIVision.Application.Ports.Devices;

public interface ILightPort
{
    Task SetIntensityAsync(int channel, int value, CancellationToken cancellationToken);

    Task TurnAsync(int channel, bool on, CancellationToken cancellationToken);

    Task<LightState> GetStateAsync(CancellationToken cancellationToken);

    Task<LightDeviceInfo> ReadDeviceInfoAsync(CancellationToken cancellationToken);

    Task<LightNetworkProfile> ReadNetworkProfileAsync(CancellationToken cancellationToken);

    Task WriteNetworkProfileAsync(LightNetworkProfile profile, CancellationToken cancellationToken);

    Task SetModeAsync(LightWorkMode mode, CancellationToken cancellationToken);

    Task SetTriggerPolarityAsync(LightTriggerPolarity polarity, CancellationToken cancellationToken);

    Task<bool> SetHeartbeatAsync(bool enabled, CancellationToken cancellationToken);

    Task BackupParametersAsync(CancellationToken cancellationToken);

    // === Auto Run 亮度模式控制 ===

    /// <summary>
    /// 設定為工作亮度（15%）- 用於取像時
    /// </summary>
    Task SetWorkingBrightnessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 設定為待機亮度（0%）- 用於待機時
    /// </summary>
    Task SetIdleBrightnessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 當前是否為工作亮度模式
    /// </summary>
    bool IsWorkingBrightness { get; }
}

public readonly record struct LightState(bool IsConnected, IReadOnlyDictionary<int, int> ChannelValue);

public readonly record struct LightDeviceInfo(
    string FirmwareVersion,
    string SerialNumber,
    int ChannelCount,
    bool IsOnline,
    LightWorkMode WorkMode,
    LightTriggerPolarity TriggerPolarity,
    bool HeartbeatEnabled);

public readonly record struct LightNetworkProfile(string DeviceIp, string GatewayIp, int DevicePort);

public enum LightWorkMode
{
    Constant = 0,
    Strobe = 1,
    External = 2,
    Internal = 3,
    Software = 4
}

public enum LightTriggerPolarity
{
    RisingEdge = 1,
    FallingEdge = 2,
    LevelHigh = 3,
    LevelLow = 4,
    PulseHigh = 5,
    PulseLow = 6
}
