namespace AIVision.Application.Contracts;

using AIVision.Domain.Shared;

/// <summary>
/// 檢測結果傳輸對象
/// </summary>
public sealed record InspectionResultDto(
    Guid Id,
    string Result,
    float? Confidence,
    int? InferenceTimeMs,
    IReadOnlyList<DefectDto>? Defects,
    ImageData? OriginalImage,
    ImageData? AnnotatedImage);
