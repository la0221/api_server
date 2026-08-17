namespace AIVision.Application.Contracts;

/// <summary>
/// 模型載入結果。
/// </summary>
/// <param name="Success">是否成功</param>
/// <param name="Message">結果訊息</param>
public sealed record ModelLoadResult(
    bool Success,
    string? Message);

