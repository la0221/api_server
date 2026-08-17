using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Domain.Shared;
using AIVision.Infrastructure.Configs;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.Adapters.ImageBatch;

/// <summary>
/// AI 分割模型 Overlay 繪製器
/// 用於將分割遮罩（mask）疊圖到原圖上，支援配置式顏色和透明度
/// </summary>
public sealed class SegOverlayRenderer : IOverlayRendererPort
{
    private readonly AiSettings _cfg;

    public SegOverlayRenderer(IOptions<AiSettings> cfg)
    {
        _cfg = cfg.Value;
    }

    /// <summary>
    /// 此方法用於舊版相容性，AI 推論層應直接調用 DrawAiAsync
    /// </summary>
    public Task<ImageData> DrawAsync(ImageData source, IReadOnlyList<Detection> detections, CancellationToken cancellationToken) =>
        Task.FromResult(source);

    /// <summary>
    /// 將 AI 分割結果的 mask 疊圖到原圖（繪製輪廓線）
    /// </summary>
    public BitmapSource DrawAiMasks(BitmapSource original, AiResult result)
    {
        // 若無 mask，返回原圖
        if (result?.SegmentationMasks is null || result.SegmentationMasks.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine("DrawAiMasks: 沒有 mask 資料");
            return original;
        }

        System.Diagnostics.Debug.WriteLine($"DrawAiMasks: 開始處理 {result.SegmentationMasks.Count} 個 mask");

        try
        {
            // 確保原圖格式正確
            var formattedOriginal = new FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0);
            
            int width = formattedOriginal.PixelWidth;
            int height = formattedOriginal.PixelHeight;
            int stride = width * 4; // BGRA32 = 4 bytes per pixel
            
            System.Diagnostics.Debug.WriteLine($"DrawAiMasks: 圖片大小 {width}x{height}, stride={stride}");
            
            // 複製原圖像素
            byte[] pixels = new byte[stride * height];
            formattedOriginal.CopyPixels(pixels, stride, 0);

            // 疊加每個 mask
            int maskIndex = 0;
            foreach (var mask in result.SegmentationMasks)
            {
                maskIndex++;
                try
                {
                    System.Diagnostics.Debug.WriteLine($"DrawAiMasks: 處理 mask {maskIndex}/{result.SegmentationMasks.Count}, 類別={mask.ClassName}");
                    
                    // 1. 解碼 PNG mask
                    using var ms = new MemoryStream(mask.PngBytes);
                    var maskBmp = new BitmapImage();
                    maskBmp.BeginInit();
                    maskBmp.CacheOption = BitmapCacheOption.OnLoad;
                    maskBmp.StreamSource = ms;
                    maskBmp.EndInit();

                    System.Diagnostics.Debug.WriteLine($"DrawAiMasks: Mask 大小 {maskBmp.PixelWidth}x{maskBmp.PixelHeight}");

                    // 2. 以最近鄰縮放到與原圖相同大小
                    var maskPixels = ResizeMaskGray(maskBmp, width, height);
                    BinarizeMask(maskPixels);

                    // 3. 檢查 mask 是否有有效像素，若沒有則嘗試改用 Alpha 通道
                    int validPixels = CountMaskPixels(maskPixels);
                    if (validPixels == 0)
                    {
                        System.Diagnostics.Debug.WriteLine("DrawAiMasks: Gray mask 無有效像素，嘗試使用 Alpha 通道");
                        maskPixels = ResizeMaskAlpha(maskBmp, width, height);
                        if (maskPixels.Length == 0)
                        {
                            System.Diagnostics.Debug.WriteLine("DrawAiMasks: Alpha mask 不存在或無有效像素，略過");
                            continue;
                        }

                        BinarizeMask(maskPixels);
                        validPixels = CountMaskPixels(maskPixels);
                    }

                    System.Diagnostics.Debug.WriteLine($"DrawAiMasks: Mask 有效像素數量 = {validPixels}");

                    // 4. 取得顏色
                    var color = ResolveColor(mask.ClassName);
                    byte b = color.B;
                    byte g = color.G;
                    byte r = color.R;
                    System.Diagnostics.Debug.WriteLine($"DrawAiMasks: 輪廓顏色 RGB=({r},{g},{b})");

                    // 5. 提取輪廓並繪製
                    int contourCount = DrawContourOnPixels(pixels, maskPixels, width, height, stride, r, g, b);
                    System.Diagnostics.Debug.WriteLine($"DrawAiMasks: 繪製了 {contourCount} 個輪廓點");
                }
                catch (Exception ex)
                {
                    // 單個 mask 繪製失敗時，繼續處理其他 mask
                    System.Diagnostics.Debug.WriteLine($"DrawAiMasks: Mask {maskIndex} 處理失敗: {ex.Message}");
                    continue;
                }
            }

            // 6. 建立新的 BitmapSource
            var outputBitmap = BitmapSource.Create(
                width, height,
                96, 96,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            
            outputBitmap.Freeze();
            System.Diagnostics.Debug.WriteLine("DrawAiMasks: 成功建立輸出圖片");
            return outputBitmap;
        }
        catch (Exception ex)
        {
            // 若繪製失敗，返回原圖
            System.Diagnostics.Debug.WriteLine($"DrawAiMasks: 整體處理失敗: {ex.Message}\n{ex.StackTrace}");
            return original;
        }
    }

