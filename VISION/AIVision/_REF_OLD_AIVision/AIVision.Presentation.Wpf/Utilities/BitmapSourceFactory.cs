using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIVision.Domain.Shared;

namespace AIVision.Presentation.Wpf.Utilities;

public static class BitmapSourceFactory
{
    public static BitmapSource FromImageData(ImageData data)
    {
        // 根據像素格式字串或實際資料大小判斷格式
        PixelFormat pixelFormat;

        if (data.PixelFormat == "Bgr24")
        {
            pixelFormat = PixelFormats.Bgr24;
        }
        else if (data.PixelFormat == "Mono8" || data.PixelFormat == "Gray8" || data.PixelFormat.Contains("Bayer"))
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
            if (detectedBytesPerPixel == 1)
            {
                pixelFormat = PixelFormats.Gray8;
            }
            else if (detectedBytesPerPixel == 2)
            {
                pixelFormat = PixelFormats.Gray16;
            }
            else if (detectedBytesPerPixel == 3)
            {
                pixelFormat = PixelFormats.Bgr24;
            }
            else
            {
                pixelFormat = PixelFormats.Bgra32;
            }
        }

        var bytesPerPixel = (pixelFormat.BitsPerPixel + 7) / 8;

        // 優先使用 ImageData 提供的實際 stride，否則計算預設值
        int stride;
        if (data.Stride > 0)
        {
            // 使用相機提供的實際 stride（包含 padding）
            stride = data.Stride;
        }
        else
        {
            // 計算預設 stride
            stride = data.Width * bytesPerPixel;

            // 嘗試對齊到 4 的倍數（WPF 建議）
            var alignedStride = (stride + 3) / 4 * 4;

            // 但如果對齊後的 stride 會導致預期大小超過實際緩衝區大小，就使用原始 stride
            // 這種情況常見於 Line Scan 模式，相機沒有添加 row padding
            var expectedSize = alignedStride * data.Height;
            if (expectedSize <= data.Bytes.Length)
            {
                stride = alignedStride;
            }
            // 否則保持原始 stride（不對齊）
        }


        return BitmapSource.Create(
            data.Width,
            data.Height,
            96,
            96,
            pixelFormat,
            null,
            data.Bytes,
            stride);
    }

    public static ImageData FromBitmapSource(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        var stride = source.PixelWidth * (source.Format.BitsPerPixel / 8);
        var buffer = new byte[stride * source.PixelHeight];
        source.CopyPixels(buffer, stride, 0);
        return new ImageData(buffer, source.PixelWidth, source.PixelHeight, "Bgra32");
    }
}
