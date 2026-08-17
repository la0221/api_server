using System.Collections.Generic;
using System.Net;
using AIVision.Application.Ports.Devices;
using AIVision.Application.ViewModels.Camera;
using MvCamCtrl.NET;

namespace AIVision.Infrastructure.Devices.Camera.Hik;

public sealed class HikDiscoveryAdapter : ICameraDiscoveryPort
{
    public Task<IReadOnlyList<CameraDeviceVm>> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cameras = new List<CameraDeviceVm>();
            var types = CSystem.MV_GIGE_DEVICE | CSystem.MV_USB_DEVICE;
            var devices = CSystem.EnumDevices((uint)types) ?? new List<CCameraInfo>();

            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (device is CGigECameraInfo gige)
                {
                    var serial = GetSerial(gige.chSerialNumber, gige.nMacAddrHigh, gige.nMacAddrLow);
                    var model = SafeString(gige.chModelName);
                    var ip = FormatIp(gige.nCurrentIp);
                    var name = string.IsNullOrWhiteSpace(model) ? $"GigE {serial}" : $"{model} {serial}";
                    cameras.Add(new CameraDeviceVm
                    {
                        Id = $"SN:{serial}",
                        Name = string.IsNullOrWhiteSpace(ip) ? name : $"{name} ({ip})"
                    });
                }
                else if (device is CUSBCameraInfo usb)
                {
                    var serial = GetSerial(usb.chSerialNumber, usb.nMacAddrHigh, usb.nMacAddrLow);
                    var model = SafeString(usb.chModelName);
                    var name = string.IsNullOrWhiteSpace(model) ? $"USB {serial}" : $"{model} {serial}";
                    cameras.Add(new CameraDeviceVm
                    {
                        Id = $"SN:{serial}",
                        Name = $"{name} (USB)"
                    });
                }
            }

            return Task.FromResult<IReadOnlyList<CameraDeviceVm>>(cameras.AsReadOnly());
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<CameraDeviceVm>>(Array.Empty<CameraDeviceVm>());
        }
    }

    private static string SafeString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

    private static string GetSerial(string? serial, uint macHigh, uint macLow)
    {
        if (!string.IsNullOrWhiteSpace(serial))
        {
            return serial.Trim();
        }

        return $"MAC:{macHigh:X8}{macLow:X8}";
    }

    private static string FormatIp(uint ip)
    {
        if (ip == 0)
        {
            return string.Empty;
        }

        var bytes = BitConverter.GetBytes(ip);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return new IPAddress(bytes).ToString();
    }
}
