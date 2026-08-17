using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Configuration;
using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Options;
using MvCamCtrl.NET;
using CameraParams = MvCamCtrl.NET.CameraParams;

namespace AIVision.Infrastructure.Devices.Camera.Hik;

public sealed class HikCameraAdapter : ICameraPort
{
    private readonly HikCameraOptions _options;
    private readonly object _sync = new();
    private CCamera? _camera;
    private string? _currentDeviceId;
    private bool _isPreviewing;
    private cbOutputExdelegate? _callback;
    private TaskCompletionSource<ImageData>? _captureSource;

    public HikCameraAdapter(IOptions<HikCameraOptions> options)
    {
        _options = options.Value;
    }

    public event EventHandler<ImageData>? FrameReceived;

    public bool IsOpen => _camera?.IsDeviceConnected() == true;

    public Task OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_camera is not null && string.Equals(_currentDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        CloseCamera();

        var deviceInfo = FindDevice(deviceId, cancellationToken)
            ?? throw new InvalidOperationException($"找不到對應的 HIK 相機：{deviceId}");

        var camera = new CCamera();
        var infoRef = deviceInfo;
        var status = camera.CreateHandle(ref infoRef);
        ThrowIfFailed(status, "CreateHandle");

        try
        {
            status = camera.OpenDevice();
            ThrowIfFailed(status, "OpenDevice");

            ConfigureCamera(camera, deviceInfo);
            RegisterCallback(camera);

            _camera = camera;
            _currentDeviceId = deviceId;
        }
        catch
        {
            camera.CloseDevice();
            camera.DestroyHandle();
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StartPreviewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var camera = EnsureCamera();
        if (_isPreviewing)
        {
            return Task.CompletedTask;
        }

        var status = camera.StartGrabbing();
        ThrowIfFailed(status, "StartGrabbing");
        _isPreviewing = true;
        return Task.CompletedTask;
    }

    public Task StopPreviewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_isPreviewing || _camera is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            var status = _camera.StopGrabbing();
            if (status != 0)
            {
                // SDK 會在未開啟時回傳失敗，可忽略。
            }
        }
        finally
        {
            _isPreviewing = false;
        }

