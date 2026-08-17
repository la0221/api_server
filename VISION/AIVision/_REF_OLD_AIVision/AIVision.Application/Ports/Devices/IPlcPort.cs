using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.Devices;

public interface IPlcPort
{
    Task<IoSnapshot> ReadAsync(CancellationToken cancellationToken);

    Task WriteAsync(IoCommand command, CancellationToken cancellationToken);
}
