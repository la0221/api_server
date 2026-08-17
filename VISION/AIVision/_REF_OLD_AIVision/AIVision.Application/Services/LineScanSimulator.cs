using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Models;
using AIVision.Application.Ports.Services;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace AIVision.Application.Services;

/// <summary>
/// Line Scan 模擬器實作
/// </summary>
public sealed class LineScanSimulator : ILineScanSimulator, IDisposable
{
    private readonly ILogger<LineScanSimulator>? _logger;
    private readonly LineScanImageBuilder _imageBuilder;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private readonly object _lock = new();

    private byte[]? _sourceImageData;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _sourceBytesPerPixel = 1; // Mono8

    private LineScanSimulatorSettings? _settings;
    private CancellationTokenSource? _scanCts;
    private SimulatorState _state = SimulatorState.Idle;
    private int _currentLine;
    private int _completedImageCount;
    private bool _disposed;

    public LineScanSimulator(ILogger<LineScanSimulator>? logger = null)
    {
        _logger = logger;
        _imageBuilder = new LineScanImageBuilder();
    }

    public SimulatorState State
    {
        get { lock (_lock) { return _state; } }
        private set { lock (_lock) { _state = value; } }
    }

    public int CurrentLine
    {
        get { lock (_lock) { return _currentLine; } }
    }

    public int TotalLines => _settings?.ScanHeight ?? 0;

    public int CompletedImageCount
    {
        get { lock (_lock) { return _completedImageCount; } }
    }

    public (int Width, int Height)? SourceImageSize
    {
        get
        {
            lock (_lock)
            {
                if (_sourceImageData is null)
                    return null;
                return (_sourceWidth, _sourceHeight);
            }
        }
    }

    public event EventHandler<LineScanLineEventArgs>? LineReceived;
    public event EventHandler<LineScanImageEventArgs>? ImageCompleted;
    public event EventHandler<LineScanErrorEventArgs>? SimulatorError;

    public void SetSourceImage(ImageData imageData)
    {
        if (imageData.Bytes is null || imageData.Bytes.Length == 0)
            throw new ArgumentException("圖片數據為空", nameof(imageData));

        if (imageData.Width <= 0 || imageData.Height <= 0)
            throw new ArgumentException("圖片尺寸無效", nameof(imageData));

        lock (_lock)
        {
            _sourceWidth = imageData.Width;
            _sourceHeight = imageData.Height;
            _sourceBytesPerPixel = 1; // 假設為 Mono8
            _sourceImageData = imageData.Bytes.ToArray(); // 複製一份
        }

        _logger?.LogInformation("✓ 來源圖片已設定: {Width}x{Height}", _sourceWidth, _sourceHeight);
    }

    public ImageData? GetSourcePreview()
    {
        lock (_lock)
        {
            if (_sourceImageData is null)
                return null;

            return new ImageData(
                _sourceImageData.ToArray(), // 複製一份
                _sourceWidth,
                _sourceHeight,
                "Mono8");
        }
    }

