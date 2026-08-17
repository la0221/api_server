using System.Collections.Generic;

namespace AIVision.Infrastructure.Configs;

/// <summary>
/// AI 推論服務設定
/// </summary>
public sealed class AiSettings
{
    /// <summary>
    /// 推論模式：classification | segmentation
    /// </summary>
    public string Mode { get; set; } = "classification";

    /// <summary>
    /// 模型服務基礎 URL（如 http://192.168.1.95:8001）
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// API 端點配置
    /// </summary>
    public AiEndpoints Endpoints { get; set; } = new();

    /// <summary>
    /// HTTP 客戶端設定
    /// </summary>
    public AiHttpSettings Http { get; set; } = new();

    /// <summary>
    /// 檔案上傳設定
    /// </summary>
    public AiUploadSettings Upload { get; set; } = new();

    /// <summary>
    /// Overlay 疊圖設定
    /// </summary>
    public AiOverlaySettings Overlay { get; set; } = new();
}

public sealed class AiEndpoints
{
    public string Classification { get; set; } = "/infer/cls";
    public string Segmentation { get; set; } = "/infer/seg";
}

public sealed class AiHttpSettings
{
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class AiUploadSettings
{
    public string FormFieldName { get; set; } = "img";
    public string FileNameParam { get; set; } = "file";
}

public sealed class AiOverlaySettings
{
    public double Alpha { get; set; } = 0.45;
    public Dictionary<string, string> ClassColors { get; set; } = new();
}

