using System.Net.Http.Headers;
using System.Text.Json;
using AIVision.Application.Configuration;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace AIVision.Infrastructure.AiService;

/// <summary>
/// AINAVI Workflow AI 推論 Port 實作
/// 使用 /workflow/run API 進行多步驟推論
///
/// 判定規則（根據原廠說明）：
/// 1. 只讀取指定的 Segmentation Block（如 tf-04-segmentation, surface-04-segmentation）
/// 2. 使用 area > 0 判斷是否有缺陷
/// 3. 從 class 欄位取得瑕疵類別名稱
/// 4. 從 contours 取得輪廓座標
/// </summary>
public sealed class WorkflowAiInferencePort : Application.Ports.Devices.IAiInferencePort
{
    private readonly HttpClient _httpClient;
    private readonly WorkflowOptions _options;
    private readonly ILogger<WorkflowAiInferencePort> _logger;
    private bool _isConnected;

    /// <summary>
    /// 要讀取的目標 Segmentation Block 名稱（Phase 1: 硬編碼，未來可配置化）
    /// </summary>
    private static readonly HashSet<string> TargetSegmentationBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "tf-04-segmentation",
        "surface-04-segmentation"
    };

    public WorkflowAiInferencePort(
        HttpClient httpClient,
        IOptions<WorkflowOptions> options,
        ILogger<WorkflowAiInferencePort> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public bool IsConnected => _isConnected;

    // 動態端點（如果設定了則覆蓋 Options）
    private string? _dynamicHost;
    private int? _dynamicPort;

    /// <summary>
    /// 設定連線狀態
    /// </summary>
    public void SetConnectionStatus(bool isConnected)
    {
        _isConnected = isConnected;
        _logger.LogDebug("[WorkflowInference] 連線狀態: {Status}", isConnected ? "已連線" : "未連線");
    }

    /// <summary>
    /// 動態設定 Workflow 推論端點
    /// </summary>
    /// <param name="host">EdgeHub 主機</param>
    /// <param name="port">Workflow 推論埠號</param>
    public void SetEndpoint(string host, int port)
    {
        _dynamicHost = host;
        _dynamicPort = port;
        _logger.LogInformation("[WorkflowInference] 動態設定端點: {Host}:{Port}", host, port);
    }

    /// <summary>
    /// 取得目前的 Workflow 推論 URL
    /// </summary>
    private string GetCurrentWorkflowRunUrl()
    {
        if (_dynamicHost != null && _dynamicPort != null)
        {
            return $"http://{_dynamicHost}:{_dynamicPort}/workflow/run";
        }
        return _options.GetWorkflowRunUrl();
    }

    /// <summary>
    /// 執行 Workflow 推論
    /// POST /workflow/run (multipart/form-data)
    /// </summary>
    public async Task<Prediction> PredictAsync(ImageData image, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;

        try
        {
            var url = GetCurrentWorkflowRunUrl();

            _logger.LogInformation("[WorkflowInference] ========== 開始推論 ==========");
            _logger.LogInformation("[WorkflowInference] URL: {Url}", url);
            _logger.LogInformation("[WorkflowInference] 圖片格式: {Format}, 大小: {Width}x{Height}, {Size} bytes",
                image.PixelFormat, image.Width, image.Height, image.Bytes.Length);

            // 取得 JPEG 格式的圖片 bytes
            var jpegBytes = GetJpegBytes(image);
            _logger.LogInformation("[WorkflowInference] JPEG 編碼後大小: {Size} bytes", jpegBytes.Length);

            // 建立 multipart form
            using var form = new MultipartFormDataContent();
            using var stream = new MemoryStream(jpegBytes);
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");

            var fileName = $"frame_{DateTime.UtcNow.Ticks}.jpg";
            form.Add(fileContent, "img", fileName);

            _logger.LogDebug("[WorkflowInference] 發送 POST 請求...");

            var response = await _httpClient.PostAsync(url, form, cancellationToken);
            var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);

            var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
            _logger.LogInformation("[WorkflowInference] 回應狀態碼: {StatusCode}, 耗時: {Elapsed:F0}ms",
                response.StatusCode, elapsed);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[WorkflowInference] ❌ 推論失敗: HTTP {StatusCode}\n{Response}",
                    (int)response.StatusCode, rawJson.Length > 1000 ? rawJson[..1000] + "..." : rawJson);
                throw new InvalidOperationException($"Workflow 推論失敗: HTTP {(int)response.StatusCode}");
            }

            // 記錄原始 JSON (截斷)
            _logger.LogDebug("[WorkflowInference] 回應 JSON (前2000字元):\n{Json}",
                rawJson.Length > 2000 ? rawJson[..2000] + "..." : rawJson);

            // 解析 Workflow 回應
            var result = ParseWorkflowResponse(rawJson);

            _logger.LogInformation("[WorkflowInference] ========== 推論完成 ==========");
            _logger.LogInformation("[WorkflowInference] 結果: IsOk={IsOk}, DefectCount={DefectCount}, Class={Class}",
                result.IsOk, result.WorkflowDefects.Count, result.PredClass);

            _isConnected = true;

            return new Prediction(
                result.PredClass,
                (float)result.Confidence,
                result.IsOk,
                "workflow-1.0",
                null,
                result.Detections)
            {
                WorkflowDefects = result.WorkflowDefects
            };
        }
        catch (HttpRequestException ex)
        {
            _isConnected = false;
            _logger.LogError(ex, "[WorkflowInference] ❌ HTTP 請求失敗 - 無法連接: {Url}", GetCurrentWorkflowRunUrl());
            throw new InvalidOperationException($"無法連接到 Workflow 服務: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "[WorkflowInference] ❌ 請求超時");
            throw new InvalidOperationException("Workflow 推論請求超時", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WorkflowInference] ❌ 推論過程發生例外");
            throw;
        }
    }

    /// <summary>
    /// 健康檢查
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var host = _dynamicHost ?? _options.EdgeHubHost;
            var port = _dynamicPort ?? _options.WorkflowPort;
            var url = $"http://{host}:{port}/";
            _logger.LogDebug("[WorkflowInference] 健康檢查: {Url}", url);

            var response = await _httpClient.GetAsync(url, cts.Token);

            _isConnected = true;
            _logger.LogInformation("[WorkflowInference] 健康檢查成功: {StatusCode}", response.StatusCode);
            return true;
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _logger.LogWarning(ex, "[WorkflowInference] 健康檢查失敗");
            return false;
        }
    }

    /// <summary>
    /// 解析結果類別
    /// </summary>
    private sealed class ParseResult
    {
        public string PredClass { get; init; } = "OK";
        public double Confidence { get; init; } = 1.0;
        public bool IsOk { get; init; } = true;
        public List<Detection> Detections { get; init; } = new();
        public List<WorkflowDefect> WorkflowDefects { get; init; } = new();
    }

    /// <summary>
    /// 解析 Workflow API 回應
    ///
    /// API 回應格式（新格式 - 實際測試結果）：
    /// {
    ///   "filename.jpg": {
    ///     "tf-04-segmentation": [ [ { class, area, contours, ... } ] ],
    ///     "surface-04-segmentation": [ [ { class, area, contours, ... } ] ]
    ///   }
    /// }
    ///
    /// 判定規則：
    /// 1. 只讀取 TargetSegmentationBlocks 中指定的 Block
    /// 2. area > 0 表示有缺陷
    /// 3. class 欄位取得瑕疵類別
    /// 4. contours 取得輪廓座標
    /// </summary>
    private ParseResult ParseWorkflowResponse(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        var detections = new List<Detection>();
        var workflowDefects = new List<WorkflowDefect>();

        _logger.LogInformation("[WorkflowInference] ========== 開始解析 Workflow 回應 ==========");

        // 新格式：root 的 key 是檔名（如 "01.jpg"），value 是各 model 的結果
        // 遍歷每個圖片的結果
        foreach (var imageProp in root.EnumerateObject())
        {
            var imageName = imageProp.Name;
            var modelResults = imageProp.Value;

            // 跳過非 Object 的屬性（可能是 status 等欄位）
            if (modelResults.ValueKind != JsonValueKind.Object)
            {
                _logger.LogDebug("[WorkflowInference] 跳過非 Object 屬性: {Name}", imageName);
                continue;
            }

            _logger.LogInformation("[WorkflowInference] 解析圖片: {ImageName}", imageName);

            // 遍歷每個 model 的結果
            foreach (var modelProp in modelResults.EnumerateObject())
            {
                var blockName = modelProp.Name;
                var stepResults = modelProp.Value;

                // 只處理目標 Segmentation Block
                if (!TargetSegmentationBlocks.Contains(blockName))
                {
                    _logger.LogDebug("[WorkflowInference] 跳過非目標 Block: {BlockName}", blockName);
                    continue;
                }

                _logger.LogInformation("[WorkflowInference] 發現目標 Block: {BlockName}", blockName);

                if (stepResults.ValueKind != JsonValueKind.Array)
                    continue;

                // API 回應是雙層 Array: [ [ {item1}, {item2} ] ]
                foreach (var imageResults in stepResults.EnumerateArray())
                {
                    if (imageResults.ValueKind == JsonValueKind.Array)
                    {
                        var itemCount = 0;
                        foreach (var item in imageResults.EnumerateArray())
                        {
                            itemCount++;
                            var defect = ParseSegmentationItem(item, blockName);
                            if (defect != null && defect.HasDefect)
                            {
                                workflowDefects.Add(defect);

                                // 同時建立 Detection 供其他地方使用（使用 contours 計算 bounding box）
                                var bbox = CalculateBoundingBox(defect.Contours);
                                if (bbox.HasValue)
                                {
                                    detections.Add(new Detection(
                                        defect.ClassName,
                                        bbox.Value,
                                        1.0f)); // Segmentation 沒有 confidence，預設 1.0
                                }
                            }
                        }
                        _logger.LogInformation("[WorkflowInference] 解析 {BlockName} - 項目數: {Count}",
                            blockName, itemCount);
                    }
                }
            }
        }

        // 判定結果 - 最大面積優先
        var validDefects = workflowDefects.Where(d => d.HasDefect).ToList();
        bool isOk = validDefects.Count == 0;

        // 按面積排序（降冪），取最大面積的瑕疵作為主要判定
        // 若面積相同，保持原始順序（先偵測到的優先）
        var primaryDefect = validDefects
            .OrderByDescending(d => d.TotalArea)
            .FirstOrDefault();

        string predClass = isOk ? "OK" : (primaryDefect?.ClassName ?? "NG");
        int primaryArea = primaryDefect?.TotalArea ?? 0;

        _logger.LogInformation("[WorkflowInference] ========== 解析完成 ==========");
        _logger.LogInformation("[WorkflowInference] 判定結果: IsOk={IsOk}, DefectCount={DefectCount}, PrimaryClass={Class}, PrimaryArea={Area}",
            isOk, validDefects.Count, predClass, primaryArea);

        // 輸出每個缺陷的詳細資訊（按面積排序）
        foreach (var defect in validDefects.OrderByDescending(d => d.TotalArea))
        {
            var isPrimary = defect == primaryDefect ? " [主要]" : "";
            _logger.LogInformation("[WorkflowInference]   瑕疵{IsPrimary}: Block={Block}, Class={Class}, Area={Area}, Contours={ContourCount}個輪廓",
                isPrimary, defect.SourceBlock, defect.ClassName, defect.TotalArea, defect.Contours.Count);
        }

        return new ParseResult
        {
            PredClass = predClass,
            Confidence = 1.0, // Segmentation 沒有 confidence
            IsOk = isOk,
            Detections = detections,
            WorkflowDefects = validDefects
        };
    }

    /// <summary>
    /// 解析單一 Segmentation 項目
    ///
    /// 欄位說明：
    /// - class: 瑕疵類別名稱（如 "gouge", "TF_crash"）
    /// - area: 瑕疵面積陣列，有值且 > 0 表示有缺陷
    /// - contours: 輪廓座標陣列
    /// - workflow_image_id: 圖片 ID
    /// - image_name: 原始圖片名稱
    /// </summary>
    private WorkflowDefect? ParseSegmentationItem(JsonElement item, string blockName)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        // 1. 讀取 class（注意：是 "class" 不是 "class_name"）
        var className = item.TryGetProperty("class", out var classEl)
            ? classEl.GetString() ?? "unknown"
            : "unknown";

        // 2. 讀取 area 陣列
        var areas = new List<int>();
        if (item.TryGetProperty("area", out var areaEl) && areaEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in areaEl.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.Number)
                {
                    areas.Add(a.GetInt32());
                }
            }
        }

        // 3. 沒有 area 或全為 0，記錄但仍回傳（讓呼叫端決定是否使用）
        if (areas.Count == 0 || areas.All(a => a == 0))
        {
            _logger.LogDebug("[WorkflowInference]   {Block}/{Class}: area 為空或全為 0，視為無缺陷",
                blockName, className);
        }
        else
        {
            _logger.LogDebug("[WorkflowInference]   {Block}/{Class}: area=[{Areas}]",
                blockName, className, string.Join(", ", areas));
        }

        // 4. 讀取 contours 輪廓座標
        var contours = new List<IReadOnlyList<ContourPoint>>();
        if (item.TryGetProperty("contours", out var contoursEl) && contoursEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var contour in contoursEl.EnumerateArray())
            {
                if (contour.ValueKind != JsonValueKind.Array) continue;

                var points = new List<ContourPoint>();
                foreach (var pt in contour.EnumerateArray())
                {
                    if (pt.TryGetProperty("x", out var xEl) && pt.TryGetProperty("y", out var yEl))
                    {
                        points.Add(new ContourPoint(xEl.GetInt32(), yEl.GetInt32()));
                    }
                }
                if (points.Count > 0)
                {
                    contours.Add(points);
                }
            }
        }

        // 5. 讀取其他資訊
        var imageId = item.TryGetProperty("workflow_image_id", out var idEl) ? idEl.GetString() : null;
        var imageName = item.TryGetProperty("image_name", out var nameEl) ? nameEl.GetString() : null;

        return new WorkflowDefect
        {
            SourceBlock = blockName,
            ClassName = className,
            Areas = areas,
            Contours = contours,
            WorkflowImageId = imageId,
            ImageName = imageName
        };
    }

    /// <summary>
    /// 從輪廓座標計算 Bounding Box
    /// </summary>
    private static System.Drawing.RectangleF? CalculateBoundingBox(IReadOnlyList<IReadOnlyList<ContourPoint>> contours)
    {
        if (contours.Count == 0)
            return null;

        var allPoints = contours.SelectMany(c => c).ToList();
        if (allPoints.Count == 0)
            return null;

        var minX = allPoints.Min(p => p.X);
        var minY = allPoints.Min(p => p.Y);
        var maxX = allPoints.Max(p => p.X);
        var maxY = allPoints.Max(p => p.Y);

        return new System.Drawing.RectangleF(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// 取得 JPEG 格式的圖片 bytes
    /// 如果已經是 JPEG 格式則直接返回，否則進行轉換
    /// </summary>
    private byte[] GetJpegBytes(ImageData image)
    {
        // 檢查是否已經是 JPEG 格式
        // JPEG 檔案開頭為 FF D8 FF
        if (image.Bytes.Length >= 3 &&
            image.Bytes[0] == 0xFF &&
            image.Bytes[1] == 0xD8 &&
            image.Bytes[2] == 0xFF)
        {
            _logger.LogDebug("[WorkflowInference] 圖片已是 JPEG 格式，直接使用");
            return image.Bytes;
        }

        // 也檢查 PixelFormat 是否為 Jpeg
        if (image.PixelFormat?.Equals("Jpeg", StringComparison.OrdinalIgnoreCase) == true ||
            image.PixelFormat?.Equals("jpeg", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogDebug("[WorkflowInference] PixelFormat 為 Jpeg，直接使用");
            return image.Bytes;
        }

        // 需要將 RAW 像素數據轉換為 JPEG
        _logger.LogInformation("[WorkflowInference] 將 RAW 圖片 ({Format}) 轉換為 JPEG...", image.PixelFormat);

        try
        {
            // 根據 PixelFormat 建立 Image
            using var ms = new MemoryStream();

            if (image.PixelFormat == "Mono8" || image.PixelFormat == "Gray8")
            {
                // 8-bit 灰階
                using var img = Image.LoadPixelData<L8>(image.Bytes, image.Width, image.Height);
                img.SaveAsJpeg(ms, new JpegEncoder { Quality = 90 });
            }
            else if (image.PixelFormat == "Mono16" || image.PixelFormat == "Gray16")
            {
                // 16-bit 灰階，轉換為 8-bit
                var bytes8 = new byte[image.Bytes.Length / 2];
                for (int i = 0; i < bytes8.Length; i++)
                {
                    // 取高位元組（假設 Little Endian）
                    bytes8[i] = image.Bytes[i * 2 + 1];
                }
                using var img = Image.LoadPixelData<L8>(bytes8, image.Width, image.Height);
                img.SaveAsJpeg(ms, new JpegEncoder { Quality = 90 });
            }
            else if (image.PixelFormat == "RGB8" || image.PixelFormat == "Rgb24")
            {
                // RGB 24-bit
                using var img = Image.LoadPixelData<Rgb24>(image.Bytes, image.Width, image.Height);
                img.SaveAsJpeg(ms, new JpegEncoder { Quality = 90 });
            }
            else if (image.PixelFormat == "BGR8" || image.PixelFormat == "Bgr24")
            {
                // BGR 24-bit，需要轉換為 RGB
                using var img = Image.LoadPixelData<Bgr24>(image.Bytes, image.Width, image.Height);
                img.SaveAsJpeg(ms, new JpegEncoder { Quality = 90 });
            }
            else
            {
                // 預設嘗試以灰階處理
                _logger.LogWarning("[WorkflowInference] 未知的 PixelFormat: {Format}，嘗試以灰階處理", image.PixelFormat);
                using var img = Image.LoadPixelData<L8>(image.Bytes, image.Width, image.Height);
                img.SaveAsJpeg(ms, new JpegEncoder { Quality = 90 });
            }

            var jpegBytes = ms.ToArray();
            _logger.LogInformation("[WorkflowInference] JPEG 轉換完成: {OriginalSize} bytes → {JpegSize} bytes",
                image.Bytes.Length, jpegBytes.Length);

            return jpegBytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WorkflowInference] JPEG 轉換失敗，嘗試直接使用原始數據");
            return image.Bytes;
        }
    }
}
