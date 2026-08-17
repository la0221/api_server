using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.ImageBatch;

public interface IImageLoaderPort
{
    Task<ImageData> LoadAsync(string path, CancellationToken cancellationToken);
}
