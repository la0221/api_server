namespace AIVision.Application.Services;

using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;

/// <summary>
/// 圖片保存服務實作
/// </summary>
public sealed class InspectionImageService : IInspectionImageService
{
    private readonly string _imageRootPath;
    private readonly ILogger<InspectionImageService> _logger;

    // 工單代碼驗證規則：只允許字母、數字、底線、連字號
    private static readonly Regex ValidWorkOrderCodePattern = new(
        @"^[A-Za-z0-9_\-]+$",
        RegexOptions.Compiled);

    public InspectionImageService(ILogger<InspectionImageService> logger)
    {
        _logger = logger;
        _imageRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIVision",
            "Images");

        // 確保根目錄存在
        Directory.CreateDirectory(_imageRootPath);

        _logger.LogInformation("圖片保存服務初始化完成，根目錄: {RootPath}", _imageRootPath);
    }

    public async Task<(string originalPath, string? annotatedPath)> SaveInspectionImageAsync(
        ImageData originalImage,
        string workOrderCode,
        string result,
        ImageData? annotatedImage,
        CancellationToken cancellationToken)
    {
        // 驗證工單代碼，防止路徑遍歷攻擊
        ValidateWorkOrderCode(workOrderCode);

        _logger.LogDebug("開始保存檢測圖片: WorkOrder={WorkOrderCode}, Result={Result}, ImageSize={Size}bytes",
            workOrderCode, result, originalImage.Bytes.Length);

        try
        {
            // 決定保存到 OK 或 NG 目錄
            // 新結構：Images/{WorkOrder}/{OK|NG}/xxx.jpg（工單在上層，結果在下層）
            var resultFolder = result.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "OK" : "NG";
            var workOrderFolder = Path.Combine(_imageRootPath, workOrderCode, resultFolder);

            // 二次驗證：確保最終路徑在允許的根目錄內
            ValidatePathWithinRoot(workOrderFolder);

            Directory.CreateDirectory(workOrderFolder);

            // 生成時間戳文件名（確保唯一性）
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var originalFileName = $"{timestamp}.jpg";
            var originalPath = Path.Combine(workOrderFolder, originalFileName);

            // 保存原始圖片
            await SaveImageDataAsync(originalImage, originalPath, cancellationToken);
            _logger.LogDebug("原始圖片已保存: {Path}", originalPath);

            // 保存標註圖片（僅 NG 且有標註時）
            string? annotatedPath = null;
            if (annotatedImage.HasValue && !result.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                var annotatedFileName = $"{timestamp}_annotated.jpg";
                annotatedPath = Path.Combine(workOrderFolder, annotatedFileName);
                await SaveImageDataAsync(annotatedImage.Value, annotatedPath, cancellationToken);
                _logger.LogDebug("標註圖片已保存: {Path}", annotatedPath);
            }

            _logger.LogInformation("檢測圖片保存完成: WorkOrder={WorkOrderCode}, Result={Result}, Path={Path}",
                workOrderCode, result, originalPath);

            return (originalPath, annotatedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存檢測圖片失敗: WorkOrder={WorkOrderCode}, Result={Result}",
                workOrderCode, result);
            throw;
        }
    }

    private static async Task SaveImageDataAsync(
        ImageData imageData,
        string path,
        CancellationToken cancellationToken)
    {
        // 將 ImageData 轉換為 BitmapSource
        var bitmapSource = CreateBitmapSource(imageData);

        // 使用 JPEG 編碼器（品質 95%）
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

        // 先編碼到 MemoryStream，再非同步寫入檔案
        using var memoryStream = new MemoryStream();
        encoder.Save(memoryStream);
        var encodedBytes = memoryStream.ToArray();

        await using var fileStream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);

        await fileStream.WriteAsync(encodedBytes, cancellationToken);
    }

    /// <summary>
    /// 將 ImageData 轉換為 BitmapSource
    /// </summary>
    private static BitmapSource CreateBitmapSource(ImageData data)
    {
        // 根據像素格式判斷
        PixelFormat pixelFormat;
        if (data.PixelFormat == "Bgr24")
        {
            pixelFormat = PixelFormats.Bgr24;
        }
        else if (data.PixelFormat == "Mono8" || data.PixelFormat == "Gray8" || data.PixelFormat?.Contains("Bayer") == true)
        {
            pixelFormat = PixelFormats.Gray8;
        }
        else if (data.PixelFormat == "Bgra32")
        {
            pixelFormat = PixelFormats.Bgra32;
        }
        else
        {
            // 未知格式，根據資料大小判斷
            var detectedBytesPerPixel = data.Bytes.Length / (data.Width * data.Height);
            pixelFormat = detectedBytesPerPixel switch
            {
                1 => PixelFormats.Gray8,
                2 => PixelFormats.Gray16,
                3 => PixelFormats.Bgr24,
                _ => PixelFormats.Bgra32
            };
        }

        var bytesPerPixel = (pixelFormat.BitsPerPixel + 7) / 8;
        var sourceStride = data.Stride > 0 ? data.Stride : data.Width * bytesPerPixel;

        // WPF 要求 stride 對齊到 4 的倍數
        var alignedStride = (sourceStride + 3) / 4 * 4;

        byte[] bufferToUse;
        int strideToUse;

        // 驗證緩衝區大小是否足夠
        var requiredSize = sourceStride * data.Height;
        var alignedBufferSize = alignedStride * data.Height;

        // 檢查來源資料是否已經對齊且大小足夠
        if (data.Bytes.Length >= requiredSize && sourceStride == alignedStride)
        {
            // 已對齊且大小足夠，直接使用原始緩衝區
            bufferToUse = data.Bytes;
            strideToUse = sourceStride;
        }
        else
        {
            // 未對齊或大小不足，需要重新分配並複製資料
            bufferToUse = new byte[alignedBufferSize];

            // 逐行複製，每行末尾填充對齊所需的 padding bytes
            for (int y = 0; y < data.Height; y++)
            {
                var srcOffset = y * sourceStride;
                var dstOffset = y * alignedStride;

                // 計算實際可複製的資料量（防止超出來源緩衝區）
                var copyLength = Math.Min(sourceStride, data.Bytes.Length - srcOffset);

                if (copyLength > 0 && srcOffset + copyLength <= data.Bytes.Length)
                {
                    Array.Copy(data.Bytes, srcOffset, bufferToUse, dstOffset, copyLength);
                }

                // padding bytes 自動為 0（new byte[] 初始化）
            }

            strideToUse = alignedStride;
        }

        return BitmapSource.Create(
            data.Width,
            data.Height,
            96,
            96,
            pixelFormat,
            null,
            bufferToUse,
            strideToUse);
    }

    /// <summary>
    /// 為工單預先創建 OK 和 NG 資料夾
    /// </summary>
    /// <param name="workOrderCode">工單代碼</param>
    public void EnsureWorkOrderFoldersExist(string workOrderCode)
    {
        ValidateWorkOrderCode(workOrderCode);

        var workOrderPath = Path.Combine(_imageRootPath, workOrderCode);
        ValidatePathWithinRoot(workOrderPath);

        var okPath = Path.Combine(workOrderPath, "OK");
        var ngPath = Path.Combine(workOrderPath, "NG");

        Directory.CreateDirectory(okPath);
        Directory.CreateDirectory(ngPath);

        _logger.LogInformation("已創建工單圖片資料夾: {WorkOrderPath}", workOrderPath);
    }

    /// <summary>
    /// 驗證工單代碼，防止路徑遍歷攻擊
    /// </summary>
    /// <param name="workOrderCode">工單代碼</param>
    /// <exception cref="ArgumentException">工單代碼無效時拋出</exception>
    private void ValidateWorkOrderCode(string workOrderCode)
    {
        if (string.IsNullOrWhiteSpace(workOrderCode))
        {
            _logger.LogWarning("工單代碼為空或空白");
            throw new ArgumentException("工單代碼不能為空", nameof(workOrderCode));
        }

        if (workOrderCode.Length > 100)
        {
            _logger.LogWarning("工單代碼過長: {Length} 字符", workOrderCode.Length);
            throw new ArgumentException("工單代碼長度不能超過 100 字符", nameof(workOrderCode));
        }

        // 檢查是否包含路徑遍歷字符
        if (workOrderCode.Contains("..") ||
            workOrderCode.Contains('/') ||
            workOrderCode.Contains('\\'))
        {
            _logger.LogWarning("工單代碼包含路徑遍歷字符: {WorkOrderCode}", workOrderCode);
            throw new ArgumentException("工單代碼包含非法字符", nameof(workOrderCode));
        }

        // 使用白名單驗證：只允許字母、數字、底線、連字號
        if (!ValidWorkOrderCodePattern.IsMatch(workOrderCode))
        {
            _logger.LogWarning("工單代碼格式無效: {WorkOrderCode}", workOrderCode);
            throw new ArgumentException("工單代碼只能包含字母、數字、底線和連字號", nameof(workOrderCode));
        }
    }

    /// <summary>
    /// 驗證路徑是否在允許的根目錄內
    /// </summary>
    /// <param name="targetPath">目標路徑</param>
    /// <exception cref="SecurityException">路徑超出允許範圍時拋出</exception>
    private void ValidatePathWithinRoot(string targetPath)
    {
        var fullTargetPath = Path.GetFullPath(targetPath);
        var fullRootPath = Path.GetFullPath(_imageRootPath);

        // 確保目標路徑在根目錄內
        if (!fullTargetPath.StartsWith(fullRootPath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("路徑遍歷攻擊被阻止: 目標路徑 {TargetPath} 不在允許的根目錄 {RootPath} 內",
                fullTargetPath, fullRootPath);
            throw new SecurityException($"路徑遍歷攻擊被阻止: 目標路徑不在允許範圍內");
        }
    }
}
