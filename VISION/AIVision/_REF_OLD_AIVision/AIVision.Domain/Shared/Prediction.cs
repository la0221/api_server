using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace AIVision.Domain.Shared;

/// <summary>
/// 單一瑕疵偵測框資訊。
/// </summary>
public sealed record Detection(string Label, RectangleF BoundingBox, float Confidence);

/// <summary>
/// Workflow 分割模型回傳的瑕疵資訊
/// </summary>
public sealed class WorkflowDefect
{
    /// <summary>
    /// 瑕疵來源 Block 名稱 (如 "tf-04-segmentation")
    /// </summary>
    public string SourceBlock { get; init; } = "";

    /// <summary>
    /// 瑕疵類別名稱 (如 "gouge", "TF_crash")
    /// </summary>
    public string ClassName { get; init; } = "";

    /// <summary>
    /// 瑕疵面積（像素數）- 用於判斷是否有缺陷
    /// </summary>
    public IReadOnlyList<int> Areas { get; init; } = Array.Empty<int>();

    /// <summary>
    /// 瑕疵輪廓座標點
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ContourPoint>> Contours { get; init; }
        = Array.Empty<IReadOnlyList<ContourPoint>>();

    /// <summary>
    /// Workflow 圖片 ID
    /// </summary>
    public string? WorkflowImageId { get; init; }

    /// <summary>
    /// 原始圖片名稱
    /// </summary>
    public string? ImageName { get; init; }

    /// <summary>
    /// 是否有缺陷（Area 陣列有任何 > 0 的值）
    /// </summary>
    public bool HasDefect => Areas.Any(a => a > 0);

    /// <summary>
    /// 總瑕疵面積
    /// </summary>
    public int TotalArea => Areas.Sum();
}

/// <summary>
/// 輪廓座標點
/// </summary>
public readonly record struct ContourPoint(int X, int Y);

/// <summary>
/// AI 推論輸出記錄。
/// </summary>
public sealed record Prediction
{
    public string Label { get; }
    public float Confidence { get; }
    public bool IsOk { get; }
    public string ModelVersion { get; }
    public string? ImagePath { get; }
    public IReadOnlyList<Detection> Detections { get; }

    /// <summary>
    /// Workflow 分割瑕疵結果（僅 Workflow 模式有值）
    /// </summary>
    public IReadOnlyList<WorkflowDefect>? WorkflowDefects { get; init; }

    public Prediction(
        string label,
        float confidence,
        bool isOk,
        string modelVersion,
        string? imagePath,
        IReadOnlyList<Detection>? detections = null)
    {
        Label = label;
        Confidence = confidence;
        IsOk = isOk;
        ModelVersion = modelVersion;
        ImagePath = imagePath;
        Detections = detections ?? Array.Empty<Detection>();
    }
}
