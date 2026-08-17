using System;

namespace AIVision.Application.Services;

/// <summary>
/// Line Scan 圖像建構器 - 負責累積行數據並拼接成完整圖像
/// </summary>
public sealed class LineScanImageBuilder : IDisposable
{
    private byte[]? _buffer;
    private int _width;
    private int _targetHeight;
    private int _bytesPerPixel;
    private int _currentLine;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>當前已累積的行數</summary>
    public int CurrentLine
    {
        get { lock (_lock) { return _currentLine; } }
    }

    /// <summary>目標行數</summary>
    public int TargetHeight
    {
        get { lock (_lock) { return _targetHeight; } }
    }

    /// <summary>每行寬度 (pixel)</summary>
    public int Width
    {
        get { lock (_lock) { return _width; } }
    }

    /// <summary>是否已完成</summary>
    public bool IsComplete
    {
        get { lock (_lock) { return _currentLine >= _targetHeight && _targetHeight > 0; } }
    }

    /// <summary>是否已初始化</summary>
    public bool IsInitialized
    {
        get { lock (_lock) { return _buffer is not null; } }
    }

    /// <summary>
    /// 初始化 Buffer
    /// </summary>
    /// <param name="width">每行寬度 (pixel)</param>
    /// <param name="targetHeight">目標行數</param>
    /// <param name="bytesPerPixel">每像素位元組數 (Mono8=1, RGB8=3)</param>
    public void Initialize(int width, int targetHeight, int bytesPerPixel = 1)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "寬度必須大於 0");
        if (targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetHeight), "目標高度必須大於 0");
        if (bytesPerPixel <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytesPerPixel), "每像素位元組數必須大於 0");

        lock (_lock)
        {
            _width = width;
            _targetHeight = targetHeight;
            _bytesPerPixel = bytesPerPixel;
            _currentLine = 0;

            var bufferSize = (long)width * targetHeight * bytesPerPixel;

            // 檢查是否超過 int.MaxValue (約 2GB)
            if (bufferSize > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"圖像大小 ({bufferSize:N0} bytes) 超過最大限制 ({int.MaxValue:N0} bytes)");
            }

            _buffer = new byte[bufferSize];
        }
    }

    /// <summary>
    /// 添加一行數據
    /// </summary>
    /// <param name="lineData">行數據 (長度應為 Width * BytesPerPixel)</param>
    /// <returns>true 如果圖像已完成</returns>
    public bool AddLine(ReadOnlySpan<byte> lineData)
    {
        lock (_lock)
        {
            if (_buffer is null)
                throw new InvalidOperationException("Buffer 尚未初始化，請先呼叫 Initialize()");

            if (_currentLine >= _targetHeight)
                return true; // 已完成

            var expectedLength = _width * _bytesPerPixel;
            if (lineData.Length != expectedLength)
            {
                throw new ArgumentException(
                    $"行數據長度不符。預期 {expectedLength}，實際 {lineData.Length}",
                    nameof(lineData));
            }

            var destOffset = _currentLine * _width * _bytesPerPixel;
            lineData.CopyTo(_buffer.AsSpan(destOffset));

            _currentLine++;
            return _currentLine >= _targetHeight;
        }
    }

    /// <summary>
    /// 添加一行數據 (byte[] 版本)
    /// </summary>
    public bool AddLine(byte[] lineData)
    {
        return AddLine(lineData.AsSpan());
    }

    /// <summary>
    /// 取得完成的圖像數據 (複製一份)
    /// </summary>
    public byte[] GetCompletedImageData()
    {
        lock (_lock)
        {
            if (_buffer is null)
                throw new InvalidOperationException("Buffer 尚未初始化");

            if (!IsComplete)
                throw new InvalidOperationException(
                    $"圖像尚未完成。目前 {_currentLine}/{_targetHeight} 行");

            return _buffer.ToArray(); // 複製一份
        }
    }

    /// <summary>
    /// 取得目前的部分圖像數據 (用於預覽，複製一份)
    /// </summary>
    public byte[]? GetPartialImageData()
    {
        lock (_lock)
        {
            if (_buffer is null || _currentLine == 0)
                return null;

            var partialSize = _currentLine * _width * _bytesPerPixel;
            var result = new byte[partialSize];
            Array.Copy(_buffer, result, partialSize);
            return result;
        }
    }

    /// <summary>
    /// 重置 Builder，準備下一張圖像
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _currentLine = 0;
            if (_buffer is not null)
            {
                Array.Clear(_buffer);
            }
        }
    }

    /// <summary>
    /// 完全清除並釋放 Buffer
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffer = null;
            _width = 0;
            _targetHeight = 0;
            _bytesPerPixel = 0;
            _currentLine = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            _buffer = null;
            _disposed = true;
        }
    }
}
