namespace AIVision.Application.Ports.ImageBatch;

public interface IImageEnumeratorPort
{
    IAsyncEnumerable<string> EnumerateAsync(string rootPath, CancellationToken cancellationToken);
}
