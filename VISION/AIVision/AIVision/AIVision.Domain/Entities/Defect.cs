using AIVision.Domain.Abstractions;

namespace AIVision.Domain.Entities;

/// <summary>
/// 瑕疵資訊，描述檢測到的缺陷細節。
/// </summary>
public sealed class Defect : Entity<Guid>
{
    public Defect(
        string type,
        float? confidence = null,
        BoundingBox? boundingBox = null,
        string? severity = null,
        Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("瑕疵類型不可為空白。", nameof(type));
        }

        Type = type;
        Confidence = confidence;
        BoundingBox = boundingBox;
        Severity = severity;
        Id = id ?? Guid.NewGuid();
    }

    /// <summary>瑕疵類型 (例如: misalign, residual, short, bridging, winding)</summary>
    public string Type { get; }

    /// <summary>信心分數 (0.0 - 1.0)</summary>
    public float? Confidence { get; }

    /// <summary>瑕疵邊界框座標</summary>
    public BoundingBox? BoundingBox { get; }

    /// <summary>嚴重程度 (Low, Medium, High)</summary>
    public string? Severity { get; }
}

/// <summary>
/// 瑕疵邊界框座標。
/// </summary>
public sealed class BoundingBox
{
    public BoundingBox(int x, int y, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentException("寬度必須大於 0", nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentException("高度必須大於 0", nameof(height));
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
}
