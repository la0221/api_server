namespace AIVision.Infrastructure.AiService;

/// <summary>
/// 推論結果記錄模型。
/// </summary>
/// <param name="Timestamp">推論時間戳</param>
/// <param name="ModelName">模型名稱</param>
/// <param name="Port">模型埠號</param>
/// <param name="FileName">檔案名稱</param>
/// <param name="PredClass">預測類別</param>
/// <param name="Confidence">信心度</param>
/// <param name="RawJson">原始回應 JSON</param>
public sealed record PredictResult(
    DateTime Timestamp,
    string ModelName,
    int Port,
    string FileName,
    string? PredClass,
    double? Confidence,
    string RawJson);

