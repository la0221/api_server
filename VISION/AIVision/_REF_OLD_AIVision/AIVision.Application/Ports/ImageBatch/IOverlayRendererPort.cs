using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.ImageBatch;

public interface IOverlayRendererPort
{
    Task<ImageData> DrawAsync(ImageData source, IReadOnlyList<Detection> detections, CancellationToken cancellationToken);
}
