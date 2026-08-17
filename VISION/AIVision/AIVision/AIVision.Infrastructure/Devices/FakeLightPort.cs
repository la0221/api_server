using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;

namespace AIVision.Infrastructure.Devices;

public sealed class FakeLightPort : ILightPort
{
    private readonly Dictionary<int, int> _channels = new();
    private LightWorkMode _mode = LightWorkMode.Constant;
    private LightTriggerPolarity _polarity = LightTriggerPolarity.RisingEdge;
    private LightNetworkProfile _network = new("192.168.1.95", "192.168.1.1", 8000);
    private bool _heartbeatEnabled;

    public Task SetIntensityAsync(int channel, int value, CancellationToken cancellationToken)
    {
        _channels[channel] = value;
        return Task.CompletedTask;
    }

    public Task TurnAsync(int channel, bool on, CancellationToken cancellationToken)
    {
        _channels[channel] = on ? 100 : 0;
        return Task.CompletedTask;
    }

    public Task<LightState> GetStateAsync(CancellationToken cancellationToken)
    {
        var snapshot = new Dictionary<int, int>(_channels);
        LightState state = new(true, snapshot);
        return Task.FromResult(state);
    }

    public Task<LightDeviceInfo> ReadDeviceInfoAsync(CancellationToken cancellationToken)
    {
        var info = new LightDeviceInfo(
            "v1.0.0",
            "FAKE-LTS",
            8,
            true,
            _mode,
            _polarity,
            _heartbeatEnabled);

        return Task.FromResult(info);
    }

    public Task<LightNetworkProfile> ReadNetworkProfileAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(_network);
    }

    public Task WriteNetworkProfileAsync(LightNetworkProfile profile, CancellationToken cancellationToken)
    {
        _network = profile;
        return Task.CompletedTask;
    }

    public Task SetModeAsync(LightWorkMode mode, CancellationToken cancellationToken)
    {
        _mode = mode;
        return Task.CompletedTask;
    }

    public Task SetTriggerPolarityAsync(LightTriggerPolarity polarity, CancellationToken cancellationToken)
    {
        _polarity = polarity;
        return Task.CompletedTask;
    }

    public Task<bool> SetHeartbeatAsync(bool enabled, CancellationToken cancellationToken)
    {
        _heartbeatEnabled = enabled;
        return Task.FromResult(_heartbeatEnabled);
    }

    public Task BackupParametersAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    // Auto Run 亮度控制 (Fake 實作)
    public Task SetWorkingBrightnessAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task SetIdleBrightnessAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public bool IsWorkingBrightness => false;
}
