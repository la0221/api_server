namespace AIVision.Application.Contracts;

/// <summary>
/// 瑕疵邊界框信息
/// </summary>
public sealed record BoundingBoxDto(
    int X,
    int Y,
    int Width,
    int Height);
