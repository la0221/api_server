using System.IO;
using System.Runtime.InteropServices;
using AIVision.Domain.Shared;
using OpenCvSharp;

namespace AIVision.MoldCode.Harness;

/// <summary>離線用:jpg(含中文路徑,經 byte 解碼) → ImageData(Bgr24)。</summary>
internal static class ImageLoad
{
    public static ImageData Load(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        using Mat bgr = Cv2.ImDecode(raw, ImreadModes.Color);
        Mat cont = bgr.IsContinuous() ? bgr : bgr.Clone();
        int len = (int)(cont.Total() * cont.ElemSize());
        var px = new byte[len];
        Marshal.Copy(cont.Data, px, 0, len);
        if (!ReferenceEquals(cont, bgr))
            cont.Dispose();
        return new ImageData(px, bgr.Width, bgr.Height, "Bgr24", 0);
    }
}
