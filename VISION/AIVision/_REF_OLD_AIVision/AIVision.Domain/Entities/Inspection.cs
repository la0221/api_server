using AIVision.Domain.Abstractions;

namespace AIVision.Domain.Entities;

/// <summary>
/// 每次檢測結果資訊，連結工單與模型版本。
/// </summary>
public sealed class Inspection : Entity<Guid>
{
    public Inspection(
        Guid workOrderId,
        string result,
        float? confidence,
        string? imagePath,
        string modelVersion,
        string? annotatedImagePath = null,
        int? inferenceTimeMs = null,
        IReadOnlyList<Defect>? defects = null,
        Guid? id = null,
        DateTime? occurredAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new ArgumentException("檢測結果不可為空白。", nameof(result));
        }

        if (string.IsNullOrWhiteSpace(modelVersion))
        {
            throw new ArgumentException("模型版本不可為空白。", nameof(modelVersion));
        }

        WorkOrderId = workOrderId;
        Result = result;
        Confidence = confidence;
        ImagePath = imagePath;
        ModelVersion = modelVersion;
        AnnotatedImagePath = annotatedImagePath;
        InferenceTimeMs = inferenceTimeMs;
        Defects = defects;
        Id = id ?? Guid.NewGuid();
        At = occurredAtUtc ?? DateTime.UtcNow;
    }

    public Guid WorkOrderId { get; }

    public string Result { get; }

    public float? Confidence { get; }

    public string? ImagePath { get; }

    public string ModelVersion { get; }

    public string? AnnotatedImagePath { get; }

    public int? InferenceTimeMs { get; }

    public IReadOnlyList<Defect>? Defects { get; }

    public DateTime At { get; }
}
