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
        DateTime? occurredAtUtc = null,
        string? expectedCode = null,
        string? readCode = null,
        string? outcome = null,
        bool? airBlown = null)
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
        ExpectedCode = expectedCode;
        ReadCode = readCode;
        Outcome = outcome;
        AirBlown = airBlown;
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

    /// <summary>操作員預期模號(模號核對站專用,例 "M101/07")。</summary>
    public string? ExpectedCode { get; }

    /// <summary>模型投票後讀到的模號(可能 null)。</summary>
    public string? ReadCode { get; }

    /// <summary>三態核對結果字串(Match / TrustInput / MixedAlarm / Skip)。</summary>
    public string? Outcome { get; }

    /// <summary>是否觸發氣吹剔除(混料警報)。</summary>
    public bool? AirBlown { get; }

    public DateTime At { get; }
}
