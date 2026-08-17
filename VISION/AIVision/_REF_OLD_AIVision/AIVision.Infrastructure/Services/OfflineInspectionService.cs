using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.Persistence;
using AIVision.Application.Services;
using AIVision.Domain.Entities;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AIVision.Infrastructure.Services;

/// <summary>
/// 離線檢測服務實作
/// </summary>
public sealed class OfflineInspectionService : IOfflineInspectionService
{
    private readonly IAiInferencePort _aiInferencePort;
    private readonly IInspectionRepository _inspectionRepository;
    private readonly ILogger<OfflineInspectionService> _logger;

    private List<string> _imagePaths = new();
    private int _currentIndex = -1;
    private int _totalInspected = 0;
    private int _okCount = 0;
    private int _ngCount = 0;

    public OfflineInspectionService(
        IAiInferencePort aiInferencePort,
        IInspectionRepository inspectionRepository,
        ILogger<OfflineInspectionService> logger)
    {
        _aiInferencePort = aiInferencePort;
        _inspectionRepository = inspectionRepository;
        _logger = logger;
    }

    public string? CurrentImagePath => _currentIndex >= 0 && _currentIndex < _imagePaths.Count
        ? _imagePaths[_currentIndex]
        : null;

    public int CurrentImageIndex => _currentIndex + 1; // 1-based
    public int TotalImageCount => _imagePaths.Count;
    public bool HasPrevious => _currentIndex > 0;
    public bool HasNext => _currentIndex < _imagePaths.Count - 1;

    public OfflineInspectionStatistics Statistics => new()
    {
        TotalInspected = _totalInspected,
        OkCount = _okCount,
        NgCount = _ngCount
    };

    public Task<int> LoadImagesFromFolderAsync(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
            {
                _logger.LogWarning("資料夾不存在: {FolderPath}", folderPath);
                return Task.FromResult(0);
            }

            // 支援的影像格式
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif" };

            _imagePaths = Directory.GetFiles(folderPath)
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f)
                .ToList();

            _currentIndex = _imagePaths.Count > 0 ? 0 : -1;

            _logger.LogInformation("成功載入 {Count} 張影像，來自資料夾: {FolderPath}",
                _imagePaths.Count, folderPath);

            return Task.FromResult(_imagePaths.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入影像失敗");
            return Task.FromResult(0);
        }
    }

    public void MoveToPrevious()
    {
        if (HasPrevious)
        {
            _currentIndex--;
            _logger.LogDebug("切換到上一張影像: {Index}/{Total}", CurrentImageIndex, TotalImageCount);
        }
    }

    public void MoveToNext()
    {
        if (HasNext)
        {
            _currentIndex++;
            _logger.LogDebug("切換到下一張影像: {Index}/{Total}", CurrentImageIndex, TotalImageCount);
        }
    }

    public async Task<OfflineInspectionResult> InspectCurrentImageAsync(Guid? workOrderId = null, string? modelVersion = null)
    {
        if (CurrentImagePath == null)
        {
            throw new InvalidOperationException("沒有可用的影像");
        }

        try
        {
            _logger.LogInformation("開始檢測影像: {ImagePath} (工單: {WorkOrder})",
                CurrentImagePath, workOrderId?.ToString() ?? "無");

            // 直接讀取原始圖片檔案（不重新編碼，保持原始格式）
            var imageBytes = await File.ReadAllBytesAsync(CurrentImagePath);

            // 載入影像以取得寬高資訊
            using var image = await Image.LoadAsync<Rgb24>(CurrentImagePath);

            var imageData = new ImageData(
                Bytes: imageBytes,
                Width: image.Width,
                Height: image.Height,
                PixelFormat: "Jpeg"
            );

            _logger.LogDebug("載入圖片: Width={Width}, Height={Height}, Size={Size} bytes",
                image.Width, image.Height, imageBytes.Length);

            // 執行 AI 推論
            var prediction = await _aiInferencePort.PredictAsync(imageData, CancellationToken.None);

            // 統計
            _totalInspected++;
            if (prediction.IsOk)
                _okCount++;
            else
                _ngCount++;

            _logger.LogInformation("檢測完成: {Result}, 良率: {Yield:F2}%",
                prediction.IsOk ? "OK" : "NG",
                Statistics.YieldRate);

            // 儲存檢測記錄到資料庫 (如果有工單)
            if (workOrderId.HasValue)
            {
                var inspection = new Inspection(
                    workOrderId: workOrderId.Value,
                    result: prediction.IsOk ? "OK" : "NG",
                    confidence: prediction.Confidence,
                    imagePath: CurrentImagePath,
                    modelVersion: modelVersion ?? "offline-test",
                    occurredAtUtc: DateTime.UtcNow
                );

                await _inspectionRepository.AddAsync(inspection, CancellationToken.None);
                _logger.LogDebug("已儲存檢測記錄到資料庫: {InspectionId}, 模型: {ModelVersion}",
                    inspection.Id, modelVersion ?? "offline-test");
            }

            return new OfflineInspectionResult
            {
                IsPass = prediction.IsOk,
                DefectType = prediction.Label,
                Confidence = prediction.Confidence,
                ImagePath = CurrentImagePath,
                Timestamp = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "檢測影像失敗: {ImagePath}", CurrentImagePath);
            throw;
        }
    }

    public void ResetStatistics()
    {
        _totalInspected = 0;
        _okCount = 0;
        _ngCount = 0;
        _logger.LogInformation("統計數據已重設");
    }

    private ImageData ConvertToImageData(Image<Rgb24> image)
    {
        // 將圖片編碼為 JPEG 格式（AINAVI 服務需要 JPEG 格式）
        var width = image.Width;
        var height = image.Height;

        using var memoryStream = new MemoryStream();
        image.SaveAsJpeg(memoryStream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder
        {
            Quality = 95
        });

        var jpegBytes = memoryStream.ToArray();

        _logger.LogDebug("轉換圖片為 JPEG: Width={Width}, Height={Height}, Size={Size} bytes",
            width, height, jpegBytes.Length);

        return new ImageData(
            Bytes: jpegBytes,
            Width: width,
            Height: height,
            PixelFormat: "Jpeg"
        );
    }
}
