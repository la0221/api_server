namespace AIVision.Application.Ports.ImageBatch;

public interface IFolderPickerPort
{
    Task<string?> PickFolderAsync(CancellationToken cancellationToken);
}