        return Task.CompletedTask;
    }

    public async Task<ImageData> CaptureOnceAsync(CancellationToken cancellationToken)
    {
        var camera = EnsureCamera();
        if (!_isPreviewing)
        {
            await StartPreviewAsync(cancellationToken).ConfigureAwait(false);
        }

        TaskCompletionSource<ImageData> tcs;
        lock (_sync)
        {
            _captureSource?.TrySetCanceled();
            tcs = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);
            _captureSource = tcs;
        }

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopPreviewAsync(CancellationToken.None).ConfigureAwait(false);
        CloseCamera();
    }

    private void OnImageCallback(IntPtr pData, ref CameraParams.MV_FRAME_OUT_INFO_EX frame, IntPtr _)
    {
        var width = frame.nWidth;
        var height = frame.nHeight;
        var frameLength = frame.nFrameLen;
        if (frameLength == 0 || width == 0 || height == 0)
        {
            return;
        }

        var length = checked((int)frameLength);
        var buffer = new byte[length];
        Marshal.Copy(pData, buffer, 0, length);

        var image = new ImageData(buffer, width, height, "Bgr24");
        FrameReceived?.Invoke(this, image);

        TaskCompletionSource<ImageData>? source;
        lock (_sync)
        {
            source = _captureSource;
            _captureSource = null;
        }

        source?.TrySetResult(image);
    }

    private void RegisterCallback(CCamera camera)
    {
        _callback = new cbOutputExdelegate(OnImageCallback);
        var status = camera.RegisterImageCallBackForBGR(_callback, IntPtr.Zero);
        ThrowIfFailed(status, "RegisterImageCallBackForBGR");
    }

    private void ConfigureCamera(CCamera camera, CCameraInfo info)
    {
        if (info is CGigECameraInfo && _options.GigE.PacketSize > 0)
        {
            TrySetInt(camera, "GevSCPSPacketSize", _options.GigE.PacketSize);
            TrySetInt(camera, "GevSCDAWSBChunkPacketSize", _options.GigE.PacketSize);
        }

        if (info is CGigECameraInfo && _options.GigE.StreamChannel > 0)
        {
            TrySetInt(camera, "GevStreamChannelSelector", _options.GigE.StreamChannel);
        }

        if (!string.IsNullOrWhiteSpace(_options.Options.TriggerMode))
        {
            TrySetEnum(camera, "TriggerMode", _options.Options.TriggerMode);
        }

        if (_options.Options.ExposureTimeUs > 0)
        {
            TrySetFloat(camera, "ExposureTime", _options.Options.ExposureTimeUs);
        }

        if (_options.Options.Gain > 0)
        {
            TrySetFloat(camera, "Gain", _options.Options.Gain);
        }

        if (!string.IsNullOrWhiteSpace(_options.Options.PixelFormat))
        {
            TrySetEnum(camera, "PixelFormat", _options.Options.PixelFormat);
        }
    }

    private static void TrySetEnum(CCamera camera, string node, string value)
    {
        try
        {
            camera.SetEnumValueByString(node, value);
        }
        catch
        {
            // Ignore unsupported nodes
        }
    }

    private static void TrySetFloat(CCamera camera, string node, double value)
    {
        try
        {
            camera.SetFloatValue(node, (float)value);
        }
        catch
        {
            // Ignore unsupported nodes
        }
    }

    private static void TrySetInt(CCamera camera, string node, int value)
    {
        try
        {
            camera.SetIntValue(node, value);
        }
        catch
        {
            // Ignore unsupported nodes
        }
    }

    private CCamera EnsureCamera()
    {
        if (_camera is null)
        {
            throw new InvalidOperationException("尚未開啟任何 HIK 相機。");
        }

        return _camera;
    }

    private CCameraInfo? FindDevice(string deviceId, CancellationToken cancellationToken)
    {
        var candidates = CSystem.EnumDevices((uint)(CSystem.MV_GIGE_DEVICE | CSystem.MV_USB_DEVICE));
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _options.Preferred)
        {
            if (!string.IsNullOrWhiteSpace(p))
            {
                var value = p.Trim();
                preferred.Add(value);
                if (!value.StartsWith("SN:", StringComparison.OrdinalIgnoreCase))
                {
                    preferred.Add($"SN:{value}");
                }
            }
        }

        var normalizedId = deviceId?.Trim() ?? string.Empty;

        foreach (var device in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = GetDeviceId(device);
            if (string.Equals(id, normalizedId, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        // 若找不到指定序號，改走 Preferred 列表。
        foreach (var device in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = GetDeviceId(device);
            if (preferred.Contains(id))
            {
                return device;
            }
        }

        return null;
    }

    private static string GetDeviceId(CCameraInfo info) =>
        info switch
        {
            CGigECameraInfo gige => $"SN:{GetSerial(gige.chSerialNumber, gige.nMacAddrHigh, gige.nMacAddrLow)}",
            CUSBCameraInfo usb => $"SN:{GetSerial(usb.chSerialNumber, usb.nMacAddrHigh, usb.nMacAddrLow)}",
            _ => $"SN:Unknown-{info.nMacAddrHigh:X8}{info.nMacAddrLow:X8}"
        };

    private static string GetSerial(string? serial, uint macHigh, uint macLow)
    {
        if (!string.IsNullOrWhiteSpace(serial))
        {
            return serial.Trim();
        }

        return $"MAC:{macHigh:X8}{macLow:X8}";
    }

    private void CloseCamera()
    {
        if (_camera is null)
        {
            return;
        }

        try
        {
            if (_isPreviewing)
            {
                _camera.StopGrabbing();
            }
        }
        catch
        {
            // 忽略停止預覽失敗
        }

        try
        {
            _camera.CloseDevice();
        }
        catch
        {
            // 忽略關閉失敗
        }

        try
        {
            _camera.DestroyHandle();
        }
        catch
        {
            // 忽略釋放失敗
        }

        _camera = null;
        _currentDeviceId = null;
        _isPreviewing = false;
        _captureSource?.TrySetCanceled();
        _captureSource = null;
        _callback = null;
    }

    private static void ThrowIfFailed(int status, string operation)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"{operation} 失敗，錯誤碼：0x{status:X8}");
        }
    }
}
