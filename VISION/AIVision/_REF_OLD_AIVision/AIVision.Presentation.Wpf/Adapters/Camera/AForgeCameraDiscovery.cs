using AForge.Video.DirectShow;
using AIVision.Application.Ports.Devices;
using AIVision.Application.ViewModels.Camera;

namespace AIVision.Presentation.Wpf.Adapters.Camera;

public sealed class AForgeCameraDiscovery : ICameraDiscoveryPort
{
    public Task<IReadOnlyList<CameraDeviceVm>> ListAsync(CancellationToken cancellationToken)
    {
        var collection = new FilterInfoCollection(FilterCategory.VideoInputDevice);
        var devices = collection.Cast<FilterInfo>()
            .Select(info => new CameraDeviceVm
            {
                Id = info.MonikerString,
                Name = info.Name
            })
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<CameraDeviceVm>>(devices);
    }
}
