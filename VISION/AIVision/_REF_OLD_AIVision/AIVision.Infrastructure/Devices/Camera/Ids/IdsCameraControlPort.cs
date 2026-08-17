using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using peak.core;
using peak.core.nodes;

namespace AIVision.Infrastructure.Devices.Camera.Ids;

/// <summary>
/// 透過 JSON 設定檔管理 IDS peak 相機的主要參數。
/// </summary>
public sealed class IdsCameraControlPort : ICameraControlPort
{
    private readonly IHostEnvironment _environment;
    private readonly ILogger<IdsCameraControlPort> _logger;
    private readonly IdsCameraOptions _options;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly object _boundsLock = new();
    private CameraParameterBounds _bounds = CameraParameterBounds.CreateDefault();
    private readonly JsonSerializerOptions _serializer = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal event EventHandler<IdsCameraSettingsChangedEventArgs>? SettingsChanged;

    public IdsCameraControlPort(
        IHostEnvironment environment,
        IOptions<IdsCameraOptions> options,
        ILogger<IdsCameraControlPort> logger)
    {
        _environment = environment;
        _logger = logger;
        _options = options.Value;
    }

    internal void UpdateBounds(NodeMap? nodeMap)
    {
        _logger.LogInformation("──── IdsCameraControlPort.UpdateBounds 開始 ────");
        if (nodeMap is null)
        {
            _logger.LogWarning("  NodeMap 是 null，無法更新邊界");
            return;
        }

        _logger.LogInformation("  NodeMap 有效，開始讀取參數邊界...");
        var bounds = CameraParameterBounds.CreateDefault();

        bounds.Exposure = ReadFloatRange(nodeMap.TryFindNodeFloat("ExposureTime"), bounds.Exposure);
        _logger.LogInformation("  ExposureTime 範圍: {Min}~{Max}", bounds.Exposure.Min, bounds.Exposure.Max);

        bounds.Gain = ReadFloatRange(nodeMap.TryFindNodeFloat("Gain"), bounds.Gain);
        _logger.LogInformation("  Gain 範圍: {Min}~{Max}", bounds.Gain.Min, bounds.Gain.Max);

        bounds.Height = ReadIntRange(nodeMap.TryFindNodeInteger("Height"), bounds.Height);
        _logger.LogInformation("  Height 範圍: {Min}~{Max}", bounds.Height.Min, bounds.Height.Max);

        var lineRateNode = nodeMap.TryFindNodeFloat("AcquisitionLineRate");
        _logger.LogInformation("  AcquisitionLineRate 節點: {Exists}, IsReadable: {Readable}",
            lineRateNode is not null, lineRateNode?.IsReadable() ?? false);

        if (lineRateNode is not null && lineRateNode.IsReadable())
        {
            var range = ReadFloatRange(lineRateNode, bounds.LineRate ?? FloatRange.Disabled);
            bounds.LineRate = range.HasRange ? range : null;
            _logger.LogInformation("  AcquisitionLineRate 範圍: {Min}~{Max} (HasRange={HasRange})",
                range.Min, range.Max, range.HasRange);
        }
        else
        {
            bounds.LineRate = null;
            _logger.LogInformation("  AcquisitionLineRate 節點不可用");
        }

        // 讀取 ROI 邊界 (Line Scan 用)
        var offsetXNode = nodeMap.TryFindNodeInteger("OffsetX");
        if (offsetXNode is not null && offsetXNode.IsReadable())
        {
            bounds.OffsetX = ReadIntRange(offsetXNode, new IntRange(0, 0, 1));
            _logger.LogDebug("ROI OffsetX 範圍: {Min} ~ {Max}", bounds.OffsetX?.Min, bounds.OffsetX?.Max);
        }

        var offsetYNode = nodeMap.TryFindNodeInteger("OffsetY");
        if (offsetYNode is not null && offsetYNode.IsReadable())
        {
            bounds.OffsetY = ReadIntRange(offsetYNode, new IntRange(0, 0, 1));
            _logger.LogDebug("ROI OffsetY 範圍: {Min} ~ {Max}", bounds.OffsetY?.Min, bounds.OffsetY?.Max);
        }

        var widthNode = nodeMap.TryFindNodeInteger("Width");
        if (widthNode is not null && widthNode.IsReadable())
        {
            bounds.Width = ReadIntRange(widthNode, new IntRange(0, 0, 1));
            _logger.LogDebug("ROI Width 範圍: {Min} ~ {Max}", bounds.Width?.Min, bounds.Width?.Max);
        }

        lock (_boundsLock)
        {
            _bounds = bounds;
        }
        _logger.LogInformation("──── IdsCameraControlPort.UpdateBounds 完成 ────");
    }

