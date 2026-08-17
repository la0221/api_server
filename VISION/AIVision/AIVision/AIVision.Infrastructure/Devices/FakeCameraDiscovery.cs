using AIVision.Application.Ports.Devices;
using AIVision.Application.ViewModels.Camera;

namespace AIVision.Infrastructure.Devices;

public sealed class FakeCameraDiscovery : ICameraDiscoveryPort
{
    public Task<IReadOnlyList<CameraDeviceVm>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CameraDeviceVm> devices = new[]
        {
            new CameraDeviceVm { Id = "fake-0", Name = "Fake Camera" }
        };

        return Task.FromResult(devices);
    }
}
