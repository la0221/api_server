using AIVision.Application.ViewModels.Camera;

namespace AIVision.Application.Ports.Devices;

public interface ICameraDiscoveryPort
{
    Task<IReadOnlyList<CameraDeviceVm>> ListAsync(CancellationToken cancellationToken);
}
