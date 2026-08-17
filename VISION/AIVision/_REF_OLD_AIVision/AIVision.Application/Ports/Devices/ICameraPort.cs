using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.Devices;

public interface ICameraPort : IAsyncDisposable
{
    event EventHandler<ImageData>? FrameReceived;

    bool IsOpen { get; }

    Task OpenAsync(string deviceId, CancellationToken cancellationToken);

    Task StartPreviewAsync(CancellationToken cancellationToken);

    Task StopPreviewAsync(CancellationToken cancellationToken);

    Task<ImageData> CaptureOnceAsync(CancellationToken cancellationToken);
}
