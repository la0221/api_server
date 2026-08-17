using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;

namespace AIVision.MoldCode.Harness;

/// <summary>離線用相機替身:把某料位的連拍 jpg 當一次取像序列回放(模擬多幀)。</summary>
internal sealed class FolderBurstCamera : ICameraPort
{
    private readonly string[] _frames;
    private int _index;

#pragma warning disable CS0067 // 離線替身不發 FrameReceived
    public event EventHandler<ImageData>? FrameReceived;
#pragma warning restore CS0067

    public FolderBurstCamera(IEnumerable<string> framePaths) => _frames = framePaths.ToArray();

    public bool IsOpen { get; private set; }

    public Task OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        IsOpen = true;
        return Task.CompletedTask;
    }

    public Task StartPreviewAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopPreviewAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ImageData> CaptureOnceAsync(CancellationToken cancellationToken)
    {
        var path = _frames[_index % _frames.Length];
        _index++;
        return Task.FromResult(ImageLoad.Load(path));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
