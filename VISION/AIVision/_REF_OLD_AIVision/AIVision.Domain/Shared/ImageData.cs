namespace AIVision.Domain.Shared;

/// <summary>
/// 代表原生影像資料與尺寸描述。
/// </summary>
/// <param name="Bytes">原始影像資料</param>
/// <param name="Width">影像寬度（像素）</param>
/// <param name="Height">影像高度（像素）</param>
/// <param name="PixelFormat">像素格式（如 Mono8, Bgr24）</param>
/// <param name="Stride">每行的實際位元組數（包含 padding），0 表示使用預設計算</param>
public readonly record struct ImageData(byte[] Bytes, int Width, int Height, string PixelFormat, int Stride = 0);
