using System.Drawing;
using System.Drawing.Imaging;
using AForge.Video;
using AForge.Video.DirectShow;
using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;

namespace AIVision.Presentation.Wpf.Adapters.Camera;

public sealed class AForgeCameraPort : ICameraPort
{
    private VideoCaptureDevice? _device;
    private CancellationTokenSource? _lifetimeCts;
    private string? _currentDeviceId;
    private bool _isPreviewing;

    public event EventHandler<ImageData>? FrameReceived;

    public bool IsOpen { get; private set; }

    public Task OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (IsOpen && string.Equals(deviceId, _currentDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        CloseDevice();

        _device = new VideoCaptureDevice(deviceId);
        _device.NewFrame += OnNewFrame;
        _currentDeviceId = deviceId;
        IsOpen = true;
        return Task.CompletedTask;
    }

    public Task StartPreviewAsync(CancellationToken cancellationToken)
    {
        EnsureDevice();
        if (_isPreviewing)
        {
            return Task.CompletedTask;
        }

        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _device!.Start();
        _isPreviewing = true;
        return Task.CompletedTask;
    }

    public Task StopPreviewAsync(CancellationToken cancellationToken)
    {
        if (!_isPreviewing)
        {
            return Task.CompletedTask;
        }

        _lifetimeCts?.Cancel();
        if (_device is { IsRunning: true })
        {
            _device.SignalToStop();
            _device.WaitForStop();
        }

        _isPreviewing = false;
        return Task.CompletedTask;
    }

    public async Task<ImageData> CaptureOnceAsync(CancellationToken cancellationToken)
    {
        EnsureDevice();

        if (!_isPreviewing)
        {
            await StartPreviewAsync(cancellationToken).ConfigureAwait(false);
        }

        var frameReady = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, ImageData data)
        {
            frameReady.TrySetResult(data);
        }

        FrameReceived += Handler;
        using var ctr = cancellationToken.Register(() => frameReady.TrySetCanceled(cancellationToken));

        try
        {
            return await frameReady.Task.ConfigureAwait(false);
        }
        finally
        {
            FrameReceived -= Handler;
        }
    }

    public ValueTask DisposeAsync()
    {
        CloseDevice();
        return ValueTask.CompletedTask;
    }

    private void CloseDevice()
    {
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;

        if (_device is { })
        {
            _device.NewFrame -= OnNewFrame;
            if (_device.IsRunning)
            {
                _device.SignalToStop();
                _device.WaitForStop();
            }

            _device = null;
        }

        IsOpen = false;
        _isPreviewing = false;
        _currentDeviceId = null;
    }

    private void EnsureDevice()
    {
        if (!IsOpen || _device is null)
        {
            throw new InvalidOperationException("Camera has not been opened.");
        }
    }

    private void OnNewFrame(object sender, NewFrameEventArgs eventArgs)
    {
        if (_lifetimeCts is { IsCancellationRequested: true })
        {
            return;
        }

        Bitmap? bitmap = null;

        try
        {
            bitmap = (Bitmap)eventArgs.Frame.Clone();
            var data = ToImageData(bitmap);
            FrameReceived?.Invoke(this, data);
        }
        catch
        {
            // Ignore frame errors
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private static ImageData ToImageData(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            var stride = bmpData.Stride;
            var buffer = new byte[stride * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, buffer, 0, buffer.Length);
            return new ImageData(buffer, bitmap.Width, bitmap.Height, "Bgr24");
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }
    }
}
