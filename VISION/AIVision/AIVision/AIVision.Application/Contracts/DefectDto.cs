namespace AIVision.Application.Contracts;

/// <summary>
/// 推論結果中的瑕疵信息傳輸對象
/// </summary>
public sealed record DefectDto(
    string Type,
    float? Confidence,
    BoundingBoxDto? BoundingBox,
    string? Severity);