    public async Task<IReadOnlyList<CameraParameterDescriptor>> GetParametersAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("──── IdsCameraControlPort.GetParametersAsync 開始 ────");
        IdsCameraSettings snapshot;
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("  讀取設定檔...");
            snapshot = await ReadSettingsInternalAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("  設定檔內容: Exposure={Exposure}, Gain={Gain}, Height={Height}, LineRate={LineRate}",
                snapshot.ExposureTimeUs, snapshot.Gain, snapshot.Height, snapshot.AcquisitionLineRate);
        }
        finally
        {
            _sync.Release();
        }

        var bounds = GetBoundsSnapshot();
        _logger.LogInformation("  Bounds 快照:");
        _logger.LogInformation("    Exposure: {Min}~{Max} (HasRange={HasRange})", bounds.Exposure.Min, bounds.Exposure.Max, bounds.Exposure.HasRange);
        _logger.LogInformation("    Gain: {Min}~{Max} (HasRange={HasRange})", bounds.Gain.Min, bounds.Gain.Max, bounds.Gain.HasRange);
        _logger.LogInformation("    Height: {Min}~{Max}", bounds.Height.Min, bounds.Height.Max);
        _logger.LogInformation("    LineRate: {LineRate}", bounds.LineRate?.HasRange == true ? $"{bounds.LineRate?.Min}~{bounds.LineRate?.Max}" : "null/disabled");

        var exposureValue = bounds.Exposure.Clamp(snapshot.ExposureTimeUs);
        var gainValue = bounds.Gain.Clamp(snapshot.Gain);
        var heightValue = bounds.Height.ClampFromDouble(snapshot.Height);

        var descriptors = new List<CameraParameterDescriptor>
        {
            new(
                CameraParameterKind.ExposureTime,
                "曝光時間 (µs)",
                exposureValue,
                Minimum: bounds.Exposure.Min,
                Maximum: bounds.Exposure.Max,
                Step: bounds.Exposure.Step,
                IsIntegral: false),
            new(
                CameraParameterKind.Gain,
                $"增益 ({snapshot.GainSelector})",
                gainValue,
                Minimum: bounds.Gain.Min,
                Maximum: bounds.Gain.Max,
                Step: bounds.Gain.Step,
                IsIntegral: false),
            new(
                CameraParameterKind.Height,
                "影像高度 (px)",
                heightValue,
                Minimum: bounds.Height.Min,
                Maximum: bounds.Height.Max,
                Step: bounds.Height.Step,
                IsIntegral: true),
        };

        if (bounds.LineRate is { } lineRange && lineRange.HasRange)
        {
            var lineValue = snapshot.AcquisitionLineRate ?? lineRange.Min;
            lineValue = lineRange.Clamp(lineValue);
            descriptors.Add(
                new CameraParameterDescriptor(
                    CameraParameterKind.AcquisitionLineRate,
                    "線頻 (Hz)",
                    lineValue,
                    Minimum: lineRange.Min,
                    Maximum: lineRange.Max,
                    Step: lineRange.Step,
                    IsIntegral: false));
            _logger.LogInformation("  已加入 AcquisitionLineRate 參數: {Value} ({Min}~{Max})", lineValue, lineRange.Min, lineRange.Max);
        }
        else
        {
            _logger.LogInformation("  AcquisitionLineRate 不可用 (LineRate bounds is null or disabled)");
        }

        _logger.LogInformation("──── IdsCameraControlPort.GetParametersAsync 完成，返回 {Count} 個參數 ────", descriptors.Count);
        return descriptors;
    }

    internal async Task<IdsCameraSettings> GetCurrentSettingsSnapshotAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadSettingsInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task SetParameterAsync(CameraParameterKind kind, double value, CancellationToken cancellationToken)
    {
        IdsCameraSettings updated;
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            updated = await ReadSettingsInternalAsync(cancellationToken).ConfigureAwait(false);
            var bounds = GetBoundsSnapshot();
            switch (kind)
            {
                case CameraParameterKind.ExposureTime:
                    updated.ExposureTimeUs = bounds.Exposure.Clamp(value);
                    break;
                case CameraParameterKind.Gain:
                    updated.Gain = bounds.Gain.Clamp(value);
                    break;
                case CameraParameterKind.Height:
                    updated.Height = bounds.Height.ClampFromDouble(value);
                    break;
                case CameraParameterKind.AcquisitionLineRate:
                    if (value <= 0)
                    {
                        updated.AcquisitionLineRate = null;
                    }
                    else if (bounds.LineRate is { } lineRange && lineRange.HasRange)
                    {
                        updated.AcquisitionLineRate = lineRange.Clamp(value);
                    }
                    else
                    {
                        updated.AcquisitionLineRate = null;
                    }

                    break;

                // Line Scan ROI 參數
                case CameraParameterKind.OffsetX:
                    if (bounds.OffsetX is { } offsetXRange)
                    {
                        updated.OffsetX = offsetXRange.ClampFromDouble(value);
                    }
                    else
                    {
                        updated.OffsetX = (long)Math.Max(0, value);
                    }
                    break;

                case CameraParameterKind.OffsetY:
                    if (bounds.OffsetY is { } offsetYRange)
                    {
                        updated.OffsetY = offsetYRange.ClampFromDouble(value);
                    }
                    else
                    {
                        updated.OffsetY = (long)Math.Max(0, value);
                    }
                    break;

                case CameraParameterKind.Width:
                    if (bounds.Width is { } widthRange)
                    {
                        updated.Width = widthRange.ClampFromDouble(value);
                    }
                    else
                    {
                        updated.Width = (long)Math.Max(0, value);
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "不支援的參數種類。");
            }

            await WriteSettingsInternalAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }

        await ApplyToHardwareAsync(updated, kind, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveConfigPath()
    {
        var basePath = _environment.ContentRootPath ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(basePath, _options.ConfigPath));
    }

    private async Task<IdsCameraSettings> ReadSettingsInternalAsync(CancellationToken cancellationToken)
    {
        var path = ResolveConfigPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(path))
        {
            var defaults = CreateDefaultSettings();
            await WriteSettingsInternalAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        using var stream = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<IdsCameraSettings>(stream, _serializer, cancellationToken)
            .ConfigureAwait(false) ?? new IdsCameraSettings();

        return MergeWithDefaults(settings);
    }

    private async Task WriteSettingsInternalAsync(IdsCameraSettings settings, CancellationToken cancellationToken)
    {
        var path = ResolveConfigPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, settings, _serializer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private IdsCameraSettings CreateDefaultSettings() =>
        new()
        {
            Preferred = _options.Preferred,
            ExposureTimeUs = _options.ExposureTimeUs,
            GainSelector = _options.GainSelector,
            Gain = _options.Gain,
            Height = _options.Height,
            AcquisitionLineRate = _options.AcquisitionLineRate
        };

    private IdsCameraSettings MergeWithDefaults(IdsCameraSettings settings)
    {
        settings.Preferred ??= _options.Preferred;

        if (settings.ExposureTimeUs <= 0)
        {
            settings.ExposureTimeUs = _options.ExposureTimeUs;
        }

        if (string.IsNullOrWhiteSpace(settings.GainSelector))
        {
            settings.GainSelector = _options.GainSelector;
        }

        if (settings.Gain < 0)
        {
            settings.Gain = _options.Gain;
        }

        if (settings.Height <= 0)
        {
            settings.Height = _options.Height;
        }

        return settings;
    }

    private Task ApplyToHardwareAsync(IdsCameraSettings settings, CameraParameterKind kind, CancellationToken cancellationToken)
    {
        _logger.LogDebug("IDS camera parameter {Kind} updated. Exposure={ExposureTime}, Gain={Gain}, Height={Height}, LineRate={LineRate}",
            kind,
            settings.ExposureTimeUs,
            settings.Gain,
            settings.Height,
            settings.AcquisitionLineRate);

        SettingsChanged?.Invoke(this, new IdsCameraSettingsChangedEventArgs(settings, kind));
        return Task.CompletedTask;
    }

    private static FloatRange ReadFloatRange(FloatNode? node, FloatRange fallback)
    {
        if (node is null || !node.IsReadable())
        {
            return fallback;
        }

        try
        {
            var min = node.Minimum();
            var max = node.Maximum();
            if (double.IsNaN(min) || double.IsNaN(max) || max <= min)
            {
                return fallback;
            }

            double step = 0;
            try
            {
                if (node.HasConstantIncrement())
                {
                    step = node.Increment();
                }
            }
            catch
            {
                step = 0;
            }

            if (step <= 0)
            {
                step = Math.Max((max - min) / 100.0, 0.0001);
            }

            return new FloatRange(min, max, step);
        }
        catch
        {
            return fallback;
        }
    }

    private static IntRange ReadIntRange(IntegerNode? node, IntRange fallback)
    {
        if (node is null || !node.IsReadable())
        {
            return fallback;
        }

        try
        {
            var min = node.Minimum();
            var max = node.Maximum();
            if (max <= min)
            {
                return fallback;
            }

            var step = Math.Max(node.Increment(), 1);
            return new IntRange(min, max, step);
        }
        catch
        {
            return fallback;
        }
    }

    private CameraParameterBounds GetBoundsSnapshot()
    {
        lock (_boundsLock)
        {
            return _bounds.Clone();
        }
    }

    private sealed class CameraParameterBounds
    {
        public FloatRange Exposure { get; set; } = FloatRange.DefaultExposure;
        public FloatRange Gain { get; set; } = FloatRange.DefaultGain;
        public IntRange Height { get; set; } = IntRange.DefaultHeight;
        public FloatRange? LineRate { get; set; }

        // Line Scan ROI 邊界
        public IntRange? OffsetX { get; set; }
        public IntRange? OffsetY { get; set; }
        public IntRange? Width { get; set; }

        public static CameraParameterBounds CreateDefault() =>
            new()
            {
                Exposure = FloatRange.DefaultExposure,
                Gain = FloatRange.DefaultGain,
                Height = IntRange.DefaultHeight,
                LineRate = null,
                OffsetX = null,
                OffsetY = null,
                Width = null
            };

        public CameraParameterBounds Clone() =>
            new()
            {
                Exposure = Exposure,
                Gain = Gain,
                Height = Height,
                LineRate = LineRate,
                OffsetX = OffsetX,
                OffsetY = OffsetY,
                Width = Width
            };
    }

    private readonly struct FloatRange
    {
        public FloatRange(double min, double max, double step)
        {
            Min = min;
            Max = max;
            Step = step;
        }

        public double Min { get; }
        public double Max { get; }
        public double Step { get; }
        public bool HasRange => Max > Min;

        public double Clamp(double value)
        {
            if (!HasRange)
            {
                return value;
            }

            var clamped = Math.Clamp(value, Min, Max);
            if (Step > double.Epsilon)
            {
                var steps = Math.Round((clamped - Min) / Step);
                clamped = Min + steps * Step;
            }

            return Math.Clamp(clamped, Min, Max);
        }

        public static FloatRange DefaultExposure => new(10, 200_000, 50);
        public static FloatRange DefaultGain => new(0, 24, 0.1);
        public static FloatRange DefaultLineRate => new(100, 250_000, 100);
        public static FloatRange Disabled => new(0, 0, 0);
    }

    private readonly struct IntRange
    {
        public IntRange(long min, long max, long step)
        {
            Min = min;
            Max = max;
            Step = step;
        }

        public long Min { get; }
        public long Max { get; }
        public long Step { get; }

        public long Clamp(long value)
        {
            var clamped = Math.Clamp(value, Min, Max);
            if (Step > 0 && Max > Min)
            {
                clamped = Min + ((clamped - Min) / Step) * Step;
            }

            return Math.Clamp(clamped, Min, Max);
        }

        public long ClampFromDouble(double value) => Clamp((long)Math.Round(value));

        public static IntRange DefaultHeight => new(64, 8192, 4);
    }
}
