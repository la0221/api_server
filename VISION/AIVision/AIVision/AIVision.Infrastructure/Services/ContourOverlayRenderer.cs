using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIVision.Application.Services;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.Services;

/// <summary>
/// 瑕疵輪廓疊加渲染器實作
///
/// 功能：
/// - 在原始圖片上繪製 Workflow 瑕疵輪廓
/// - 使用 Bresenham 畫線演算法繪製多邊形輪廓
/// - 支援多輪廓瑕疵
/// - 根據瑕疵類別配置不同顏色
/// </summary>
public sealed class ContourOverlayRenderer : IContourOverlayRenderer
{
    private readonly ILogger<ContourOverlayRenderer> _logger;
    private readonly OverlayOptions _options;

    public ContourOverlayRenderer(
        ILogger<ContourOverlayRenderer> logger,
        IOptions<OverlayOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// 在原始圖片上繪製瑕疵輪廓
    /// </summary>
    public ImageData DrawDefectContours(ImageData original, IReadOnlyList<WorkflowDefect> defects)
    {
        if (defects == null || defects.Count == 0)
        {
            _logger.LogDebug("[OverlayRenderer] 無瑕疵需要繪製，回傳原始圖片");
            return original;
        }

        try
        {
            _logger.LogInformation("[OverlayRenderer] 開始繪製 {Count} 個瑕疵輪廓", defects.Count);

            // 1. 將 ImageData 轉為 BitmapSource
            var bitmapSource = CreateBitmapSource(original);

            // 2. 轉為可編輯的像素陣列
            var pixels = GetPixels(bitmapSource, out int width, out int height, out int stride);

            // 3. 繪製每個瑕疵的輪廓
            foreach (var defect in defects)
            {
                if (defect.Contours == null || defect.Contours.Count == 0)
                {
                    _logger.LogDebug("[OverlayRenderer] 瑕疵 {Class} 無輪廓資料，跳過", defect.ClassName);
                    continue;
                }

                var color = GetColor(defect.ClassName);
                int contourIndex = 0;

                foreach (var contourPoints in defect.Contours)
                {
                    if (contourPoints.Count < 2)
                    {
                        _logger.LogDebug("[OverlayRenderer] 瑕疵 {Class} 輪廓 {Index} 點數不足 ({Count} 點)，跳過",
                            defect.ClassName, contourIndex, contourPoints.Count);
                        contourIndex++;
                        continue;
                    }

                    // 繪製多邊形輪廓線（3 像素寬）
                    DrawPolyline(pixels, contourPoints, width, height, stride, color, _options.LineWidth);

                    _logger.LogDebug("[OverlayRenderer] 已繪製瑕疵 {Class} 輪廓 {Index} ({PointCount} 點)",
                        defect.ClassName, contourIndex, contourPoints.Count);
                    contourIndex++;
                }
            }

            // 4. 建立新的 ImageData
            var annotatedImage = CreateImageData(pixels, width, height);

            _logger.LogInformation("[OverlayRenderer] 瑕疵輪廓繪製完成");
            return annotatedImage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OverlayRenderer] 繪製瑕疵輪廓失敗，回傳原始圖片");
            return original;
        }
    }

    /// <summary>
    /// 將 ImageData 轉換為 BitmapSource
    /// </summary>
    private BitmapSource CreateBitmapSource(ImageData image)
    {
        PixelFormat pixelFormat = image.PixelFormat switch
        {
            "Mono8" or "Gray8" => PixelFormats.Gray8,
            "RGB8" => PixelFormats.Rgb24,
            "BGR8" => PixelFormats.Bgr24,
            "Mono16" => PixelFormats.Gray16,
            "BGRA32" => PixelFormats.Bgra32,
            _ => PixelFormats.Bgr24
        };

        int bytesPerPixel = (pixelFormat.BitsPerPixel + 7) / 8;
        int stride = image.Width * bytesPerPixel;

        return BitmapSource.Create(
            image.Width,
            image.Height,
            96, 96,
            pixelFormat,
            null,
            image.Bytes,
            stride);
    }

    /// <summary>
    /// 將 BitmapSource 轉換為可編輯的像素陣列（BGRA32 格式）
    /// </summary>
    private byte[] GetPixels(BitmapSource source, out int width, out int height, out int stride)
    {
        width = source.PixelWidth;
        height = source.PixelHeight;

        // 轉換為 BGRA32 格式（4 bytes per pixel）
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        stride = width * 4;

        var pixels = new byte[height * stride];
        converted.CopyPixels(pixels, stride, 0);

        return pixels;
    }

