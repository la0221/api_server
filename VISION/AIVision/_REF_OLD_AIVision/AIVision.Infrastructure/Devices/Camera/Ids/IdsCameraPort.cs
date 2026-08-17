using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using peak;
using peak.core;
using peak.core.nodes;

namespace AIVision.Infrastructure.Devices.Camera.Ids;

public sealed class IdsCameraPort : ICameraPort
{
    private readonly ILogger<IdsCameraPort> _logger;
    private readonly IdsCameraOptions _options;
    private readonly IdsCameraControlPort _controlPort;
    private readonly SemaphoreSlim _openLock = new(1, 1);
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly List<BufferWrapper> _buffers = new();

    private DeviceDescriptor? _deviceDescriptor;
    private peak.core.Device? _device;
    private peak.core.RemoteDevice? _remote;
    private peak.core.DataStream? _dataStream;
    private peak.core.DataStreamDescriptor? _dataStreamDescriptor;
    private peak.core.NodeMap? _remoteNodeMap;
    private CommandNode? _acquisitionStart;
    private CommandNode? _acquisitionStop;
    private IntegerNode? _tlParamsLocked;
    private string? _currentDeviceId;
    private string _pixelFormat = "Mono8";
    private int _xPadding = 0; // 行填充（參考 CameraViewerForm.cs）
    private CancellationTokenSource? _acquisitionCts;
    private Task? _acquisitionTask;
    private bool _isPreviewing;
    private IdsCameraSettings? _currentSettings;

    public IdsCameraPort(
        IOptions<IdsCameraOptions> options,
        IdsCameraControlPort controlPort,
        ILogger<IdsCameraPort> logger)
    {
        _options = options.Value;
        _controlPort = controlPort;
        _logger = logger;
        _controlPort.SettingsChanged += OnSettingsChanged;
    }

    public event EventHandler<ImageData>? FrameReceived;

    public bool IsOpen => _device is not null;