    /// <summary>
    /// 在像素陣列上繪製輪廓線，返回繪製的輪廓點數量
    /// 與 Python 版本一致：掃描範圍為 y=1 到 height-1, x=1 到 width-1
    /// </summary>
    private int DrawContourOnPixels(byte[] pixels, byte[] maskPixels, int width, int height, int stride, byte r, byte g, byte b)
    {
        int contourCount = 0;
        const byte threshold = 128;
        
        // 找出邊緣像素並繪製輪廓線
        // 與 Python 版本一致：從 1 開始到 height-1，避免邊界檢查時越界
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int maskIdx = y * width + x;
                byte centerValue = maskPixels[maskIdx];

                // 只處理 mask 中的有效像素（與 Python 版本一致：> threshold）
                if (centerValue > threshold)
                {
                    // 檢查是否為邊緣（4鄰域檢測，與 Python 版本一致）
                    bool isEdge = (
                        maskPixels[maskIdx - width] < threshold ||  // 上
                        maskPixels[maskIdx + width] < threshold ||  // 下
                        maskPixels[maskIdx - 1] < threshold ||      // 左
                        maskPixels[maskIdx + 1] < threshold         // 右
                    );

                    // 如果是邊緣，繪製輪廓線（較粗，3像素寬）
                    if (isEdge)
                    {
                        // 3x3 筆刷讓輪廓更粗
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                SetPixel(pixels, x + dx, y + dy, stride, width, height, r, g, b);
                            }
                        }
                        
                        contourCount++;
                    }
                }
            }
        }
        
        return contourCount;
    }

    /// <summary>
    /// 設置單個像素的顏色並確保完全不透明
    /// </summary>
    private void SetPixel(byte[] pixels, int x, int y, int stride, int width, int height, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
            return;
            
        int idx = y * stride + x * 4;
        
        if (idx < 0 || idx + 3 >= pixels.Length)
            return;

        pixels[idx] = b;       // B
        pixels[idx + 1] = g;   // G
        pixels[idx + 2] = r;   // R
        pixels[idx + 3] = 255; // A
    }

    private byte[] ResizeMaskGray(BitmapSource maskBmp, int targetWidth, int targetHeight)
    {
        var maskFormatted = new FormatConvertedBitmap(maskBmp, PixelFormats.Gray8, null, 0);
        int srcWidth = maskFormatted.PixelWidth;
        int srcHeight = maskFormatted.PixelHeight;
        byte[] src = new byte[srcWidth * srcHeight];
        maskFormatted.CopyPixels(src, srcWidth, 0);
        return NearestResizeGray8(src, srcWidth, srcHeight, targetWidth, targetHeight);
    }

    private byte[] ResizeMaskAlpha(BitmapSource maskBmp, int targetWidth, int targetHeight)
    {
        var maskFormatted = new FormatConvertedBitmap(maskBmp, PixelFormats.Bgra32, null, 0);
        int srcWidth = maskFormatted.PixelWidth;
        int srcHeight = maskFormatted.PixelHeight;
        int srcStride = srcWidth * 4;
        byte[] src = new byte[srcStride * srcHeight];
        maskFormatted.CopyPixels(src, srcStride, 0);

        byte[] alpha = new byte[srcWidth * srcHeight];
        bool hasMeaningfulAlpha = false;

        for (int y = 0; y < srcHeight; y++)
        {
            int srcRow = y * srcStride;
            int dstRow = y * srcWidth;
            for (int x = 0; x < srcWidth; x++)
            {
                byte a = src[srcRow + x * 4 + 3];
                alpha[dstRow + x] = a;
                if (!hasMeaningfulAlpha && a != 255)
                {
                    hasMeaningfulAlpha = true;
                }
            }
        }

        return hasMeaningfulAlpha
            ? NearestResizeGray8(alpha, srcWidth, srcHeight, targetWidth, targetHeight)
            : Array.Empty<byte>();
    }

    private byte[] NearestResizeGray8(byte[] src, int srcWidth, int srcHeight, int targetWidth, int targetHeight)
    {
        if (src.Length == 0 || targetWidth <= 0 || targetHeight <= 0)
            return Array.Empty<byte>();

        byte[] dst = new byte[targetWidth * targetHeight];
        double sx = (double)srcWidth / targetWidth;
        double sy = (double)srcHeight / targetHeight;

        for (int y = 0; y < targetHeight; y++)
        {
            int syi = Math.Min((int)(y * sy), srcHeight - 1);
            int srcRow = syi * srcWidth;
            int dstRow = y * targetWidth;

            for (int x = 0; x < targetWidth; x++)
            {
                int sxi = Math.Min((int)(x * sx), srcWidth - 1);
                dst[dstRow + x] = src[srcRow + sxi];
            }
        }

        return dst;
    }

    private void BinarizeMask(byte[] maskPixels, byte threshold = 128)
    {
        if (maskPixels is null || maskPixels.Length == 0)
            return;

        for (int i = 0; i < maskPixels.Length; i++)
        {
            maskPixels[i] = maskPixels[i] > threshold ? (byte)255 : (byte)0;
        }
    }

    private int CountMaskPixels(byte[] maskPixels, byte threshold = 128)
    {
        if (maskPixels is null || maskPixels.Length == 0)
            return 0;

        int validPixels = 0;
        for (int i = 0; i < maskPixels.Length; i++)
        {
            if (maskPixels[i] > threshold)
                validPixels++;
        }

        return validPixels;
    }

    private System.Windows.Media.Color ResolveColor(string className)
    {
        // 從配置查詢顏色
        if (_cfg.Overlay.ClassColors.TryGetValue(className, out var hex))
        {
            try
            {
                if (System.Windows.Media.ColorConverter.ConvertFromString(hex) is System.Windows.Media.Color c)
                    return c;
            }
            catch (FormatException)
            {
                // 顏色格式無效，使用 fallback
                System.Diagnostics.Debug.WriteLine($"無效的顏色格式: {hex} (class: {className})");
            }
        }

        // Fallback：查詢 "other"
        if (_cfg.Overlay.ClassColors.TryGetValue("other", out var hex2))
        {
            try
            {
                if (System.Windows.Media.ColorConverter.ConvertFromString(hex2) is System.Windows.Media.Color c2)
                    return c2;
            }
            catch (FormatException)
            {
                // 顏色格式無效，使用最終 fallback
                System.Diagnostics.Debug.WriteLine($"無效的 fallback 顏色格式: {hex2}");
            }
        }

        // 最後 fallback：紅色
        return System.Windows.Media.Colors.Red;
    }
}

