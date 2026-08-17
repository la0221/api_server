using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Domain.Shared;

namespace AIVision.Presentation.Wpf.Adapters.ImageBatch;

public sealed class NullOverlayRenderer : IOverlayRendererPort
{
    public Task<ImageData> DrawAsync(ImageData source, IReadOnlyList<Detection> detections, CancellationToken cancellationToken) =>
        Task.FromResult(source);
}
