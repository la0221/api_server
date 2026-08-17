using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.ImageBatch;

public interface IImageWriterPort
{
    Task SaveAsync(string outputPath, ImageData image, CancellationToken cancellationToken);
}
