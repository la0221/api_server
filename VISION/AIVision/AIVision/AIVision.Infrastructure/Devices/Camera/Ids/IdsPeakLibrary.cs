using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using peak;

namespace AIVision.Infrastructure.Devices.Camera.Ids;

internal static class IdsPeakLibrary
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static ILogger? _logger;
    private static string? _sdkPath;

    public static void EnsureInitialized(string sdkPath, ILogger logger)
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            var absolutePath = ResolveAbsolutePath(sdkPath);
            _logger = logger;

            // 驗證 SDK 路徑和關鍵 DLL
            ValidateSdkPath(absolutePath);

            // 預先載入原生介面 DLL（參考 CameraSDK_CSharp 範例）
            PreloadNativeLibraries(absolutePath);

            // 添加到搜尋路徑
            AddToSearchPath(absolutePath);

            // 初始化 IDS Peak 運行時
            try
            {
                peak.Library.Initialize();
                _sdkPath = absolutePath;
                _initialized = true;
                _logger.LogInformation("✓ IDS peak runtime initialized successfully. SDK path: {SdkPath}", absolutePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "✗ Failed to initialize IDS peak runtime. SDK path: {SdkPath}", absolutePath);
                throw new InvalidOperationException($"IDS Peak SDK 初始化失敗: {ex.Message}", ex);
            }
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (!_initialized)
            {
                return;
            }

            try
            {
                peak.Library.Close();
                _logger?.LogInformation("IDS peak runtime closed.");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to close IDS peak runtime.");
            }
            finally
            {
                _initialized = false;
                _logger = null;
                _sdkPath = null;
            }
        }
    }

    private static string ResolveAbsolutePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var basePath = AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(basePath, path));
    }

    private static void ValidateSdkPath(string path)
    {
        if (!Directory.Exists(path))
        {
            var errorMsg = $"IDS Peak SDK 路徑不存在: {path}";
            _logger?.LogError(errorMsg);
            throw new DirectoryNotFoundException(errorMsg);
        }

        // 檢查必要的 DLL 文件
        var requiredDlls = new[]
        {
            "ids_peak.dll",
            "ids_peak_dotnet.dll",
            "ids_peak_dotnet_interface.dll"
        };

        var missingDlls = requiredDlls.Where(dll => !File.Exists(Path.Combine(path, dll))).ToList();

        if (missingDlls.Any())
        {
            var errorMsg = $"缺少必要的 IDS Peak DLL 文件: {string.Join(", ", missingDlls)}";
            _logger?.LogError(errorMsg);
            throw new FileNotFoundException(errorMsg);
        }

        _logger?.LogDebug("✓ SDK 路徑驗證通過: {Path}", path);
    }

    private static void PreloadNativeLibraries(string sdkPath)
    {
        // 參考 CameraSDK_CSharp/Program.cs 的做法
        // 預先載入原生介面 DLL，避免延遲綁定失敗
        var nativeInterfacePath = Path.Combine(sdkPath, "ids_peak_dotnet_interface.dll");

        if (File.Exists(nativeInterfacePath))
        {
            try
            {
                NativeLibrary.Load(nativeInterfacePath);
                _logger?.LogDebug("✓ 預先載入原生介面庫: {Path}", nativeInterfacePath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "✗ 載入原生介面庫失敗: {Path}", nativeInterfacePath);
                // 不拋出異常，讓後續初始化繼續，可能仍可成功
            }
        }
        else
        {
            _logger?.LogWarning("⚠ 找不到原生介面庫: {Path}", nativeInterfacePath);
        }

        // 也嘗試預先載入 IPL 介面（如果存在）
        var iplInterfacePath = Path.Combine(sdkPath, "ids_peak_ipl_dotnet_interface.dll");
        if (File.Exists(iplInterfacePath))
        {
            try
            {
                NativeLibrary.Load(iplInterfacePath);
                _logger?.LogDebug("✓ 預先載入 IPL 介面庫: {Path}", iplInterfacePath);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "載入 IPL 介面庫失敗（可選）: {Path}", iplInterfacePath);
            }
        }
    }

    private static void AddToSearchPath(string path)
    {
        // 設置 PATH 環境變數
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathSegments = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        if (!pathSegments.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
        {
            var newPath = $"{path}{Path.PathSeparator}{currentPath}";
            Environment.SetEnvironmentVariable("PATH", newPath);
            _logger?.LogDebug("✓ 已添加到 PATH: {Path}", path);
        }

        // 設置 DLL 搜尋目錄（Win32 API）
        if (!SetDllDirectory(path))
        {
            var error = Marshal.GetLastWin32Error();
            _logger?.LogWarning("SetDllDirectory 失敗 (ErrorCode={ErrorCode})", error);
        }
        else
        {
            _logger?.LogDebug("✓ 已設置 DLL 搜尋目錄: {Path}", path);
        }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string? lpPathName);
}
