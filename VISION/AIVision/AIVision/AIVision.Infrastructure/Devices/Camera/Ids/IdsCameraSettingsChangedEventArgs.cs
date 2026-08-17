using System;
using AIVision.Application.Ports.Devices;

namespace AIVision.Infrastructure.Devices.Camera.Ids;

internal sealed class IdsCameraSettingsChangedEventArgs : EventArgs
{
    public IdsCameraSettingsChangedEventArgs(IdsCameraSettings settings, CameraParameterKind kind)
    {
        Settings = settings;
        Kind = kind;
    }

    public IdsCameraSettings Settings { get; }

    public CameraParameterKind Kind { get; }
}
