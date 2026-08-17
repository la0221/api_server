using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.ImageBatch;

/// <summary>
/// AI 推論 Port（用於圖片點檢的 AI 推論服務）
/// </summary>
public interface IAiInferencePort
{
    /// <summary>
    /// 執行 AI 推論，支援分類和分割兩種模式
    /// </summary>
    /// <param name="image">輸入圖片數據</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>推論結果（包含分類或分割數據）</returns>
    Task<AiResult> PredictAsync(ImageData image, CancellationToken ct = default);
}

/// <summary>
/// AI 推論輸出結果（支援分類、分割和 Workflow 多步驟）
/// </summary>
public sealed class AiResult
{
    /// <summary>
    /// 預測的類別（分類模式用）
    /// </summary>
    public string? PredictedClass { get; init; }

    /// <summary>
    /// 信心分數 0.0~1.0（分類模式用）
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>
    /// 瑕疵分割遮罩清單（分割模式用）
    /// </summary>
    public IReadOnlyList<SegMask>? SegmentationMasks { get; init; }

    /// <summary>
    /// 物件偵測結果清單（Workflow 物件偵測用）
    /// </summary>
    public IReadOnlyList<DetectionResult>? Detections { get; init; }

    /// <summary>
    /// 是否為 OK 判定（無缺陷）
    /// </summary>
    public bool IsOk { get; init; } = true;

    /// <summary>
    /// 缺陷總數
    /// </summary>
    public int DefectCount { get; init; }

    /// <summary>
    /// 原始 JSON 回應（除錯用）
    /// </summary>
    public string? RawJson { get; init; }
}

/// <summary>
/// 單個瑕疵類別的分割遮罩
/// </summary>
public sealed class SegMask
{
    /// <summary>
    /// 缺陷類別名稱（如 scratch, stain）
    /// </summary>
    public string ClassName { get; set; } = "";

    /// <summary>
    /// PNG 格式的二值遮罩（白=缺陷，黑=背景）
    /// </summary>
    public byte[] PngBytes { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// 遮罩圖片寬度（像素）
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// 遮罩圖片高度（像素）
    /// </summary>
    public int? Height { get; set; }

    /// <summary>
    /// 在原圖上的 Bounding Box (用於定位 mask 位置)
    /// </summary>
    public BoundingBox? Bbox { get; set; }

    /// <summary>
    /// 信心分數 (0.0~1.0)
    /// </summary>
    public double? Confidence { get; set; }
}

/// <summary>
/// Bounding Box 座標
/// </summary>
public sealed class BoundingBox
{
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }

    public int Width => X2 - X1;
    public int Height => Y2 - Y1;
}

/// <summary>
/// 物件偵測結果
/// </summary>
public sealed class DetectionResult
{
    /// <summary>
    /// 缺陷類別名稱
    /// </summary>
    public string ClassName { get; set; } = "";

    /// <summary>
    /// 信心分數 (0.0~1.0)
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Bounding Box 座標
    /// </summary>
    public BoundingBox Bbox { get; set; } = new();
}