    public Task StartAsync(LineScanSimulatorSettings settings, CancellationToken ct = default)
    {
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));

        if (!settings.IsValid)
            throw new ArgumentException("模擬器設定無效", nameof(settings));

        lock (_lock)
        {
            if (_sourceImageData is null)
                throw new InvalidOperationException("請先設定來源圖片");

            if (!settings.IsRoiInBounds(_sourceWidth, _sourceHeight))
                throw new ArgumentException(
                    $"ROI 超出圖片範圍。圖片: {_sourceWidth}x{_sourceHeight}, " +
                    $"ROI: ({settings.AnchorX}, {settings.AnchorY}) + {settings.ScanWidth}",
                    nameof(settings));

            if (_state == SimulatorState.Running)
                throw new InvalidOperationException("模擬器已在執行中");
        }

        _logger?.LogInformation(
            "開始模擬 Line Scan: AnchorX={AnchorX}, AnchorY={AnchorY}, Width={Width}, Height={Height}, LineRate={LineRate}Hz",
            settings.AnchorX, settings.AnchorY, settings.ScanWidth, settings.ScanHeight, settings.LineRate);

        _settings = settings;
        _currentLine = 0;
        _imageBuilder.Initialize(settings.ScanWidth, settings.ScanHeight, bytesPerPixel: 1);
        _pauseEvent.Set(); // 確保不是暫停狀態

        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        State = SimulatorState.Running;

        // 啟動背景任務
        _ = Task.Run(() => SimulationLoopAsync(_scanCts.Token), _scanCts.Token);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation("停止模擬...");

        _scanCts?.Cancel();
        _pauseEvent.Set(); // 解除暫停，讓循環可以退出

        lock (_lock)
        {
            _state = SimulatorState.Idle;
        }

        _imageBuilder.Clear();

        _logger?.LogInformation("✓ 模擬已停止，共完成 {Count} 張圖像", _completedImageCount);
        return Task.CompletedTask;
    }

    public void Pause()
    {
        if (State != SimulatorState.Running)
            return;

        _pauseEvent.Reset();
        State = SimulatorState.Paused;
        _logger?.LogInformation("模擬已暫停");
    }

    public void Resume()
    {
        if (State != SimulatorState.Paused)
            return;

        _pauseEvent.Set();
        State = SimulatorState.Running;
        _logger?.LogInformation("模擬已繼續");
    }

    private async Task SimulationLoopAsync(CancellationToken ct)
    {
        if (_settings is null || _sourceImageData is null)
            return;

        var lineInterval = TimeSpan.FromSeconds(1.0 / _settings.LineRate);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            while (_currentLine < _settings.ScanHeight && !ct.IsCancellationRequested)
            {
                // 等待暫停解除
                try
                {
                    _pauseEvent.Wait(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // 從來源圖片讀取一行數據
                var lineData = ExtractLine(_settings.AnchorX, _settings.AnchorY, _settings.ScanWidth);

                // 加入 ImageBuilder
                var isComplete = _imageBuilder.AddLine(lineData);

                lock (_lock)
                {
                    _currentLine++;
                }

                // 觸發事件
                LineReceived?.Invoke(this, new LineScanLineEventArgs
                {
                    LineIndex = _currentLine,
                    TotalLines = _settings.ScanHeight
                });

                // 時序模擬
                if (_settings.EnableTiming && !ct.IsCancellationRequested)
                {
                    var expectedTime = TimeSpan.FromTicks(lineInterval.Ticks * _currentLine);
                    var delay = expectedTime - stopwatch.Elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }

                // 圖像完成
                if (isComplete)
                {
                    var elapsed = stopwatch.Elapsed;
                    var actualLineRate = _settings.ScanHeight / elapsed.TotalSeconds;

                    var completedImage = new ImageData(
                        _imageBuilder.GetCompletedImageData(),
                        _settings.ScanWidth,
                        _settings.ScanHeight,
                        "Mono8");

                    lock (_lock)
                    {
                        _completedImageCount++;
                    }

                    _logger?.LogInformation(
                        "✓ 模擬圖像 #{Index} 完成: {Width}x{Height}, 耗時 {Elapsed:F2}s, 實際行頻 {LineRate:F1}Hz",
                        _completedImageCount,
                        completedImage.Width,
                        completedImage.Height,
                        elapsed.TotalSeconds,
                        actualLineRate);

                    ImageCompleted?.Invoke(this, new LineScanImageEventArgs
                    {
                        Image = completedImage,
                        ImageIndex = _completedImageCount - 1,
                        ElapsedTime = elapsed,
                        ActualLineRate = actualLineRate
                    });

                    // 完成後停止（單次模式）
                    State = SimulatorState.Completed;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "模擬過程發生錯誤");

            State = SimulatorState.Error;

            SimulatorError?.Invoke(this, new LineScanErrorEventArgs
            {
                Exception = ex,
                Message = $"模擬過程發生錯誤: {ex.Message}",
                IsFatal = true
            });
        }
    }

    private byte[] ExtractLine(int anchorX, int anchorY, int width)
    {
        lock (_lock)
        {
            if (_sourceImageData is null)
                throw new InvalidOperationException("來源圖片尚未設定");

            var lineData = new byte[width * _sourceBytesPerPixel];
            var startIndex = (anchorY * _sourceWidth + anchorX) * _sourceBytesPerPixel;

            Array.Copy(_sourceImageData, startIndex, lineData, 0, lineData.Length);
            return lineData;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _pauseEvent.Dispose();
        _imageBuilder.Dispose();

        lock (_lock)
        {
            _sourceImageData = null;
            _disposed = true;
        }
    }
}
