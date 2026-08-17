using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;

namespace AIVision.Infrastructure.Devices;

/// <summary>
/// 預設的相機控制實作，當前端驅動不支援調參時提供空集合。
/// </summary>
public sealed class NullCameraControlPort : ICameraControlPort
{
    public Task<IReadOnlyList<CameraParameterDescriptor>> GetParametersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CameraParameterDescriptor>>(Array.Empty<CameraParameterDescriptor>());

    public Task SetParameterAsync(CameraParameterKind kind, double value, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
