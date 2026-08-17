using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;

namespace AIVision.Infrastructure.Devices;

public sealed class FakePlcPort : IPlcPort
{
    private IoCommand _lastCommand;

    public IoCommand LastCommand => _lastCommand;

    public Task<IoSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = new IoSnapshot(true, _lastCommand.CaptureStart, _lastCommand.ResultOn, 0);
        return Task.FromResult(snapshot);
    }

    public Task WriteAsync(IoCommand command, CancellationToken cancellationToken)
    {
        _lastCommand = command;
        return Task.CompletedTask;
    }
}
