using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;

namespace AIVision.Infrastructure.Devices;

public sealed class FakeCameraPort : ICameraPort
{
    private static readonly byte[] Sample = new byte[640 * 480];

    public event EventHandler<ImageData>? FrameReceived;

    public bool IsOpen { get; private set; }

    public Task OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        IsOpen = true;
        return Task.CompletedTask;
    }

    public Task StartPreviewAsync(CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("Camera not open.");
        }

        var buffer = (byte[])Sample.Clone();
        var data = new ImageData(buffer, 640, 480, "Mono8");
        FrameReceived?.Invoke(this, data);
        return Task.CompletedTask;
    }

    public Task StopPreviewAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ImageData> CaptureOnceAsync(CancellationToken cancellationToken)
    {
        var buffer = (byte[])Sample.Clone();
        return Task.FromResult(new ImageData(buffer, 640, 480, "Mono8"));
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        return ValueTask.CompletedTask;
    }
}