    /// <summary>
    /// 從像素陣列建立 ImageData（Bgr24 格式）
    /// </summary>
    private ImageData CreateImageData(byte[] pixels, int width, int height)
    {
        // BGRA32 (4 bytes/pixel) 轉換為 BGR24 (3 bytes/pixel)
        int srcStride = width * 4;  // BGRA32
        int dstStride = width * 3;  // BGR24

        // 對齊到 4 bytes
        dstStride = ((dstStride + 3) / 4) * 4;

        var bgrPixels = new byte[dstStride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int srcIdx = y * srcStride + x * 4;  // BGRA32
                int dstIdx = y * dstStride + x * 3;  // BGR24

                // 複製 BGR，丟棄 Alpha
                bgrPixels[dstIdx] = pixels[srcIdx];       // B
                bgrPixels[dstIdx + 1] = pixels[srcIdx + 1]; // G
                bgrPixels[dstIdx + 2] = pixels[srcIdx + 2]; // R
            }
        }

        return new ImageData(
            Bytes: bgrPixels,
            Width: width,
            Height: height,
            PixelFormat: "Bgr24",
            Stride: dstStride
        );
    }

    /// <summary>
    /// 繪製多邊形輪廓線（閉合）
    /// </summary>
    private void DrawPolyline(
        byte[] pixels,
        IReadOnlyList<ContourPoint> points,
        int width, int height, int stride,
        System.Drawing.Color color,
        int lineWidth)
    {
        for (int i = 0; i < points.Count; i++)
        {
            var p1 = points[i];
            var p2 = points[(i + 1) % points.Count];  // 閉合多邊形

            // Bresenham 畫線演算法
            DrawThickLine(pixels, p1.X, p1.Y, p2.X, p2.Y, width, height, stride, color, lineWidth);
        }
    }

    /// <summary>
    /// 繪製粗線（使用 Bresenham 演算法）
    /// </summary>
    private void DrawThickLine(
        byte[] pixels,
        int x0, int y0, int x1, int y1,
        int width, int height, int stride,
        System.Drawing.Color color,
        int lineWidth)
    {
        // Bresenham's line algorithm
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            // 繪製粗線（lineWidth x lineWidth 正方形）
            int halfWidth = lineWidth / 2;
            for (int dy2 = -halfWidth; dy2 <= halfWidth; dy2++)
            {
                for (int dx2 = -halfWidth; dx2 <= halfWidth; dx2++)
                {
                    SetPixel(pixels, x0 + dx2, y0 + dy2, stride, width, height, color);
                }
            }

            if (x0 == x1 && y0 == y1) break;

            int e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x0 += sx;
            }
            if (e2 < dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>
    /// 設定像素顏色（BGRA32 格式）
    /// </summary>
    private void SetPixel(
        byte[] pixels,
        int x, int y,
        int stride,
        int width, int height,
        System.Drawing.Color color)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return;

        int idx = y * stride + x * 4;  // BGRA32
        if (idx < 0 || idx + 3 >= pixels.Length) return;

        pixels[idx] = color.B;       // B
        pixels[idx + 1] = color.G;   // G
        pixels[idx + 2] = color.R;   // R
        pixels[idx + 3] = 255;       // A (完全不透明)
    }

    /// <summary>
    /// 根據瑕疵類別取得顏色
    /// </summary>
    private System.Drawing.Color GetColor(string className)
    {
        if (_options.ClassColors.TryGetValue(className, out var hexColor))
        {
            return System.Drawing.ColorTranslator.FromHtml(hexColor);
        }

        // 預設顏色（白色）
        if (_options.ClassColors.TryGetValue("other", out var defaultColor))
        {
            return System.Drawing.ColorTranslator.FromHtml(defaultColor);
        }

        return System.Drawing.Color.White;
    }
}

/// <summary>
/// Overlay 渲染器配置選項（對應 Workflow:Overlay）
/// </summary>
public sealed class OverlayOptions
{
    /// <summary>
    /// 是否啟用 Overlay 功能
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 輪廓線寬度（像素）
    /// </summary>
    public int LineWidth { get; set; } = 3;

    /// <summary>
    /// 瑕疵類別顏色對應表（Hex 顏色代碼）
    /// </summary>
    public Dictionary<string, string> ClassColors { get; set; } = new()
    {
        { "TF_crash", "#FF0000" },          // 紅色
        { "TF_discoloration", "#FFA500" },  // 橙色
        { "TF_dents", "#FFFF00" },          // 黃色
        { "gouge", "#00FF00" },             // 綠色
        { "crash", "#FF00FF" },             // 洋紅色
        { "dents", "#00FFFF" },             // 青色
        { "other", "#FFFFFF" }              // 白色（預設）
    };
}