    public async Task OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        await _openLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsOpen && string.Equals(_currentDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await CloseInternalAsync().ConfigureAwait(false);

            IdsPeakLibrary.EnsureInitialized(ResolveSdkDirectory(), _logger);

            var descriptor = FindDeviceDescriptor(deviceId);
            if (descriptor is null)
            {
                throw new InvalidOperationException($"找不到 IDS 相機：{deviceId}");
            }

            _deviceDescriptor = descriptor;

            // 嘗試以不同的存取模式開啟設備（參考 CameraSDK_CSharp）
            var accessType = DeviceAccessType.Control;
            if (descriptor.IsOpenable(DeviceAccessType.Exclusive))
            {
                accessType = DeviceAccessType.Exclusive;
                _logger.LogDebug("使用 Exclusive 模式開啟相機");
            }
            else if (descriptor.IsOpenable(DeviceAccessType.Control))
            {
                _logger.LogDebug("使用 Control 模式開啟相機（Exclusive 不可用）");
            }
            else if (descriptor.IsOpenable(DeviceAccessType.ReadOnly))
            {
                accessType = DeviceAccessType.ReadOnly;
                _logger.LogWarning("使用 ReadOnly 模式開啟相機（Control 和 Exclusive 都不可用）");
            }
            else
            {
                throw new InvalidOperationException("無法以任何模式開啟相機");
            }

            try
            {
                _device = descriptor.OpenDevice(accessType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "開啟相機失敗 (AccessType={AccessType})", accessType);
                throw new InvalidOperationException($"無法開啟相機: {ex.Message}", ex);
            }

            _remote = _device.RemoteDevice();
            _remoteNodeMap = _remote.NodeMaps().ToArray().FirstOrDefault();

            // 載入預設使用者設定（參考 simple_live_windows_forms\BackEnd.cs）
            LoadDefaultUserSet();

            // 查詢並記錄設備資訊（參考 CameraSDK_CSharp）
            LogDeviceInfo();

            _controlPort.UpdateBounds(_remoteNodeMap);
            _acquisitionStart = _remoteNodeMap?.TryFindNodeCommand("AcquisitionStart");
            _acquisitionStop = _remoteNodeMap?.TryFindNodeCommand("AcquisitionStop");
            _tlParamsLocked = _remoteNodeMap?.TryFindNodeInteger("TLParamsLocked");
            _pixelFormat = ResolvePixelFormat();

            // 讀取 XPadding（參考 CameraViewerForm.cs:79-83）
            var xPaddingNode = _remoteNodeMap?.TryFindNodeInteger("XPadding");
            _xPadding = xPaddingNode is not null && xPaddingNode.IsReadable()
                ? (int)xPaddingNode.Value()
                : 0;
            if (_xPadding > 0)
            {
                _logger.LogDebug("✓ XPadding = {XPadding} (影像資料有行填充)", _xPadding);
            }

            (_dataStreamDescriptor, _dataStream) = OpenDefaultDataStream(_device);
            AllocateBuffers(_dataStream);

            _currentDeviceId = deviceId;

            var settings = await _controlPort.GetCurrentSettingsSnapshotAsync(cancellationToken).ConfigureAwait(false);
            _currentSettings = settings;

            await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ApplySettingsInternalAsync(settings, null, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _applyLock.Release();
            }

            _logger.LogInformation("✓ IDS 相機 {DeviceId} 已成功開啟 (AccessType={AccessType})", deviceId, accessType);
        }
        finally
        {
            _openLock.Release();
        }
    }

    public Task StartPreviewAsync(CancellationToken cancellationToken)
    {
        if (_isPreviewing)
        {
            return Task.CompletedTask;
        }

        EnsureReady();
        BeginAcquisition();

        _acquisitionCts = new CancellationTokenSource();
        _acquisitionTask = Task.Run(() => AcquisitionLoop(_acquisitionCts.Token), CancellationToken.None);
        _isPreviewing = true;

        return Task.CompletedTask;
    }

    public async Task StopPreviewAsync(CancellationToken cancellationToken)
    {
        if (!_isPreviewing)
        {
            return;
        }

        // 先標記為非預覽狀態，防止重複呼叫
        _isPreviewing = false;

        // 取得並清空 CTS 引用，防止並發問題
        var cts = Interlocked.Exchange(ref _acquisitionCts, null);
        var task = Interlocked.Exchange(ref _acquisitionTask, null);

        if (cts is not null)
        {
            try
            {
                // 安全地取消（如果尚未被取消或 dispose）
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // CTS 已被 dispose，忽略
                _logger.LogDebug("CancellationTokenSource 已被釋放，跳過取消操作");
            }

            try
            {
                if (task is not null)
                {
                    await task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止 IDS 預覽時發生例外。");
            }
            finally
            {
                try
                {
                    cts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // 已被 dispose，忽略
                }
            }
        }

        EndAcquisition();
    }

    public async Task<ImageData> CaptureOnceAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("──── CaptureOnceAsync 開始 ────");
        EnsureReady();
        _logger.LogInformation("  EnsureReady 完成, _isPreviewing={IsPreviewing}", _isPreviewing);

        if (_isPreviewing)
        {
            _logger.LogInformation("  正在預覽中，等待下一幀...");
            var tcs = new TaskCompletionSource<ImageData>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? _, ImageData data) => tcs.TrySetResult(data);

            FrameReceived += Handler;
            try
            {
                using var ctr = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                var result = await tcs.Task.ConfigureAwait(false);
                _logger.LogInformation("  預覽模式擷取完成: {Width}x{Height}", result.Width, result.Height);
                return result;
            }
            finally
            {
                FrameReceived -= Handler;
            }
        }

        _logger.LogInformation("  非預覽模式，等待 _applyLock...");
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("  取得 _applyLock");
        try
        {
            _logger.LogInformation("  呼叫 BeginAcquisition...");
            BeginAcquisition();
            _logger.LogInformation("  BeginAcquisition 完成，等待影像...");
            try
            {
                var result = await WaitForSingleImageAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("  WaitForSingleImageAsync 完成: {Width}x{Height}", result.Width, result.Height);
                return result;
            }
            finally
            {
                _logger.LogInformation("  呼叫 EndAcquisition...");
                EndAcquisition();
                _logger.LogInformation("  EndAcquisition 完成");
            }
        }
        finally
        {
            _applyLock.Release();
            _logger.LogInformation("──── CaptureOnceAsync 結束 ────");
        }
    }

    public ValueTask DisposeAsync()
    {
        _controlPort.SettingsChanged -= OnSettingsChanged;
        return new ValueTask(CloseInternalAsync());
    }

    private async Task CloseInternalAsync()
    {
        await StopPreviewAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (var wrapper in _buffers)
        {
            try
            {
                _dataStream?.RevokeBuffer(wrapper.Buffer);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RevokeBuffer 失敗。");
            }

            wrapper.Dispose();
        }

        _buffers.Clear();

        try
        {
            _dataStream?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "釋放 DataStream 失敗。");
        }
        finally
        {
            _dataStream = null;
        }

        try
        {
            _dataStreamDescriptor?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "釋放 DataStreamDescriptor 失敗。");
        }
        finally
        {
            _dataStreamDescriptor = null;
        }

        try
        {
            _device?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "釋放 Device 失敗。");
        }
        finally
        {
            _device = null;
            _remote = null;
            _remoteNodeMap = null;
            _acquisitionStart = null;
            _acquisitionStop = null;
            _tlParamsLocked = null;
            _currentDeviceId = null;
            _deviceDescriptor?.Dispose();
            _deviceDescriptor = null;
        }

        // 關閉 IDS Peak SDK，確保相機資源完全釋放
        // 這樣下次啟動程式時才能正常開啟相機
        try
        {
            IdsPeakLibrary.Shutdown();
            _logger.LogInformation("IDS Peak SDK 已關閉");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "關閉 IDS Peak SDK 時發生錯誤");
        }
    }

    private void OnSettingsChanged(object? sender, IdsCameraSettingsChangedEventArgs e)
    {
        _currentSettings = e.Settings;
        if (!IsOpen)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _applyLock.WaitAsync().ConfigureAwait(false);
                await ApplySettingsInternalAsync(e.Settings, e.Kind, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "套用 IDS 相機參數變更失敗 ({Kind})。", e.Kind);
            }
            finally
            {
                _applyLock.Release();
            }
        });
    }

    private async Task ApplySettingsInternalAsync(IdsCameraSettings settings, CameraParameterKind? changedKind, CancellationToken cancellationToken)
    {
        if (_remoteNodeMap is null)
        {
            return;
        }

        if (changedKind is null || changedKind == CameraParameterKind.ExposureTime)
        {
            await SafeReconfigureAsync(() => ApplyExposure(settings.ExposureTimeUs), requiresBufferRecreate: false, cancellationToken, "Exposure").ConfigureAwait(false);
        }

        if (changedKind is null || changedKind == CameraParameterKind.Gain)
        {
            await SafeReconfigureAsync(() => ApplyGain(settings.GainSelector, settings.Gain), requiresBufferRecreate: false, cancellationToken, "Gain").ConfigureAwait(false);
        }

        if (changedKind is null || changedKind == CameraParameterKind.Height)
        {
            await SafeReconfigureAsync(() => ApplyHeight(settings.Height), requiresBufferRecreate: true, cancellationToken, "Height").ConfigureAwait(false);
        }

        if ((changedKind is null || changedKind == CameraParameterKind.AcquisitionLineRate) && settings.AcquisitionLineRate.HasValue)
        {
            await SafeReconfigureAsync(() => ApplyLineRate(settings.AcquisitionLineRate), requiresBufferRecreate: false, cancellationToken, "LineRate").ConfigureAwait(false);
        }

        // ROI 參數 (OffsetX, OffsetY, Width) - 需要 Buffer 重建
        if (changedKind == CameraParameterKind.OffsetX)
        {
            await SafeReconfigureAsync(() => ApplyOffsetX(settings.OffsetX), requiresBufferRecreate: false, cancellationToken, "OffsetX").ConfigureAwait(false);
        }

        if (changedKind == CameraParameterKind.OffsetY)
        {
            await SafeReconfigureAsync(() => ApplyOffsetY(settings.OffsetY), requiresBufferRecreate: false, cancellationToken, "OffsetY").ConfigureAwait(false);
        }

        if (changedKind == CameraParameterKind.Width && settings.Width > 0)
        {
            await SafeReconfigureAsync(() => ApplyWidth(settings.Width), requiresBufferRecreate: true, cancellationToken, "Width").ConfigureAwait(false);
        }
    }

    private void ApplyExposure(double exposureUs)
    {
        var exposureMode = _remoteNodeMap!.TryFindNodeEnumeration("ExposureMode");
        var exposureAuto = _remoteNodeMap.TryFindNodeEnumeration("ExposureAuto");
        var exposureTime = _remoteNodeMap.TryFindNodeFloat("ExposureTime");

        // Step 1: 設定曝光模式
        if (exposureMode?.IsWriteable() == true && exposureMode.HasEntry("Timed"))
        {
            try
            {
                exposureMode.SetCurrentEntry("Timed");
                _logger.LogDebug("✓ ExposureMode = Timed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "設定 ExposureMode 失敗");
            }
        }

        // Step 2: 關閉自動曝光
        if (exposureAuto?.IsWriteable() == true && exposureAuto.HasEntry("Off"))
        {
            try
            {
                exposureAuto.SetCurrentEntry("Off");
                _logger.LogDebug("✓ ExposureAuto = Off");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "設定 ExposureAuto 失敗");
            }
        }

        // Step 3: 設定曝光時間（加強驗證）
        if (exposureTime?.IsWriteable() == true)
        {
            try
            {
                var min = exposureTime.Minimum();
                var max = exposureTime.Maximum();
                var current = exposureTime.Value();

                _logger.LogDebug("曝光時間範圍: Min={Min}µs, Max={Max}µs, Current={Current}µs, Requested={Requested}µs",
                    min, max, current, exposureUs);

                // 驗證範圍
                if (exposureUs < min || exposureUs > max)
                {
                    var safeValue = Math.Clamp(exposureUs, min, max);
                    _logger.LogWarning("⚠ 設定的曝光時間 {Exposure}µs 超出範圍 [{Min}, {Max}]，調整為 {SafeValue}µs",
                        exposureUs, min, max, safeValue);
                    exposureUs = safeValue;
                }

                var value = ClampToIncrement(exposureTime, exposureUs);
                exposureTime.SetValue(value);
                _logger.LogInformation("✓ ExposureTime = {Value}µs (調整後)", value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "✗ 設定 ExposureTime 失敗 - 這可能導致相機無法產生影像！");
                throw; // 曝光設定失敗是嚴重錯誤
            }
        }
        else
        {
            _logger.LogWarning("⚠ ExposureTime 節點不可寫入或不存在");
        }
    }

    private void ApplyGain(string selector, double gain)
    {
        var gainAuto = _remoteNodeMap!.TryFindNodeEnumeration("GainAuto");
        var gainSelector = _remoteNodeMap.TryFindNodeEnumeration("GainSelector");
        var gainNode = _remoteNodeMap.TryFindNodeFloat("Gain");

        if (gainAuto?.IsWriteable() == true && gainAuto.HasEntry("Off"))
        {
            gainAuto.SetCurrentEntry("Off");
        }

        if (gainSelector?.IsWriteable() == true && gainSelector.HasEntry(selector))
        {
            gainSelector.SetCurrentEntry(selector);
        }

        if (gainNode?.IsWriteable() == true)
        {
            var value = ClampToIncrement(gainNode, gain);
            gainNode.SetValue(value);
        }
    }

    private void ApplyHeight(long height)
    {
        var heightNode = _remoteNodeMap!.TryFindNodeInteger("Height");
        if (heightNode?.IsWriteable() == true)
        {
            var value = ClampToIncrement(heightNode, height);
            heightNode.SetValue(value);
            _logger.LogDebug("✓ Height = {Value}", value);
        }
    }

    private void ApplyOffsetX(long offsetX)
    {
        var node = _remoteNodeMap!.TryFindNodeInteger("OffsetX");
        if (node?.IsWriteable() == true)
        {
            var value = ClampToIncrement(node, offsetX);
            node.SetValue(value);
            _logger.LogDebug("✓ OffsetX = {Value}", value);
        }
        else
        {
            _logger.LogWarning("OffsetX 節點不可寫入或不存在");
        }
    }

    private void ApplyOffsetY(long offsetY)
    {
        var node = _remoteNodeMap!.TryFindNodeInteger("OffsetY");
        if (node?.IsWriteable() == true)
        {
            var value = ClampToIncrement(node, offsetY);
            node.SetValue(value);
            _logger.LogDebug("✓ OffsetY = {Value}", value);
        }
        else
        {
            _logger.LogWarning("OffsetY 節點不可寫入或不存在");
        }
    }

    private void ApplyWidth(long width)
    {
        var node = _remoteNodeMap!.TryFindNodeInteger("Width");
        if (node?.IsWriteable() == true)
        {
            var value = ClampToIncrement(node, width);
            node.SetValue(value);
            _logger.LogDebug("✓ Width = {Value}", value);
        }
        else
        {
            _logger.LogWarning("Width 節點不可寫入或不存在");
        }
    }

    /// <summary>
    /// 設定完整的 Line Scan ROI (公開方法供 LineScanService 使用)
    /// 注意：設定順序很重要，要避免參數相依性問題
    /// 關鍵：必須先載入 Linescan UserSet 才能正確設定 Line Scan 參數
    /// </summary>
    /// <param name="offsetX">X 偏移量</param>
    /// <param name="offsetY">Y 偏移量 (決定掃描哪一行)</param>
    /// <param name="width">寬度</param>
    /// <param name="height">高度 (硬體累積行數)</param>
    /// <param name="exposureTimeUs">曝光時間 (µs)，如果為 null 則使用當前設定</param>
    /// <param name="gain">增益，如果為 null 則使用當前設定</param>
    /// <param name="lineRate">行頻 (Hz)，如果為 null 則使用當前設定</param>
    /// <param name="userSetName">要載入的 UserSet 名稱 (UserSet0, UserSet1, Linescan, 或 Default)</param>
    /// <returns>實際使用的 ROI 設定 (Width, Height, LineRate)</returns>
    public (int Width, int Height, double LineRate) ApplyLineScanRoi(long offsetX, long offsetY, long width, long height = 1,
        double? exposureTimeUs = null, double? gain = null, double? lineRate = null, string userSetName = "Linescan")
    {
        if (_remoteNodeMap is null)
        {
            throw new InvalidOperationException("相機尚未開啟");
        }

        _logger.LogInformation("══════ ApplyLineScanRoi 開始 ══════");

        // 判斷是否使用自訂 UserSet (UserSet0/UserSet1)
        // 這些 UserSet 的所有參數都已儲存在相機 EEPROM 中，我們只需載入即可
        var isCustomUserSet = userSetName == "UserSet0" || userSetName == "UserSet1";

        if (isCustomUserSet)
        {
            _logger.LogInformation("  使用自訂 UserSet '{UserSet}' - 所有參數將從 EEPROM 載入", userSetName);
        }
        else
        {
            _logger.LogInformation("  目標設定: OffsetX={OffsetX}, OffsetY={OffsetY}, Width={Width}, Height={Height}",
                offsetX, offsetY, width, height);
        }

        // Step 0: 載入指定的 UserSet (關鍵！必須在設定 ROI 前執行)
        // 這會將相機切換到 Line Scan 模式，使 OffsetY 可以設定到感測器的任意行
        _logger.LogInformation("  Step 0: 載入 UserSet '{UserSet}'...", userSetName);
        var userSetLoaded = LoadUserSet(userSetName);
        if (!userSetLoaded)
        {
            _logger.LogWarning("  ⚠ 無法載入 UserSet '{UserSet}'，可能會導致 Line Scan 參數設定不正確", userSetName);
        }

        // 如果使用自訂 UserSet，直接使用 EEPROM 中的所有參數，不再覆蓋
        if (isCustomUserSet)
        {
            // 讀取並記錄 UserSet 中的實際值
            var actualWidth = _remoteNodeMap.TryFindNodeInteger("Width")?.Value() ?? 0;
            var actualHeight = _remoteNodeMap.TryFindNodeInteger("Height")?.Value() ?? 0;
            var actualOffsetX = _remoteNodeMap.TryFindNodeInteger("OffsetX")?.Value() ?? 0;
            var actualOffsetY = _remoteNodeMap.TryFindNodeInteger("OffsetY")?.Value() ?? 0;

            _logger.LogInformation("  ✓ 使用 EEPROM 設定: Width={Width}, Height={Height}, OffsetX={OffsetX}, OffsetY={OffsetY}",
                actualWidth, actualHeight, actualOffsetX, actualOffsetY);

            // 讀取並記錄相機參數
            var exposureNode = _remoteNodeMap.TryFindNodeFloat("ExposureTime");
            var gainNode = _remoteNodeMap.TryFindNodeFloat("Gain");
            var lineRateNode = _remoteNodeMap.TryFindNodeFloat("AcquisitionLineRate");

            if (exposureNode?.IsReadable() == true)
                _logger.LogInformation("  ✓ EEPROM ExposureTime = {Value:F2} µs", exposureNode.Value());
            if (gainNode?.IsReadable() == true)
                _logger.LogInformation("  ✓ EEPROM Gain = {Value:F2}", gainNode.Value());
            if (lineRateNode?.IsReadable() == true)
                _logger.LogInformation("  ✓ EEPROM LineRate = {Value:F2} Hz", lineRateNode.Value());

            // 如果有傳入 LineRate > 0，在 EEPROM 載入後額外設定（覆蓋 EEPROM 的 LineRate）
            // LineRate <= 0 表示使用 EEPROM 的設定，不覆蓋
            if (lineRate.HasValue && lineRate.Value > 0 && lineRateNode?.IsWriteable() == true)
            {
                _logger.LogInformation("  Step 5a: 覆蓋 EEPROM LineRate，設定為 {LineRate:F2} Hz...", lineRate.Value);
                ApplyLineRate(lineRate.Value);
                var newLineRate = lineRateNode.Value();
                _logger.LogInformation("  ✓ LineRate 已設定為 {Value:F2} Hz", newLineRate);
            }
            else if (lineRateNode?.IsReadable() == true)
            {
                _logger.LogInformation("  ✓ 使用 EEPROM LineRate = {Value:F2} Hz (不覆蓋)", lineRateNode.Value());
            }
        }
        else
        {
            // 使用程式提供的參數設定 ROI

            // Step 1: 先重置 Offset 為 0，避免 Width/Height 設定失敗
            _logger.LogInformation("  Step 1: 重置 Offset 為 0...");
            ApplyOffsetX(0);
            ApplyOffsetY(0);

            // Step 2: 設定 Width (如果 width > 0)
            if (width > 0)
            {
                _logger.LogInformation("  Step 2: 設定 Width={Width}...", width);
                ApplyWidth(width);
            }

            // Step 3: 設定 Height (關鍵！硬體累積模式)
            _logger.LogInformation("  Step 3: 設定 Height={Height} (硬體累積模式)...", height);
            ApplyHeight(height);

            // Step 4: 設定 Offset
            _logger.LogInformation("  Step 4: 設定 Offset...");
            ApplyOffsetX(offsetX);
            ApplyOffsetY(offsetY);

            // 驗證設定結果
            var actualWidth = _remoteNodeMap.TryFindNodeInteger("Width")?.Value() ?? 0;
            var actualHeight = _remoteNodeMap.TryFindNodeInteger("Height")?.Value() ?? 0;
            var actualOffsetX = _remoteNodeMap.TryFindNodeInteger("OffsetX")?.Value() ?? 0;
            var actualOffsetY = _remoteNodeMap.TryFindNodeInteger("OffsetY")?.Value() ?? 0;

            _logger.LogInformation("  ✓ 實際設定結果: Width={Width}, Height={Height}, OffsetX={OffsetX}, OffsetY={OffsetY}",
                actualWidth, actualHeight, actualOffsetX, actualOffsetY);

            // 檢查 OffsetY 是否符合預期 (這是 Line Scan 模式的關鍵指標)
            if (actualOffsetY != offsetY)
            {
                _logger.LogWarning("  ⚠ OffsetY 設定不符預期! 目標={Target}, 實際={Actual}", offsetY, actualOffsetY);
                _logger.LogWarning("  這可能表示相機沒有正確切換到 Line Scan 模式");
            }
            else
            {
                _logger.LogInformation("  ✓ OffsetY 設定成功: {OffsetY} (Line Scan 將從感測器第 {Row} 行掃描)", actualOffsetY, actualOffsetY);
            }

            // 檢查 Height 是否符合預期
            if (actualHeight != height)
            {
                _logger.LogWarning("  ⚠ Height 設定不符預期! 目標={Target}, 實際={Actual}", height, actualHeight);
            }
            else
            {
                _logger.LogInformation("  ✓ Height 設定成功: {Height} (相機將累積 {Height} 行後輸出)", actualHeight, actualHeight);
            }

            // Step 5: 套用行頻、曝光時間和增益
            _logger.LogInformation("  Step 5: 套用行頻、曝光時間和增益...");
            ApplyLineScanParameters(exposureTimeUs, gain, lineRate);
        }

        // Step 6: 重新讀取 XPadding (Line Scan 模式可能有不同的值)
        var xPaddingNode = _remoteNodeMap.TryFindNodeInteger("XPadding");
        var oldXPadding = _xPadding;
        _xPadding = xPaddingNode is not null && xPaddingNode.IsReadable()
            ? (int)xPaddingNode.Value()
            : 0;
        _logger.LogInformation("  Step 6: XPadding = {XPadding} (之前: {Old})", _xPadding, oldXPadding);

        // Step 7: 重新分配緩衝區 (關鍵！ROI 改變後 PayloadSize 會改變)
        _logger.LogInformation("  Step 7: 重新分配緩衝區...");
        RecreateBuffers();
        _logger.LogInformation("  ✓ 緩衝區已重新分配");

        // 讀取實際使用的 ROI 設定 (可能與傳入參數不同，特別是使用 UserSet0/UserSet1 時)
        var finalWidth = (int)(_remoteNodeMap.TryFindNodeInteger("Width")?.Value() ?? width);
        var finalHeight = (int)(_remoteNodeMap.TryFindNodeInteger("Height")?.Value() ?? height);
        var finalLineRate = _remoteNodeMap.TryFindNodeFloat("AcquisitionLineRate")?.Value() ?? lineRate ?? 0;

        _logger.LogInformation("══════ ApplyLineScanRoi 結束 ══════");

        return (finalWidth, finalHeight, finalLineRate);
    }

    /// <summary>
    /// 套用 Line Scan 模式的行頻、曝光時間和增益
    /// 重要：載入 Linescan UserSet 會重置相機參數，所以需要重新套用
    /// </summary>
    private void ApplyLineScanParameters(double? exposureTimeUs, double? gain, double? lineRate)
    {
        if (_remoteNodeMap is null) return;

        _logger.LogInformation("  Step 5: 套用行頻、曝光和增益...");

        try
        {
            var exposureNode = _remoteNodeMap.TryFindNodeFloat("ExposureTime");
            var gainNode = _remoteNodeMap.TryFindNodeFloat("Gain");
            var lineRateNode = _remoteNodeMap.TryFindNodeFloat("AcquisitionLineRate");

            // 1. 套用行頻 (必須在曝光時間之前設定，因為行頻會影響曝光時間的最大值)
            if (lineRateNode is not null && lineRateNode.IsWriteable() && lineRate.HasValue && lineRate.Value > 0)
            {
                var lineRateMin = lineRateNode.Minimum();
                var lineRateMax = lineRateNode.Maximum();
                var clampedLineRate = Math.Clamp(lineRate.Value, lineRateMin, lineRateMax);
                clampedLineRate = ClampToIncrement(lineRateNode, clampedLineRate);

                lineRateNode.SetValue(clampedLineRate);
                var linePeriodUs = 1000000.0 / clampedLineRate;
                _logger.LogInformation("    ✓ 行頻已設定: {LineRate:F2} Hz (行週期: {Period:F2} µs)",
                    clampedLineRate, linePeriodUs);
            }
            else if (lineRateNode is not null && lineRateNode.IsReadable())
            {
                var currentLineRate = lineRateNode.Value();
                var linePeriodUs = 1000000.0 / currentLineRate;
                _logger.LogInformation("    行週期: {Period:F2} µs (LineRate={Rate:F2} Hz, 未修改)",
                    linePeriodUs, currentLineRate);
            }

            // 2. 套用曝光時間
            if (exposureNode is not null && exposureNode.IsWriteable())
            {
                var exposureMin = exposureNode.Minimum();
                var exposureMax = exposureNode.Maximum();
                _logger.LogInformation("    曝光範圍: {Min:F2} ~ {Max:F2} µs", exposureMin, exposureMax);

                if (exposureTimeUs.HasValue)
                {
                    // 使用指定的曝光時間，但限制在有效範圍內
                    var targetExposure = exposureTimeUs.Value;
                    var clampedExposure = Math.Clamp(targetExposure, exposureMin, exposureMax);
                    clampedExposure = ClampToIncrement(exposureNode, clampedExposure);

                    exposureNode.SetValue(clampedExposure);

                    if (Math.Abs(clampedExposure - targetExposure) > 1)
                    {
                        _logger.LogWarning("    ⚠ 曝光時間 {Target:F0} µs 超出範圍，已調整為 {Actual:F0} µs",
                            targetExposure, clampedExposure);
                    }
                    else
                    {
                        _logger.LogInformation("    ✓ 曝光時間已設定: {Exposure:F0} µs", clampedExposure);
                    }
                }
                else
                {
                    // 沒有指定曝光時間，使用合理的預設值 (行週期的 80%)
                    if (lineRateNode is not null && lineRateNode.IsReadable())
                    {
                        var currentLineRate = lineRateNode.Value();
                        var linePeriodUs = 1000000.0 / currentLineRate;
                        var defaultExposure = Math.Min(linePeriodUs * 0.8, exposureMax);
                        defaultExposure = Math.Max(defaultExposure, exposureMin);
                        defaultExposure = ClampToIncrement(exposureNode, defaultExposure);

                        exposureNode.SetValue(defaultExposure);
                        _logger.LogInformation("    ✓ 使用預設曝光時間: {Exposure:F0} µs (行週期 80%)", defaultExposure);
                    }
                }
            }

            // 3. 套用增益
            if (gainNode is not null && gainNode.IsWriteable() && gain.HasValue)
            {
                var gainMin = gainNode.Minimum();
                var gainMax = gainNode.Maximum();
                var clampedGain = Math.Clamp(gain.Value, gainMin, gainMax);
                clampedGain = ClampToIncrement(gainNode, clampedGain);

                gainNode.SetValue(clampedGain);
                _logger.LogInformation("    ✓ 增益已設定: {Gain:F2}", clampedGain);
            }

            // 顯示最終設定
            var finalLineRate = lineRateNode?.Value() ?? 0;
            var finalExposure = exposureNode?.Value() ?? 0;
            var finalGain = gainNode?.Value() ?? 0;
            _logger.LogInformation("    最終設定: 行頻={LineRate:F2} Hz, 曝光={Exposure:F0} µs, 增益={Gain:F2}",
                finalLineRate, finalExposure, finalGain);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "套用行頻、曝光和增益失敗");
        }
    }

    /// <summary>
    /// 取得 ROI 參數的有效範圍
    /// </summary>
    public (long minX, long maxX, long minY, long maxY, long minW, long maxW, long minH, long maxH)? GetRoiBounds()
    {
        if (_remoteNodeMap is null) return null;

        try
        {
            var offsetX = _remoteNodeMap.TryFindNodeInteger("OffsetX");
            var offsetY = _remoteNodeMap.TryFindNodeInteger("OffsetY");
            var width = _remoteNodeMap.TryFindNodeInteger("Width");
            var height = _remoteNodeMap.TryFindNodeInteger("Height");

            return (
                offsetX?.Minimum() ?? 0, offsetX?.Maximum() ?? 0,
                offsetY?.Minimum() ?? 0, offsetY?.Maximum() ?? 0,
                width?.Minimum() ?? 0, width?.Maximum() ?? 0,
                height?.Minimum() ?? 0, height?.Maximum() ?? 0
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取 ROI 邊界失敗");
            return null;
        }
    }

    /// <summary>
    /// 取得感測器最大解析度
    /// </summary>
    public (long width, long height)? GetSensorSize()
    {
        if (_remoteNodeMap is null) return null;

        try
        {
            var sensorWidth = _remoteNodeMap.TryFindNodeInteger("SensorWidth")?.Value();
            var sensorHeight = _remoteNodeMap.TryFindNodeInteger("SensorHeight")?.Value();

            // 如果沒有 SensorWidth/Height，使用 Width/Height 的最大值
            if (!sensorWidth.HasValue || !sensorHeight.HasValue)
            {
                var widthNode = _remoteNodeMap.TryFindNodeInteger("Width");
                var heightNode = _remoteNodeMap.TryFindNodeInteger("Height");
                sensorWidth = widthNode?.Maximum();
                sensorHeight = heightNode?.Maximum();
            }

            if (sensorWidth.HasValue && sensorHeight.HasValue)
            {
                return (sensorWidth.Value, sensorHeight.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取感測器尺寸失敗");
        }

        return null;
    }

    private void ApplyLineRate(double? lineRate)
    {
        var lineRateNode = _remoteNodeMap!.TryFindNodeFloat("AcquisitionLineRate");

        // 診斷 log
        _logger.LogDebug("ApplyLineRate: lineRate={LineRate}, nodeIsNull={IsNull}, isWriteable={IsWriteable}",
            lineRate?.ToString("F2") ?? "null",
            lineRateNode is null,
            lineRateNode?.IsWriteable() ?? false);

        if (lineRateNode is null)
        {
            _logger.LogWarning("ApplyLineRate: AcquisitionLineRate 節點不存在");
            return;
        }

        if (!lineRateNode.IsWriteable())
        {
            _logger.LogWarning("ApplyLineRate: AcquisitionLineRate 節點不可寫入");
            return;
        }

        if (!lineRate.HasValue || lineRate.Value <= 0)
        {
            _logger.LogWarning("ApplyLineRate: lineRate 無效 ({Value})", lineRate?.ToString("F2") ?? "null");
            return;
        }

        var triggerSelector = _remoteNodeMap!.TryFindNodeEnumeration("TriggerSelector");
        var triggerMode = _remoteNodeMap.TryFindNodeEnumeration("TriggerMode");

        if (triggerSelector?.IsWriteable() == true && triggerSelector.HasEntry("LineStart"))
        {
            triggerSelector.SetCurrentEntry("LineStart");
            _logger.LogDebug("ApplyLineRate: TriggerSelector = LineStart");
        }

        if (triggerMode?.IsWriteable() == true && triggerMode.HasEntry("Off"))
        {
            triggerMode.SetCurrentEntry("Off");
            _logger.LogDebug("ApplyLineRate: TriggerMode = Off");
        }

        var currentValue = lineRateNode.Value();
        var min = lineRateNode.Minimum();
        var max = lineRateNode.Maximum();
        var value = ClampToIncrement(lineRateNode, lineRate.Value);

        _logger.LogInformation("ApplyLineRate: 設定行頻 {Current:F2} → {Target:F2} Hz (範圍: {Min:F2}~{Max:F2})",
            currentValue, value, min, max);

        lineRateNode.SetValue(value);

        // 驗證設定結果
        var actualValue = lineRateNode.Value();
        _logger.LogInformation("ApplyLineRate: 實際行頻 = {Value:F2} Hz", actualValue);
    }

    private void ConfigureAcquisitionMode()
    {
        if (_remoteNodeMap is null)
        {
            _logger.LogWarning("NodeMap 為 null，無法設定擷取模式");
            return;
        }

        try
        {
            // 1. 設定 TriggerMode = Off（關鍵！讓相機自由運行）
            var triggerModeNode = _remoteNodeMap.TryFindNodeEnumeration("TriggerMode");
            if (triggerModeNode is not null && triggerModeNode.IsWriteable())
            {
                if (triggerModeNode.HasEntry("Off"))
                {
                    triggerModeNode.SetCurrentEntry("Off");
                    _logger.LogInformation("✓ TriggerMode = Off (自由運行模式)");
                }
            }
            else
            {
                _logger.LogDebug("TriggerMode 節點不可用或不可寫入");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "設定 TriggerMode 失敗");
        }

        try
        {
            // 2. 設定 AcquisitionMode = Continuous（連續擷取）
            var acquisitionModeNode = _remoteNodeMap.TryFindNodeEnumeration("AcquisitionMode");
            if (acquisitionModeNode is not null && acquisitionModeNode.IsWriteable())
            {
                if (acquisitionModeNode.HasEntry("Continuous"))
                {
                    acquisitionModeNode.SetCurrentEntry("Continuous");
                    _logger.LogInformation("✓ AcquisitionMode = Continuous (連續擷取模式)");
                }
            }
            else
            {
                _logger.LogDebug("AcquisitionMode 節點不可用或不可寫入");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "設定 AcquisitionMode 失敗");
        }

        try
        {
            // 3. 設定 TriggerSelector = FrameStart（如果存在）
            var triggerSelectorNode = _remoteNodeMap.TryFindNodeEnumeration("TriggerSelector");
            if (triggerSelectorNode is not null && triggerSelectorNode.IsWriteable())
            {
                if (triggerSelectorNode.HasEntry("FrameStart"))
                {
                    triggerSelectorNode.SetCurrentEntry("FrameStart");
                    _logger.LogDebug("✓ TriggerSelector = FrameStart");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "設定 TriggerSelector 失敗（可能不支援）");
        }
    }

    private void BeginAcquisition()
    {
        _logger.LogInformation("▶ BeginAcquisition 開始...");

        // Step 0: 設定擷取模式（關鍵！必須在啟動前設定）
        _logger.LogInformation("  Step 0: ConfigureAcquisitionMode...");
        ConfigureAcquisitionMode();

        // Step 1: 確保緩衝區在佇列中（關鍵修正！參考官方範例）
        _logger.LogInformation("  Step 1: EnsureBuffersQueued...");
        EnsureBuffersQueued();

        // Step 2: 鎖定傳輸層參數
        _logger.LogInformation("  Step 2: TLParamsLocked...");
        try
        {
            if (_tlParamsLocked?.IsWriteable() == true)
            {
                _tlParamsLocked.SetValue(1);
                _logger.LogInformation("  ✓ TLParamsLocked = 1");
            }
            else
            {
                _logger.LogWarning("  TLParamsLocked 不可寫入");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "  設定 TLParamsLocked 失敗");
        }

        // 注意：不要在此處 FlushDataStream！這會清空緩衝區佇列導致 WaitForFinishedBuffer 超時
        // 參考官方範例 simple_live_wpf，StartAcquisition 前不需要 Flush

        // Step 3: 啟動資料流（必須在 AcquisitionStart 之前）
        _logger.LogInformation("  Step 3: DataStream.StartAcquisition...");
        try
        {
            _dataStream!.StartAcquisition();
            _logger.LogInformation("  ✓ DataStream.StartAcquisition 成功");
        }
        catch (Exception ex) when (ex.Message.Contains("buffer") || ex.Message.Contains("BAD_ACCESS"))
        {
            // Buffer 問題，嘗試重建後重試
            _logger.LogWarning(ex, "  ⚠ DataStream.StartAcquisition 失敗 (buffer 問題)，嘗試重建 buffer...");

            try
            {
                // 重建 buffer
                RecreateBuffers();
                _logger.LogInformation("  ✓ Buffer 重建完成，重試 StartAcquisition...");

                _dataStream!.StartAcquisition();
                _logger.LogInformation("  ✓ DataStream.StartAcquisition 成功 (重試後)");
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx, "  ✗ DataStream.StartAcquisition 重試失敗");

                // 恢復參數鎖
                try
                {
                    if (_tlParamsLocked?.IsWriteable() == true)
                    {
                        _tlParamsLocked.SetValue(0);
                    }
                }
                catch (Exception resetEx)
                {
                    _logger.LogWarning(resetEx, "TLParamsLocked 重置失敗（資料流啟動錯誤恢復期間）");
                }

                throw new InvalidOperationException("無法啟動資料流（重試後仍失敗）", retryEx);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "  ✗ DataStream.StartAcquisition 失敗");

            // 恢復參數鎖
            try
            {
                if (_tlParamsLocked?.IsWriteable() == true)
                {
                    _tlParamsLocked.SetValue(0);
                }
            }
            catch (Exception resetEx)
            {
                _logger.LogWarning(resetEx, "TLParamsLocked 重置失敗（資料流啟動錯誤恢復期間）");
            }

            throw new InvalidOperationException("無法啟動資料流", ex);
        }

        // Step 4: 執行 AcquisitionStart 命令（啟動相機擷取）
        _logger.LogInformation("  Step 4: AcquisitionStart...");
        try
        {
            if (_acquisitionStart?.IsWriteable() == true)
            {
                _acquisitionStart.Execute();
                _logger.LogInformation("  AcquisitionStart.Execute() 已呼叫");

                // 等待命令完成（參考 AcquisitionWorker.cs:94-95）
                try
                {
                    _acquisitionStart.WaitUntilDone();
                    _logger.LogInformation("✓ AcquisitionStart 命令執行成功 - 相機開始擷取");
                }
                catch (Exception waitEx)
                {
                    _logger.LogWarning(waitEx, "⚠ AcquisitionStart.WaitUntilDone() 超時或失敗（非致命錯誤，繼續執行）");
                    // 非致命錯誤，繼續執行
                }
            }
            else if (_acquisitionStart is null)
            {
                _logger.LogWarning("⚠ AcquisitionStart 命令節點為 null");
            }
            else
            {
                _logger.LogWarning("⚠ AcquisitionStart 命令不可寫入 (IsWriteable = false)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "✗ 執行 AcquisitionStart 失敗");

            // 停止資料流
            try
            {
                _dataStream!.StopAcquisition();
            }
            catch (Exception stopEx)
            {
                _logger.LogWarning(stopEx, "DataStream.StopAcquisition 失敗（AcquisitionStart 錯誤恢復期間）");
            }

            // 恢復參數鎖
            try
            {
                if (_tlParamsLocked?.IsWriteable() == true)
                {
                    _tlParamsLocked.SetValue(0);
                }
            }
            catch (Exception resetEx)
            {
                _logger.LogWarning(resetEx, "TLParamsLocked 重置失敗（AcquisitionStart 錯誤恢復期間）");
            }

            throw new InvalidOperationException("無法執行 AcquisitionStart 命令", ex);
        }

        _logger.LogInformation("▶ BeginAcquisition 完成 ✓");
    }

    private void FlushDataStream()
    {
        if (_dataStream is null)
        {
            return;
        }

        try
        {
            _dataStream.Flush(DataStreamFlushMode.DiscardAll);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Flush DataStream 失敗。");
        }
    }

    /// <summary>
    /// 確保所有緩衝區都在 DataStream 佇列中。
    /// 這是解決 WaitForFinishedBuffer 超時問題的關鍵方法。
    /// 參考 IDS 官方範例 simple_live_wpf。
    /// </summary>
    private void EnsureBuffersQueued()
    {
        if (_dataStream is null)
        {
            _logger.LogWarning("EnsureBuffersQueued: DataStream 為 null");
            return;
        }

        var queuedCount = 0;
        var alreadyQueuedCount = 0;
        var errorCount = 0;

        foreach (var wrapper in _buffers)
        {
            try
            {
                // 嘗試將緩衝區排入佇列
                _dataStream.QueueBuffer(wrapper.Buffer);
                queuedCount++;
            }
            catch (Exception ex)
            {
                // 如果緩衝區已經在佇列中，會拋出異常，這是正常的
                if (ex.Message.Contains("ALREADY_QUEUED") || ex.Message.Contains("already"))
                {
                    alreadyQueuedCount++;
                }
                else
                {
                    errorCount++;
                    _logger.LogDebug(ex, "排入緩衝區失敗: {Message}", ex.Message);
                }
            }
        }

        _logger.LogInformation("  ✓ 緩衝區狀態: 總數={Total}, 新排入={Queued}, 已在佇列={AlreadyQueued}, 錯誤={Error}",
            _buffers.Count, queuedCount, alreadyQueuedCount, errorCount);
    }

    private void EndAcquisition()
    {
        _logger.LogDebug("停止影像擷取序列...");

        // Step 1: 執行 AcquisitionStop 命令（停止相機擷取）
        try
        {
            if (_acquisitionStop?.IsWriteable() == true)
            {
                _acquisitionStop.Execute();

                // 等待命令完成
                try
                {
                    _acquisitionStop.WaitUntilDone();
                    _logger.LogDebug("✓ AcquisitionStop 命令執行成功");
                }
                catch (Exception waitEx)
                {
                    _logger.LogDebug(waitEx, "AcquisitionStop.WaitUntilDone() 失敗（非致命）");
                }
            }
            else if (_acquisitionStop is null)
            {
                _logger.LogDebug("AcquisitionStop 命令節點為 null");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "執行 AcquisitionStop 失敗");
        }

        // Step 2: 短暫延遲確保相機停止
        Thread.Sleep(50);

        // Step 3: 停止資料流（必須在 AcquisitionStop 之後）
        try
        {
            _dataStream?.StopAcquisition();
            _logger.LogDebug("✓ DataStream.StopAcquisition 成功");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DataStream.StopAcquisition 失敗");
        }

        // Step 4: 解鎖傳輸層參數
        try
        {
            if (_tlParamsLocked?.IsWriteable() == true)
            {
                _tlParamsLocked.SetValue(0);
                _logger.LogDebug("✓ TLParamsLocked = 0");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "重設 TLParamsLocked 失敗");
        }

        _logger.LogDebug("✓ 影像擷取已停止");
    }

    private void AcquisitionLoop(CancellationToken cancellationToken)
    {
        _logger.LogInformation("▶ 影像擷取迴圈開始");
        var consecutiveErrors = 0;
        var successfulFrames = 0;
        const int maxConsecutiveErrors = 10;

        while (!cancellationToken.IsCancellationRequested)
        {
            peak.core.Buffer? buffer = null;
            try
            {
                _logger.LogTrace("準備調用 WaitForFinishedBuffer...");

                // 嘗試直接捕獲 WaitForFinishedBuffer 拋出的異常
                try
                {
                    buffer = _dataStream!.WaitForFinishedBuffer(new peak.core.Timeout(5000));
                    _logger.LogTrace("WaitForFinishedBuffer 返回，buffer is null: {IsNull}", buffer is null);
                }
                catch (Exception waitEx)
                {
                    // 立即記錄任何異常
                    _logger.LogError(waitEx, "WaitForFinishedBuffer 拋出異常 - Type: {Type}, Message: {Message}",
                        waitEx.GetType().FullName, waitEx.Message);
                    throw; // 重新拋出讓外層處理
                }

                if (buffer is null)
                {
                    _logger.LogDebug("WaitForFinishedBuffer 返回 null");
                    continue;
                }

                // 重置錯誤計數器（成功獲取緩衝區）
                consecutiveErrors = 0;

                if (!buffer.HasImage())
                {
                    _logger.LogDebug("緩衝區中沒有影像");
                    continue;
                }

                // 檢查緩衝區是否完整（關鍵！參考 CameraSDK_CSharp）
                if (buffer.IsIncomplete())
                {
                    _logger.LogWarning("⚠ 緩衝區標記為不完整（IsIncomplete=true），跳過此幀");
                    continue;
                }

                // 提取影像並觸發事件
                _logger.LogTrace("開始提取影像...");
                var image = ExtractImage(buffer);
                _logger.LogTrace("影像提取完成，觸發 FrameReceived 事件");
                FrameReceived?.Invoke(this, image);

                successfulFrames++;
                if (successfulFrames == 1 || successfulFrames % 30 == 0)
                {
                    _logger.LogInformation("✓ 已成功擷取 {Count} 幀影像", successfulFrames);
                }
            }
            catch (Exception ex) when (ex.Message.Contains("PEAK_RETURN_CODE_TIMEOUT") || ex.Message.Contains("GC_ERR_TIMEOUT"))
            {
                // Timeout 是正常的（等待新幀）
                _logger.LogTrace("等待幀超時 (正常)");
                continue;
            }
            catch (Exception ex) when (ex.Message.Contains("PEAK_RETURN_CODE_ABORTED") || ex.Message.Contains("GC_ERR_ABORT") || ex.Message.Contains("abort"))
            {
                // 相機連接中斷或 KillWait 被調用
                _logger.LogWarning("影像擷取被中止: {Message}", ex.Message);
                break;
            }
            catch (System.ApplicationException appEx)
            {
                consecutiveErrors++;

                // 記錄詳細的錯誤資訊
                var fullMessage = appEx.Message;
                var errorCode = fullMessage.Contains("Error-Code:")
                    ? fullMessage.Substring(fullMessage.IndexOf("Error-Code:"))
                    : "No Error-Code in message";

                _logger.LogError(appEx, "❌ ApplicationException (連續: {Count}/{Max})\n  完整訊息: {FullMessage}\n  錯誤碼: {ErrorCode}\n  StackTrace: {StackTrace}",
                    consecutiveErrors, maxConsecutiveErrors, fullMessage, errorCode, appEx.StackTrace);

                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    _logger.LogError("連續錯誤次數過多，停止擷取迴圈");
                    break;
                }

                // 短暫延遲
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                _logger.LogError(ex, "❌ 處理影像幀時發生例外 (連續: {Count}/{Max})\n  Type: {Type}\n  Message: {Message}\n  StackTrace: {StackTrace}",
                    consecutiveErrors, maxConsecutiveErrors, ex.GetType().FullName, ex.Message, ex.StackTrace);

                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    _logger.LogError("連續錯誤次數過多，停止擷取迴圈");
                    break;
                }

                // 短暫延遲
                Thread.Sleep(100);
            }
            finally
            {
                if (buffer is not null)
                {
                    try
                    {
                        _dataStream!.QueueBuffer(buffer);
                        _logger.LogTrace("緩衝區已重新加入佇列");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "QueueBuffer 失敗: {Message}", ex.Message);
                    }
                }
            }
        }

        _logger.LogInformation("■ 影像擷取迴圈結束 (總共擷取 {Count} 幀)", successfulFrames);
    }

    private async Task<ImageData> WaitForSingleImageAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("    ──── WaitForSingleImageAsync 開始 ────");
        await Task.Yield();

        var attemptCount = 0;
        var maxAttempts = 30; // 最多等待 30 秒 (每次 1 秒 timeout)

        while (attemptCount < maxAttempts)
        {
            attemptCount++;
            cancellationToken.ThrowIfCancellationRequested();

            peak.core.Buffer? buffer = null;
            try
            {
                _logger.LogDebug("    等待緩衝區 (嘗試 {Attempt}/{Max})...", attemptCount, maxAttempts);
                buffer = _dataStream!.WaitForFinishedBuffer(new peak.core.Timeout(1000));
                _logger.LogDebug("    取得緩衝區成功");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("    WaitForFinishedBuffer 失敗 (嘗試 {Attempt}): {Message}", attemptCount, ex.Message);
                continue;
            }

            if (buffer is null)
            {
                _logger.LogWarning("    緩衝區為 null (嘗試 {Attempt})", attemptCount);
                continue;
            }

            try
            {
                _logger.LogDebug("    檢查緩衝區是否有影像...");
                if (buffer.HasImage())
                {
                    _logger.LogInformation("    ✓ 緩衝區有影像，正在提取...");
                    var image = ExtractImage(buffer);
                    _logger.LogInformation("    ──── WaitForSingleImageAsync 成功 ({Width}x{Height}) ────", image.Width, image.Height);
                    return image;
                }
                else
                {
                    _logger.LogWarning("    緩衝區沒有影像");
                }
            }
            finally
            {
                _dataStream!.QueueBuffer(buffer);
            }
        }

        _logger.LogError("    ──── WaitForSingleImageAsync 超時 (嘗試 {Attempts} 次) ────", attemptCount);
        throw new TimeoutException($"等待影像超時 (嘗試 {attemptCount} 次)");
    }

    private ImageData ExtractImage(peak.core.Buffer buffer)
    {
        try
        {
            // 獲取影像尺寸
            var width = buffer.Width();
            var height = buffer.Height();

            if (width == 0 || height == 0)
            {
                throw new InvalidOperationException($"無效的影像尺寸: {width}x{height}");
            }

            // 獲取資料大小
            var deliveredSize = buffer.DeliveredDataSize();
            var bufferSize = buffer.Size();
            var dataSize = Math.Max(deliveredSize, bufferSize);

            if (dataSize == 0)
            {
                throw new InvalidOperationException($"緩衝區資料大小為 0 (Delivered: {deliveredSize}, BufferSize: {bufferSize})");
            }

            // 獲取基礎指標
            var basePtr = buffer.BasePtr();
            if (basePtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("緩衝區基礎指標為 NULL");
            }

            // 計算預期的資料大小
            var bytesPerPixel = GetBytesPerPixel(_pixelFormat);
            var expectedStride = (int)(width * bytesPerPixel);
            var expectedSize = expectedStride * height;

            // 使用 DeliveredDataSize 作為實際資料大小（不是 Buffer Size）
            // Buffer Size 可能比實際資料大（預分配的緩衝區）
            var actualDataSize = deliveredSize > 0 ? deliveredSize : dataSize;
            var copySize = (int)Math.Min(actualDataSize, expectedSize);

            _logger.LogTrace("ExtractImage: {Width}x{Height}, Delivered={Delivered}, Expected={Expected}, Copy={Copy}",
                width, height, deliveredSize, expectedSize, copySize);

            // 複製影像資料（只複製實際需要的大小）
            var data = new byte[copySize];
            Marshal.Copy(basePtr, data, 0, copySize);

            _logger.LogTrace("✓ 成功提取影像: {Width}x{Height}, {Size} bytes, Format: {Format}",
                width, height, copySize, _pixelFormat);

            // 不傳遞 stride，讓顯示端根據 width 計算
            return new ImageData(data, (int)width, (int)height, _pixelFormat);
        }
        catch (System.ApplicationException appEx)
        {
            _logger.LogError(appEx, "提取影像時發生 ApplicationException - 這通常表示 SDK 內部錯誤");
            throw new InvalidOperationException($"IDS Peak SDK 內部錯誤: {appEx.Message}", appEx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "提取影像失敗");
            throw;
        }
    }

    private static int GetBytesPerPixel(string pixelFormat)
    {
        // 參考 CameraViewerForm.cs:70-75
        return pixelFormat switch
        {
            "Mono8" => 1,
            "Mono10" => 2,
            "Mono12" => 2,
            "Mono16" => 2,
            "RGB8" => 3,
            "BGR8" => 3,
            "RGBa8" => 4,
            "BGRa8" => 4,
            _ => 1 // 預設為 1
        };
    }

    private void AllocateBuffers(peak.core.DataStream dataStream)
    {
        // 獲取 Payload 大小
        var payload = dataStream.PayloadSize();

        // 如果無法從 DataStream 獲取，嘗試從 NodeMap 讀取
        if (payload == 0 && _remoteNodeMap is not null)
        {
            try
            {
                var payloadNode = _remoteNodeMap.TryFindNodeInteger("PayloadSize");
                if (payloadNode is not null && payloadNode.IsReadable())
                {
                    payload = (uint)payloadNode.Value();
                    _logger.LogDebug("從 NodeMap 讀取 PayloadSize: {Size} bytes", payload);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "從 NodeMap 讀取 PayloadSize 失敗");
            }
        }

        // 最後的備用值
        if (payload == 0)
        {
            payload = 4 * 1024 * 1024; // 4MB 預設值
            _logger.LogWarning("無法獲取 PayloadSize，使用預設值: {Size} bytes", payload);
        }

        // 計算需要的緩衝區數量（參考 CameraSDK_CSharp 範例：最小 3 個）
        var minRequired = dataStream.NumBuffersAnnouncedMinRequired();
        var bufferCount = Math.Max(minRequired, 3u);

        _logger.LogInformation("分配緩衝區: Count={Count} (最小需求: {Min}), PayloadSize={Size} bytes",
            bufferCount, minRequired, payload);

        // 分配並宣布緩衝區
        // 重要：使用 IntPtr.Zero 讓 SDK 自行分配記憶體（參考 CameraSDK_CSharp 範例）
        for (var i = 0; i < bufferCount; i++)
        {
            try
            {
                // 使用 IntPtr.Zero 讓 SDK 內部管理緩衝區記憶體
                var buffer = dataStream.AllocAndAnnounceBuffer(payload, IntPtr.Zero);
                _buffers.Add(new BufferWrapper(buffer, IntPtr.Zero));
                dataStream.QueueBuffer(buffer);

                _logger.LogDebug("✓ 緩衝區 #{Index} 分配成功", i);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "✗ 分配緩衝區 #{Index} 失敗", i);
                throw;
            }
        }

        _logger.LogInformation("✓ 所有緩衝區分配完成: {Count} 個緩衝區已加入佇列", bufferCount);
    }

    private (peak.core.DataStreamDescriptor descriptor, peak.core.DataStream stream) OpenDefaultDataStream(peak.core.Device device)
    {
        var streams = device.DataStreams().ToArray();
        if (streams.Length == 0)
        {
            throw new InvalidOperationException("IDS 相機不支援擷取資料流。");
        }

        foreach (var descriptor in streams)
        {
            peak.core.DataStream? stream = null;
            try
            {
                stream = descriptor.OpenDataStream();
                if (stream is not null)
                {
                    return (descriptor, stream);
                }
            }
            catch
            {
                stream?.Dispose();
                descriptor.Dispose();
                continue;
            }

            stream?.Dispose();
            descriptor.Dispose();
        }

        throw new InvalidOperationException("無法開啟任何 IDS DataStream。");
    }

    private void LoadDefaultUserSet()
    {
        if (_remoteNodeMap is null)
        {
            _logger.LogWarning("NodeMap 為 null，無法載入使用者設定");
            return;
        }

        try
        {
            // 參考 simple_live_windows_forms\BackEnd.cs:182-191
            var userSetSelector = _remoteNodeMap.TryFindNodeEnumeration("UserSetSelector");
            var userSetLoad = _remoteNodeMap.TryFindNodeCommand("UserSetLoad");

            if (userSetSelector is not null && userSetSelector.IsWriteable() && userSetSelector.HasEntry("Default"))
            {
                userSetSelector.SetCurrentEntry("Default");
                _logger.LogDebug("✓ UserSetSelector = Default");

                if (userSetLoad is not null && userSetLoad.IsWriteable())
                {
                    userSetLoad.Execute();
                    userSetLoad.WaitUntilDone();
                    _logger.LogInformation("✓ 已載入預設使用者設定 (UserSetLoad)");
                }
                else
                {
                    _logger.LogDebug("UserSetLoad 命令不可用");
                }
            }
            else
            {
                _logger.LogDebug("UserSetSelector 不可用或無 Default 選項");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "載入預設使用者設定失敗（相機可能不支援）");
        }
    }

    /// <summary>
    /// 載入 Linescan UserSet - 這是使用 Line Scan 模式的關鍵步驟
    /// 參考 IDS 官方範例 linescan_software_trigger/backend.cpp
    /// </summary>
    public bool LoadLineScanUserSet() => LoadUserSet("Linescan");

    /// <summary>
    /// 載入指定的 UserSet
    /// </summary>
    /// <param name="userSetName">UserSet 名稱 (UserSet0, UserSet1, Linescan, 或 Default)</param>
    public bool LoadUserSet(string userSetName)
    {
        if (_remoteNodeMap is null)
        {
            _logger.LogWarning("NodeMap 為 null，無法載入 UserSet '{UserSet}'", userSetName);
            return false;
        }

        try
        {
            var userSetSelector = _remoteNodeMap.TryFindNodeEnumeration("UserSetSelector");
            var userSetLoad = _remoteNodeMap.TryFindNodeCommand("UserSetLoad");

            if (userSetSelector is null || !userSetSelector.IsWriteable())
            {
                _logger.LogWarning("UserSetSelector 節點不可用或不可寫入");
                return false;
            }

            // 檢查是否有指定的 UserSet 選項
            if (!userSetSelector.HasEntry(userSetName))
            {
                _logger.LogWarning("⚠ 此相機不支援 '{UserSet}' UserSet", userSetName);

                // 列出可用的 UserSet 選項
                var entries = userSetSelector.Entries().ToArray();
                var availableEntries = string.Join(", ", entries.Select(e => e.SymbolicValue()));
                _logger.LogInformation("  可用的 UserSet 選項: {Entries}", availableEntries);

                return false;
            }

            // 載入指定的 UserSet
            _logger.LogInformation("══════ 載入 UserSet '{UserSet}' ══════", userSetName);
            userSetSelector.SetCurrentEntry(userSetName);
            _logger.LogInformation("  ✓ UserSetSelector = {UserSet}", userSetName);

            if (userSetLoad is not null && userSetLoad.IsWriteable())
            {
                userSetLoad.Execute();
                userSetLoad.WaitUntilDone();
                _logger.LogInformation("  ✓ UserSetLoad 執行完成 - 已載入 '{UserSet}'", userSetName);
            }
            else
            {
                _logger.LogWarning("  ⚠ UserSetLoad 命令不可用");
                return false;
            }

            // 驗證載入後的設定
            LogLineScanModeStatus();

            _logger.LogInformation("══════ UserSet '{UserSet}' 載入完成 ══════", userSetName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入 UserSet '{UserSet}' 失敗", userSetName);
            return false;
        }
    }

    /// <summary>
    /// 記錄 Line Scan 模式的狀態資訊
    /// </summary>
    private void LogLineScanModeStatus()
    {
        if (_remoteNodeMap is null) return;

        try
        {
            // 讀取 ROI 範圍
            var offsetYNode = _remoteNodeMap.TryFindNodeInteger("OffsetY");
            var heightNode = _remoteNodeMap.TryFindNodeInteger("Height");

            if (offsetYNode is not null)
            {
                var min = offsetYNode.Minimum();
                var max = offsetYNode.Maximum();
                var current = offsetYNode.Value();
                _logger.LogInformation("  OffsetY 範圍: Min={Min}, Max={Max}, Current={Current}", min, max, current);
            }

            if (heightNode is not null)
            {
                var min = heightNode.Minimum();
                var max = heightNode.Maximum();
                var current = heightNode.Value();
                _logger.LogInformation("  Height 範圍: Min={Min}, Max={Max}, Current={Current}", min, max, current);
            }

            // 讀取 AcquisitionLineRate
            var lineRateNode = _remoteNodeMap.TryFindNodeFloat("AcquisitionLineRate");
            if (lineRateNode is not null && lineRateNode.IsReadable())
            {
                var min = lineRateNode.Minimum();
                var max = lineRateNode.Maximum();
                var current = lineRateNode.Value();
                _logger.LogInformation("  AcquisitionLineRate 範圍: Min={Min:F2} Hz, Max={Max:F2} Hz, Current={Current:F2} Hz", min, max, current);

                // 計算行週期並提醒曝光時間限制
                var linePeriodUs = 1000000.0 / max; // 最大行頻對應的最小行週期
                _logger.LogInformation("  ⚠ Line Scan 模式下曝光時間上限約 {MaxExposure:F0} µs (1/LineRateMax)", linePeriodUs);
            }

            // 讀取曝光時間範圍（Line Scan 模式下可能會改變）
            var exposureNode = _remoteNodeMap.TryFindNodeFloat("ExposureTime");
            if (exposureNode is not null && exposureNode.IsReadable())
            {
                var min = exposureNode.Minimum();
                var max = exposureNode.Maximum();
                var current = exposureNode.Value();
                _logger.LogInformation("  ExposureTime 範圍: Min={Min:F2} µs, Max={Max:F2} µs, Current={Current:F2} µs", min, max, current);
            }

            // ===== 診斷用：記錄可能影響影像亮度的參數 =====
            _logger.LogInformation("  ───── 影像亮度相關參數 ─────");

            // Gamma
            var gammaNode = _remoteNodeMap.TryFindNodeFloat("Gamma");
            if (gammaNode is not null && gammaNode.IsReadable())
            {
                _logger.LogInformation("  Gamma = {Value:F2}", gammaNode.Value());
            }
            else
            {
                _logger.LogInformation("  Gamma: 不可用");
            }

            // BlackLevel
            var blackLevelNode = _remoteNodeMap.TryFindNodeFloat("BlackLevel");
            if (blackLevelNode is not null && blackLevelNode.IsReadable())
            {
                _logger.LogInformation("  BlackLevel = {Value:F2}", blackLevelNode.Value());
            }
            else
            {
                _logger.LogInformation("  BlackLevel: 不可用");
            }

            // DigitalGain (如果有)
            var digitalGainNode = _remoteNodeMap.TryFindNodeFloat("DigitalGain");
            if (digitalGainNode is not null && digitalGainNode.IsReadable())
            {
                _logger.LogInformation("  DigitalGain = {Value:F2}", digitalGainNode.Value());
            }

            // Gain
            var gainNode = _remoteNodeMap.TryFindNodeFloat("Gain");
            if (gainNode is not null && gainNode.IsReadable())
            {
                _logger.LogInformation("  Gain = {Value:F2}", gainNode.Value());
            }

            // PixelFormat
            var pixelFormatNode = _remoteNodeMap.TryFindNodeEnumeration("PixelFormat");
            if (pixelFormatNode is not null && pixelFormatNode.IsReadable())
            {
                var entry = pixelFormatNode.CurrentEntry();
                _logger.LogInformation("  PixelFormat = {Value}", entry?.SymbolicValue() ?? "Unknown");
            }

            // GainAuto
            var gainAutoNode = _remoteNodeMap.TryFindNodeEnumeration("GainAuto");
            if (gainAutoNode is not null && gainAutoNode.IsReadable())
            {
                var entry = gainAutoNode.CurrentEntry();
                _logger.LogInformation("  GainAuto = {Value}", entry?.SymbolicValue() ?? "Unknown");
            }

            // ExposureAuto
            var exposureAutoNode = _remoteNodeMap.TryFindNodeEnumeration("ExposureAuto");
            if (exposureAutoNode is not null && exposureAutoNode.IsReadable())
            {
                var entry = exposureAutoNode.CurrentEntry();
                _logger.LogInformation("  ExposureAuto = {Value}", entry?.SymbolicValue() ?? "Unknown");
            }

            // BalanceWhiteAuto (如果有)
            var balanceWhiteAutoNode = _remoteNodeMap.TryFindNodeEnumeration("BalanceWhiteAuto");
            if (balanceWhiteAutoNode is not null && balanceWhiteAutoNode.IsReadable())
            {
                var entry = balanceWhiteAutoNode.CurrentEntry();
                _logger.LogInformation("  BalanceWhiteAuto = {Value}", entry?.SymbolicValue() ?? "Unknown");
            }

            // TriggerSelector 和 TriggerMode (診斷觸發設定)
            _logger.LogInformation("  ───── Trigger 設定 ─────");
            var triggerSelectorNode = _remoteNodeMap.TryFindNodeEnumeration("TriggerSelector");
            var triggerModeNode = _remoteNodeMap.TryFindNodeEnumeration("TriggerMode");
            if (triggerSelectorNode is not null && triggerSelectorNode.IsReadable())
            {
                var selector = triggerSelectorNode.CurrentEntry()?.SymbolicValue() ?? "Unknown";
                var mode = triggerModeNode?.CurrentEntry()?.SymbolicValue() ?? "Unknown";
                _logger.LogInformation("  TriggerSelector = {Selector}, TriggerMode = {Mode}", selector, mode);
            }

            _logger.LogInformation("  ─────────────────────────");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "讀取 Line Scan 模式狀態失敗");
        }
    }

    private void LogDeviceInfo()
    {
        if (_remoteNodeMap is null)
        {
            _logger.LogWarning("NodeMap 為 null，無法查詢設備資訊");
            return;
        }

        try
        {
            var deviceModelName = _remoteNodeMap.TryFindNodeString("DeviceModelName")?.Value() ?? "Unknown";
            var deviceVendor = _remoteNodeMap.TryFindNodeString("DeviceVendorName")?.Value() ?? "Unknown";
            var deviceVersion = _remoteNodeMap.TryFindNodeString("DeviceVersion")?.Value() ?? "Unknown";
            var deviceSerialNumber = _remoteNodeMap.TryFindNodeString("DeviceSerialNumber")?.Value() ?? "Unknown";
            var sensorName = _remoteNodeMap.TryFindNodeString("SensorName")?.Value() ?? "Unknown";

            var widthNode = _remoteNodeMap.TryFindNodeInteger("Width");
            var heightNode = _remoteNodeMap.TryFindNodeInteger("Height");
            var widthMax = widthNode?.Maximum() ?? 0;
            var heightMax = heightNode?.Maximum() ?? 0;

            _logger.LogInformation("────────────── 設備資訊 ──────────────");
            _logger.LogInformation("  型號: {Model}", deviceModelName);
            _logger.LogInformation("  製造商: {Vendor}", deviceVendor);
            _logger.LogInformation("  版本: {Version}", deviceVersion);
            _logger.LogInformation("  序列號: {Serial}", deviceSerialNumber);
            _logger.LogInformation("  感測器: {Sensor}", sensorName);
            _logger.LogInformation("  最大解析度: {Width}x{Height}", widthMax, heightMax);
            _logger.LogInformation("──────────────────────────────────────");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "查詢設備資訊時發生錯誤");
        }
    }

    private string ResolvePixelFormat()
    {
        if (_remoteNodeMap is null)
        {
            return "Unknown";
        }

        try
        {
            var pixelFormatNode = _remoteNodeMap.TryFindNodeEnumeration("PixelFormat");
            var entry = pixelFormatNode?.CurrentEntry();
            if (entry is not null)
            {
                var value = entry.SymbolicValue();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _logger.LogDebug("當前像素格式: {Format}", value);
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "讀取 PixelFormat 失敗");
        }

        _logger.LogWarning("無法讀取像素格式，使用預設值: Mono8");
        return "Mono8";
    }

    private DeviceDescriptor? FindDeviceDescriptor(string deviceId)
    {
        var manager = DeviceManager.Instance();
        manager.Update(DeviceManager.UpdatePolicy.ScanEnvironmentForProducerLibraries);

        DeviceDescriptor? fallback = null;
        foreach (var descriptor in manager.Devices().ToArray())
        {
            try
            {
                if (string.Equals(descriptor.ID(), deviceId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(descriptor.SerialNumber(), deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return descriptor;
                }

                if (fallback is null)
                {
                    fallback = descriptor;
                }
                else
                {
                    descriptor.Dispose();
                }
            }
            catch
            {
                descriptor.Dispose();
            }
        }

        return fallback;
    }

    private void EnsureReady()
    {
        if (_device is null || _dataStream is null)
        {
            throw new InvalidOperationException("尚未開啟任何 IDS 相機。");
        }
    }

    private bool TryPausePreviewForReconfiguration()
    {
        if (!_isPreviewing)
        {
            return false;
        }

        _acquisitionCts?.Cancel();
        try
        {
            _dataStream?.KillWait();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "KillWait 失敗。");
        }

        try
        {
            // 使用超時等待，避免無限阻塞
            const int timeoutMs = 5000;
            if (_acquisitionTask != null && !_acquisitionTask.Wait(timeoutMs))
            {
                _logger.LogWarning("等待擷取任務停止超時 ({TimeoutMs}ms)，強制繼續", timeoutMs);
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // 取消操作，正常情況
        }
        catch (OperationCanceledException)
        {
            // 取消操作，正常情況
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "等待擷取迴圈停止時發生例外");
        }
        finally
        {
            _acquisitionTask = null;
            _acquisitionCts?.Dispose();
            _acquisitionCts = null;
        }

        EndAcquisition();
        _isPreviewing = false;
        return true;
    }

    private void ResumePreviewAfterReconfiguration()
    {
        BeginAcquisition();
        _acquisitionCts = new CancellationTokenSource();
        _acquisitionTask = Task.Run(() => AcquisitionLoop(_acquisitionCts.Token), CancellationToken.None);
        _isPreviewing = true;
    }

    private async Task SafeReconfigureAsync(Action configure, bool requiresBufferRecreate, CancellationToken cancellationToken, string description)
    {
        if (_remoteNodeMap is null)
        {
            throw new InvalidOperationException("Camera has not been opened.");
        }

        var resumePreview = TryPausePreviewForReconfiguration();
        if (!resumePreview)
        {
            EndAcquisition();
        }

        if (requiresBufferRecreate)
        {
            RecreateBuffers();
        }
        else
        {
            FlushDataStream();
        }

        try
        {
            configure();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "套用 {Description} 設定時發生例外。", description);
            throw;
        }
        finally
        {
            if (resumePreview)
            {
                var waitTask = WaitForNextFrameAsync(cancellationToken);
                ResumePreviewAfterReconfiguration();
                try
                {
                    await waitTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "等待重新啟動後首幀失敗。");
                }
            }
            else
            {
                FlushDataStream();
            }
        }
    }

    private async Task WaitForNextFrameAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, ImageData data)
        {
            FrameReceived -= Handler;
            tcs.TrySetResult(true);
        }

        FrameReceived += Handler;

        using var registration = cancellationToken.Register(() =>
        {
            FrameReceived -= Handler;
            tcs.TrySetCanceled(cancellationToken);
        });

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            FrameReceived -= Handler;
        }
    }

    private void RecreateBuffers()
    {
        if (_dataStream is null)
        {
            return;
        }

        FlushDataStream();

        foreach (var wrapper in _buffers)
        {
            try
            {
                _dataStream.RevokeBuffer(wrapper.Buffer);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RevokeBuffer 失敗 (重新配置)。");
            }

            wrapper.Dispose();
        }

        _buffers.Clear();
        AllocateBuffers(_dataStream);
    }

    private static double ClampToIncrement(FloatNode node, double value)
    {
        var min = node.Minimum();
        var max = node.Maximum();
        var aligned = value;

        try
        {
            if (node.HasConstantIncrement())
            {
                var inc = Math.Max(node.Increment(), 0.0001);
                aligned = min + Math.Round((value - min) / inc) * inc;
            }
        }
        catch
        {
            // 某些節點不支援 Increment，忽略即可。
        }

        return Math.Clamp(aligned, min, max);
    }

    private static long ClampToIncrement(IntegerNode node, long value)
    {
        var min = node.Minimum();
        var max = node.Maximum();
        var inc = Math.Max(node.Increment(), 1);
        var aligned = min + ((value - min) / inc) * inc;
        return Math.Clamp(aligned, min, max);
    }

    private string ResolveSdkDirectory()
    {
        var path = _options.SdkPath ?? "libs/cameras/IDS_PEAK";
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private sealed class BufferWrapper : IDisposable
    {
        public BufferWrapper(peak.core.Buffer buffer, IntPtr memory)
        {
            Buffer = buffer;
            Memory = memory;
        }

        public peak.core.Buffer Buffer { get; }

        public IntPtr Memory { get; }

        public void Dispose()
        {
            try
            {
                Buffer.Dispose();
            }
            catch
            {
                // ignored
            }

            if (Memory != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Memory);
            }
        }
    }
}
