using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;

namespace AIVision.MoldCode.Harness;

/// <summary>離線用 PLC 替身:記錄寫入的 IO 命令(氣吹/結果),不接真硬體。</summary>
internal sealed class FakePlcPort : IPlcPort
{
    public List<IoCommand> Writes { get; } = new();

    public Task<IoSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new IoSnapshot(InReady: true, OutCapture: false, OutResult: false, RawMask: 0));

    public Task WriteAsync(IoCommand command, CancellationToken cancellationToken)
    {
        Writes.Add(command);
        return Task.CompletedTask;
    }
}
