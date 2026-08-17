using AIVision.Domain.Shared;
using AIVision.MoldCode.Onnx;
using OpenCvSharp;

namespace AIVision.Application.Tests.MoldCode;

/// <summary>
/// 驗證 R2 BLOCKER 修正:FromImageData 對「編碼影像位元組」(離線/批量傳的 JPEG/PNG)會先解碼,
/// 而非當成原始像素(否則尺寸不符會丟例外 → 永遠辨識失敗)。
/// </summary>
public class MoldCodePreprocessorDecodeTests
{
    [Fact]
    public void FromImageData_JpegBytes_AreDecodedToBgr()
    {
        using var src = new Mat(40, 60, MatType.CV_8UC3, new Scalar(120, 130, 140));
        Cv2.ImEncode(".jpg", src, out var jpgBytes);

        // 模擬離線/批量:傳壓縮位元組 + PixelFormat="Jpeg",Width/Height 未知(0)。
        var img = new ImageData(jpgBytes, 0, 0, "Jpeg", 0);

        using var mat = MoldCodePreprocessor.FromImageData(img);

        Assert.False(mat.Empty());
        Assert.Equal(60, mat.Width);
        Assert.Equal(40, mat.Height);
        Assert.Equal(3, mat.Channels());
    }

    [Fact]
    public void FromImageData_EncodedByMagic_EvenWithoutFormatHint()
    {
        using var src = new Mat(32, 48, MatType.CV_8UC3, new Scalar(10, 20, 30));
        Cv2.ImEncode(".png", src, out var pngBytes);

        // PixelFormat 留空,僅靠位元組魔數(PNG 89 50 4E 47)判斷為編碼影像。
        var img = new ImageData(pngBytes, 0, 0, "", 0);

        using var mat = MoldCodePreprocessor.FromImageData(img);

        Assert.False(mat.Empty());
        Assert.Equal(48, mat.Width);
        Assert.Equal(32, mat.Height);
    }

    [Fact]
    public void FromImageData_RawBgr24_PassesThroughWithoutDecode()
    {
        // 相機路徑:原始 Bgr24 像素,不可被誤當編碼檔。
        const int w = 8, h = 4;
        var raw = new byte[w * h * 3];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 256);
        var img = new ImageData(raw, w, h, "Bgr24", 0);

        using var mat = MoldCodePreprocessor.FromImageData(img);

        Assert.False(mat.Empty());
        Assert.Equal(w, mat.Width);
        Assert.Equal(h, mat.Height);
        Assert.Equal(3, mat.Channels());
    }
}
