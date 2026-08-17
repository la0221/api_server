using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using AIVision.Application.ViewModels.Camera;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using peak;
using peak.core;

namespace AIVision.Infrastructure.Devices.Camera.Ids;

public sealed class IdsCameraDiscoveryAdapter : ICameraDiscoveryPort
{
    private readonly ILogger<IdsCameraDiscoveryAdapter> _logger;
    private readonly IdsCameraOptions _options;
    private readonly IReadOnlyList<string> _preferredOrder;

    public IdsCameraDiscoveryAdapter(
        IOptions<IdsCameraOptions> options,
        ILogger<IdsCameraDiscoveryAdapter> logger)
    {
        _logger = logger;
        _options = options.Value;
        _preferredOrder = _options.Preferred
            .Select((value, index) => (value, index))
            .Where(tuple => !string.IsNullOrWhiteSpace(tuple.value))
            .OrderBy(tuple => tuple.index)
            .Select(tuple => tuple.value.Trim())
            .ToList();
    }

    public Task<IReadOnlyList<CameraDeviceVm>> ListAsync(CancellationToken cancellationToken)
    {
        IdsPeakLibrary.EnsureInitialized(ResolveSdkDirectory(), _logger);

        var devices = new List<CameraDeviceVm>();

        try
        {
            var manager = DeviceManager.Instance();
            manager.Reset(DeviceManager.ResetPolicy.IgnoreOpenDevices);
            manager.Update(DeviceManager.UpdatePolicy.ScanEnvironmentForProducerLibraries);

            foreach (var descriptor in manager.Devices().ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var id = SafeTrim(descriptor.ID());
                    var serial = SafeTrim(descriptor.SerialNumber());
                    var name = ChooseName(descriptor);

                    var vm = new CameraDeviceVm
                    {
                        Id = id ?? serial ?? Guid.NewGuid().ToString("N"),
                        Name = name
                    };

                    devices.Add(vm);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate IDS device {Id}", descriptor.ID());
                }
                finally
                {
                    descriptor.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IDS peak discovery failed.");
        }

        var ordered = devices
            .OrderBy(vm => GetPreferredIndex(vm.Id))
            .ThenBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Append preferred entries that are not present as placeholders
        foreach (var preferred in _preferredOrder)
        {
            if (ordered.Any(vm => string.Equals(vm.Id, preferred, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            ordered.Insert(0, new CameraDeviceVm
            {
                Id = preferred,
                Name = $"Preferred ({preferred})"
            });
        }

        return Task.FromResult<IReadOnlyList<CameraDeviceVm>>(ordered);
    }

    private static string ChooseName(DeviceDescriptor descriptor)
    {
        var userDefined = SafeTrim(descriptor.UserDefinedName());
        if (!string.IsNullOrWhiteSpace(userDefined))
        {
            return userDefined!;
        }

        var display = SafeTrim(descriptor.DisplayName());
        var serial = SafeTrim(descriptor.SerialNumber());

        if (!string.IsNullOrWhiteSpace(display) && !string.IsNullOrWhiteSpace(serial))
        {
            return $"{display} ({serial})";
        }

        return display ?? serial ?? "IDS Camera";
    }

    private int GetPreferredIndex(string deviceId)
    {
        for (var i = 0; i < _preferredOrder.Count; i++)
        {
            if (string.Equals(_preferredOrder[i], deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private string ResolveSdkDirectory()
    {
        var path = _options.SdkPath ?? "libs/cameras/IDS_PEAK";
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string? SafeTrim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
