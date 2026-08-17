using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.Logging;

/// <summary>
/// 檔案日誌提供者
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly LogLevel _minimumLevel;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();
    private bool _disposed;

    public FileLoggerProvider(string logDirectory, LogLevel minimumLevel = LogLevel.Debug)
    {
        _logDirectory = logDirectory;
        _minimumLevel = minimumLevel;

        // 確保目錄存在
        Directory.CreateDirectory(_logDirectory);

        // 建立日誌檔案，使用日期時間命名
        var fileName = $"AIVision_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        var filePath = Path.Combine(_logDirectory, fileName);

        _writer = new StreamWriter(filePath, append: true, encoding: Encoding.UTF8)
        {
            AutoFlush = true
        };

        // 寫入啟動標記
        _writer.WriteLine($"=== AIVision Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        _writer.WriteLine();
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this, _minimumLevel));
    }

    internal void WriteLog(string message)
    {
        if (_disposed) return;

        lock (_writeLock)
        {
            try
            {
                _writer.WriteLine(message);
            }
            catch
            {
                // 忽略寫入錯誤
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _writer.WriteLine();
        _writer.WriteLine($"=== AIVision Log Ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        _writer.Dispose();
        _loggers.Clear();
    }
}

/// <summary>
/// 檔案日誌記錄器
/// </summary>
public sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLoggerProvider _provider;
    private readonly LogLevel _minimumLevel;

    public FileLogger(string categoryName, FileLoggerProvider provider, LogLevel minimumLevel)
    {
        _categoryName = categoryName;
        _provider = provider;
        _minimumLevel = minimumLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var levelStr = logLevel switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT ",
            _ => "NONE "
        };

        // 簡化 category name（只取最後一段）
        var shortCategory = _categoryName;
        var lastDot = _categoryName.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < _categoryName.Length - 1)
        {
            shortCategory = _categoryName[(lastDot + 1)..];
        }

        var logLine = $"[{DateTime.Now:HH:mm:ss.fff}] [{levelStr}] [{shortCategory}] {message}";

        if (exception != null)
        {
            logLine += Environment.NewLine + exception.ToString();
        }

        _provider.WriteLog(logLine);
    }
}

/// <summary>
/// FileLogger 擴充方法
/// </summary>
public static class FileLoggerExtensions
{
    /// <summary>
    /// 加入檔案日誌記錄器
    /// </summary>
    public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string logDirectory, LogLevel minimumLevel = LogLevel.Debug)
    {
        builder.AddProvider(new FileLoggerProvider(logDirectory, minimumLevel));
        return builder;
    }
}
